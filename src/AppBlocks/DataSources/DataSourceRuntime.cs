using System.Collections.Concurrent;
using System.Data.Common;
using AppBlocks.Connections;

namespace AppBlocks.DataSources;

/// <summary>
/// What every data source shares: the application connections and dialects, the plan cache, the create
/// receipts and the clock. Plans are cached by definition fingerprint for a minute, so a published change takes
/// effect at once and a change to the database schema within a minute; a failure is retried after five seconds.
/// </summary>
public sealed class DataSourceRuntime(IApplicationConnectionFactory connections, ILogger<DataSourceRuntime> logger,
    TimeProvider? time = null, ConnectionCatalog? managed = null)
{
    private static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ProblemLifetime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefinitionLifetime = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, PlanEntry> plans = new();
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
    internal DataSourceReceipts Receipts { get; } = new(time ?? TimeProvider.System);
    /// <summary>The web server's local time, which <c>:NOW</c> binds and the audit columns record.</summary>
    internal DateTime Now() => clock.GetLocalNow().DateTime;

    internal sealed record PlanEntry(DataSourcePlan? Plan, string? Problem, DateTimeOffset Expires);

    internal PlanEntry Plan(DataSourceDefinition definition)
    {
        var key = definition.ComputeFingerprint() + ":" + managed?.Revision(definition.Connection);
        var now = clock.GetUtcNow();
        if (plans.TryGetValue(key, out var cached) && cached.Expires > now) return cached;
        var errors = new List<string>();
        var plan = Build(definition, errors, compileStatements: false);
        var entry = plan is not null ? new PlanEntry(plan, null, now + PlanLifetime)
            : new PlanEntry(null, errors.Count > 0 ? string.Join(" ", errors) : "The data source could not be prepared.", now + ProblemLifetime);
        if (plan is null) logger.LogWarning("Data source {Alias} is not available: {Problem}", definition.Alias, entry.Problem);
        if (plans.Count > 500) foreach (var stale in plans.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToArray()) plans.TryRemove(stale, out _);
        return plans[key] = entry;
    }

    /// <summary>
    /// Everything publishing checks against the database: the connection, the row source, the restriction, the key
    /// and version columns, every column and bind variable the definition names, and that custom statements compile.
    /// </summary>
    public IReadOnlyList<string> Validate(DataSourceDefinition definition)
    {
        var errors = new List<string>();
        if (Build(definition, errors, compileStatements: true) is null && errors.Count == 0) errors.Add("The data source could not be prepared.");
        return errors;
    }

    /// <summary>Authoring tests inspect the current schema, without reusing a cached application plan.</summary>
    internal DataSourcePlan? Inspect(DataSourceDefinition definition, List<string> errors)
        => Build(definition, errors, compileStatements: true);

    private DataSourcePlan? Build(DataSourceDefinition definition, List<string> errors, bool compileStatements)
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
            return DataSourcePlanner.Build(definition, dialect, connection, errors, compileStatements);
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
        catch (InvalidOperationException)
        {
            errors.Add($"The connection {connection} is not registered. Choose a managed connection or register it with AddAppBlocks.");
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
/// Remembers successful creates by submission key for fifteen minutes, so a retried request returns the record it
/// created instead of inserting it twice, and a reused key with different values is refused. Creates with the same
/// key wait for each other. The receipts live in memory: the browser makes a new key after every successful save,
/// so a receipt only has to outlast a retry, and an application database needs no receipts table of its own.
/// </summary>
internal sealed class DataSourceReceipts(TimeProvider clock)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, (string Key, string Hash, DateTimeOffset Expires)> receipts = new();
    private readonly SemaphoreSlim[] gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public async Task<IDisposable> EnterAsync(string submission, CancellationToken ct)
    {
        var gate = gates[Gate(submission)];
        await gate.WaitAsync(ct);
        return new Release(gate);
    }

    /// <summary>
    /// Enters the gates of several submissions at once, as a master/detail save does, each gate once and in one order, so two
    /// saves that share a gate wait for each other rather than for themselves.
    /// </summary>
    public async Task<IDisposable> EnterAsync(IEnumerable<string> submissions, CancellationToken ct)
    {
        var entered = new List<IDisposable>();
        try
        {
            foreach (var index in submissions.Select(Gate).Distinct().Order())
            {
                await gates[index].WaitAsync(ct);
                entered.Add(new Release(gates[index]));
            }
            return new Releases(entered);
        }
        catch { new Releases(entered).Dispose(); throw; }
    }

    private int Gate(string submission) => (submission.GetHashCode(StringComparison.Ordinal) & int.MaxValue) % gates.Length;

    public (string Key, string Hash)? Find(string submission)
        => receipts.TryGetValue(submission, out var receipt) && receipt.Expires > clock.GetUtcNow() ? (receipt.Key, receipt.Hash) : null;

    public void Add(string submission, string key, string hash)
    {
        var now = clock.GetUtcNow();
        if (receipts.Count > 10_000) foreach (var stale in receipts.Where(r => r.Value.Expires <= now).Select(r => r.Key).ToArray()) receipts.TryRemove(stale, out _);
        receipts[submission] = (key, hash, now + Lifetime);
    }

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private int released;
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) gate.Release(); }
    }

    private sealed class Releases(IReadOnlyList<IDisposable> entered) : IDisposable
    {
        public void Dispose() { foreach (var release in entered.Reverse()) release.Dispose(); }
    }
}
