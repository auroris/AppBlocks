using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.Extensions.Options;
using AppBlocks.Connections;

namespace AppBlocks.DataSources;

/// <summary>
/// What every data source shares: the application connections and dialects, the plan cache, the create
/// receipts and the clock. Plans are cached by definition fingerprint for a minute, so a published change takes
/// effect at once and a change to the database schema within about a minute, rebuilt in the background while the
/// expired plan serves; a failure is retried after five seconds.
/// </summary>
public sealed class DataSourceRuntime(IApplicationConnectionFactory connections, ILogger<DataSourceRuntime> logger,
    TimeProvider? time = null, ConnectionCatalog? managed = null, IOptions<DataSourceOptions>? options = null, IDataSourceReceiptStore? receipts = null)
{
    private static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ProblemLifetime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefinitionLifetime = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, PlanEntry> plans = new();
    private readonly ConcurrentDictionary<string, Lazy<PlanEntry>> building = new();
    private readonly ConcurrentDictionary<(Type Kind, Guid Key, DateTime Updated, string Dependencies), (object? Definition, DateTimeOffset Expires)> definitions = new();
    private readonly TimeProvider clock = time ?? TimeProvider.System;

    /// <summary>
    /// A published node's definition, read once per published version and per version of what it reads besides
    /// the node, given by <paramref name="dependencies"/>: a report's data source, for one. Kept five minutes, so
    /// a renamed member group reaches the definition without a republish; <paramref name="read"/> reports and
    /// logs its own problems.
    /// </summary>
    internal T? Definition<T>(Guid key, DateTime updated, Func<T?> read, string dependencies = "") where T : class
    {
        var now = clock.GetUtcNow();
        if (definitions.TryGetValue((typeof(T), key, updated, dependencies), out var cached) && cached.Expires > now) return (T?)cached.Definition;
        if (definitions.Count > 1000) foreach (var stale in definitions.Where(d => d.Value.Expires <= now).Select(d => d.Key).ToArray()) definitions.TryRemove(stale, out _);
        var definition = read();
        definitions[(typeof(T), key, updated, dependencies)] = (definition, now + DefinitionLifetime);
        return definition;
    }

    internal ILogger Logger => logger;
    internal DataSourceOptions Options { get; } = options?.Value ?? new();
    internal IDataSourceReceiptStore Receipts { get; } = receipts ?? new DataSourceReceipts(time, options);
    /// <summary>
    /// The current moment with the web server's offset. Its local wall time is what <c>:NOW</c> binds and the audit
    /// columns record; its UTC time is what <c>:NOW_UTC</c> binds. Both come from one reading, so they agree in the
    /// autumn hour that repeats.
    /// </summary>
    internal DateTimeOffset Now() => clock.GetLocalNow();

    /// <summary>
    /// The current moment as a data source's date-times hold it: with the web server's offset, or at UTC for a source that keeps UTC, so
    /// that <c>:NOW</c> and the audit columns write UTC wall time there. <c>:NOW_UTC</c> is the same moment either way.
    /// </summary>
    internal DateTimeOffset Now(DataSourceDefinition definition) => As(definition, clock.GetUtcNow());

    /// <summary>One clock reading as <paramref name="definition"/>'s date-times hold it, for a save that writes to several data sources.</summary>
    internal DateTimeOffset As(DataSourceDefinition definition, DateTimeOffset moment)
        => definition.DateTimes == DataSourceDateTimes.Utc ? moment.ToUniversalTime() : TimeZoneInfo.ConvertTime(moment, clock.LocalTimeZone);

    internal sealed record PlanEntry(DataSourcePlan? Plan, string? Problem, DateTimeOffset Expires);

    /// <summary>
    /// The plan for a definition. One build runs at a time for each definition and connection revision, and callers that ask meanwhile
    /// share it. A working plan whose minute has passed keeps serving while one rebuild runs in the background, so no request waits for
    /// the schema to be read again; a definition with no plan yet, or with a problem past its five seconds, is built while its callers wait.
    /// </summary>
    internal PlanEntry Plan(DataSourceDefinition definition)
    {
        var key = definition.ComputeFingerprint() + ":" + managed?.Revision(definition.Connection);
        if (plans.TryGetValue(key, out var cached))
        {
            if (cached.Expires > clock.GetUtcNow()) return cached;
            if (cached.Plan is not null)
            {
                var rebuild = Building(key, definition, out var started);
                if (started) _ = Task.Run(() => Finish(key, rebuild));
                return cached;
            }
        }
        var build = Building(key, definition, out var mine);
        try { return build.Value; }
        finally { if (mine) building.TryRemove(new KeyValuePair<string, Lazy<PlanEntry>>(key, build)); }
    }

    /// <summary>The build running for a key, or a new one that this caller <paramref name="started"/>.</summary>
    private Lazy<PlanEntry> Building(string key, DataSourceDefinition definition, out bool started)
    {
        var mine = new Lazy<PlanEntry>(() => Store(key, definition), LazyThreadSafetyMode.ExecutionAndPublication);
        var build = building.GetOrAdd(key, mine);
        started = ReferenceEquals(build, mine);
        return build;
    }

    private void Finish(string key, Lazy<PlanEntry> build)
    {
        try { _ = build.Value; }
        finally { building.TryRemove(new KeyValuePair<string, Lazy<PlanEntry>>(key, build)); }
    }

    /// <summary>Builds a plan and keeps it, or the problem that stopped it; an entry another build stored meanwhile is kept instead.</summary>
    private PlanEntry Store(string key, DataSourceDefinition definition)
    {
        var now = clock.GetUtcNow();
        if (plans.TryGetValue(key, out var fresh) && fresh.Expires > now) return fresh;
        var errors = new List<string>();
        var plan = Build(definition, errors, compileStatements: false);
        now = clock.GetUtcNow();
        var entry = plan is not null ? new PlanEntry(plan, null, now + PlanLifetime)
            : new PlanEntry(null, errors.Count > 0 ? string.Join(" ", errors) : "The data source could not be prepared.", now + ProblemLifetime);
        if (plan is null) logger.LogWarning("Data source {Alias} is not available: {Problem}", definition.Alias, entry.Problem);
        if (plans.Count > 500) foreach (var stale in plans.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToArray()) plans.TryRemove(stale, out _);
        return plans[key] = entry;
    }

    /// <summary>
    /// Everything publishing checks against the database: the connection, the row source, the restriction, the key
    /// columns, every column and bind variable the definition names, and that custom statements compile.
    /// </summary>
    public IReadOnlyList<string> Validate(DataSourceDefinition definition) => Validate(definition, new List<string>());

    /// <summary>The checks of <see cref="Validate(DataSourceDefinition)"/>, with what publishing reports without refusing in <paramref name="warnings"/>.</summary>
    public IReadOnlyList<string> Validate(DataSourceDefinition definition, ICollection<string> warnings)
    {
        var errors = new List<string>();
        if (Build(definition, errors, compileStatements: true, warnings) is null && errors.Count == 0) errors.Add("The data source could not be prepared.");
        return errors;
    }

    /// <summary>Authoring tests inspect the current schema, without reusing a cached application plan.</summary>
    internal DataSourcePlan? Inspect(DataSourceDefinition definition, List<string> errors, ICollection<string>? warnings = null)
        => Build(definition, errors, compileStatements: true, warnings);

    private DataSourcePlan? Build(DataSourceDefinition definition, List<string> errors, bool compileStatements, ICollection<string>? warnings = null)
    {
        var start = errors.Count;
        definition.ValidateAccess(errors);
        if (errors.Count != start) return null;
        if (Dialect(definition.Connection, errors) is not { } dialect) return null;
        try
        {
            using var connection = connections.CreateConnection(definition.Connection);
            connection.Open();
            if (dialect.Kind == DatabaseDialect.Oracle && int.TryParse(connection.ServerVersion.Split('.')[0], out var major) && major < 12)
            {
                dialect = OracleDialect.Legacy;
                connections.SetDetectedDialect(definition.Connection, DatabaseDialect.Oracle11g);
            }
            return DataSourcePlanner.Build(definition, dialect, connection, errors, compileStatements, warnings, Options.CommandTimeoutSeconds);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Whatever a provider or catalog throws becomes the data source's problem, reported and retried,
            // rather than a failure of every page and catalogue request that touches it.
            logger.LogWarning(error, "Data source {Alias} could not read its schema through {Connection}.", definition.Alias, definition.Connection);
            errors.Add($"The schema could not be read through connection {definition.Connection}: {error.Message}");
            return null;
        }
    }

    /// <summary>The dialect resolved for a registered application connection.</summary>
    internal SqlDialect? Dialect(string connection, ICollection<string> errors)
    {
        DatabaseDialect? dialect;
        try { dialect = connections.Dialect(connection); }
        catch (ConnectionNotRegisteredException)
        {
            errors.Add($"The connection {connection} is not registered. Choose a managed connection or register it with AddAppBlocks.");
            return null;
        }
        catch (InvalidOperationException error)
        {
            // A driver the site has not installed, or a provider AppBlocks does not know: the reason is the message.
            errors.Add($"Connection {connection} cannot be used: {error.Message}");
            return null;
        }
        if (dialect is { } known && SqlDialect.For(known) is { } result) return result;
        errors.Add($"Connection {connection} uses {dialect?.ToString() ?? "an unknown database"}; supported data sources are SQLite, SQL Server, PostgreSQL and Oracle.");
        return null;
    }

    internal async Task<DbConnection> OpenAsync(string name, CancellationToken ct)
    {
        var connection = connections.CreateConnection(name);
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
}

/// <summary>
/// The default <see cref="IDataSourceReceiptStore"/>: receipts in this process's memory for <see cref="DataSourceOptions.ReceiptLifetimeMinutes"/>,
/// and creates with the same key waiting for each other on one of 64 gates. Memory is enough on one web server: the browser makes a new key
/// after every successful save, so a receipt only has to outlast a retry, and an application database needs no receipts table of its own.
/// </summary>
internal sealed class DataSourceReceipts(TimeProvider? time = null, IOptions<DataSourceOptions>? options = null) : IDataSourceReceiptStore
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly DataSourceOptions settings = options?.Value ?? new();
    private readonly ConcurrentDictionary<string, (DataSourceReceipt Receipt, DateTimeOffset Expires)> receipts = new();
    private readonly SemaphoreSlim[] gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    /// <summary>
    /// Enters the gate of each submission once and in one order, as a master/detail save needs, so two saves that share a gate wait for
    /// each other rather than for themselves.
    /// </summary>
    public async Task<IAsyncDisposable> EnterAsync(IReadOnlyCollection<string> submissions, CancellationToken ct)
    {
        var entered = new List<SemaphoreSlim>();
        try
        {
            foreach (var index in submissions.Select(Gate).Distinct().Order())
            {
                await gates[index].WaitAsync(ct);
                entered.Add(gates[index]);
            }
            return new Releases(entered);
        }
        catch { await new Releases(entered).DisposeAsync(); throw; }
    }

    private int Gate(string submission) => (submission.GetHashCode(StringComparison.Ordinal) & int.MaxValue) % gates.Length;

    public ValueTask<DataSourceReceipt?> FindAsync(string submission, CancellationToken ct)
        => new(receipts.TryGetValue(submission, out var entry) && entry.Expires > clock.GetUtcNow() ? entry.Receipt : null);

    public ValueTask AddAsync(string submission, DataSourceReceipt receipt, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (receipts.Count > settings.ReceiptCapacity) foreach (var stale in receipts.Where(r => r.Value.Expires <= now).Select(r => r.Key).ToArray()) receipts.TryRemove(stale, out _);
        receipts[submission] = (receipt, now + TimeSpan.FromMinutes(settings.ReceiptLifetimeMinutes));
        return ValueTask.CompletedTask;
    }

    private sealed class Releases(IReadOnlyList<SemaphoreSlim> entered) : IAsyncDisposable
    {
        private int released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) foreach (var gate in entered.Reverse()) gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
