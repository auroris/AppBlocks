using System.Data.Common;
using System.Globalization;
using System.Reflection;
using AppBlocks.Connections;
using AppBlocks.Forms;

namespace AppBlocks.DataSources;

/// <summary>
/// A data source's backing class: application code an editor picks on a <c>sqlDataSource</c> node by its <see cref="Alias"/>,
/// which the engine asks at each step of an operation once its own checks have passed, around the row it reads or writes itself.
/// A hook may rewrite the values that are written or shown, write the row itself through <see cref="DataSourceOperation.Sql"/> inside
/// the engine's transaction, or refuse. Every hook passes through by default, so a class
/// implements only the ones it needs. Register one with <c>AddScoped&lt;IDataSourceHandler, YourHandler&gt;()</c>.
/// </summary>
/// <remarks>
/// The engine checks the caller, the groups and the allow flags before it asks the class, asks every
/// <see cref="IDataSourcePolicy"/> after it, and reads a written row back through the row restriction, so a class cannot widen
/// what the data source allows. Read hooks run without a transaction and must not write.
/// </remarks>
public interface IDataSourceHandler
{
    /// <summary>The name an editor picks: letters, digits, '.', '-' and '_', starting with a letter.</summary>
    string Alias { get; }

    /// <summary>A new record's starting values, after the insert defaults; <see cref="DataSourceOperation.Values"/> is what the form shows.</summary>
    ValueTask<DataSourceOutcome> InitializeAsync(DataSourceOperation operation, CancellationToken ct) => DataSourceOutcome.Pass;

    /// <summary>A record read by key through the restriction; <see cref="DataSourceOperation.Values"/> is what the form shows.</summary>
    ValueTask<DataSourceOutcome> LoadAsync(DataSourceOperation operation, CancellationToken ct) => DataSourceOutcome.Pass;

    /// <summary>A new row, after the submitted values are parsed and the defaults filled; <see cref="DataSourceOutcome.Handled"/> returns its key.</summary>
    ValueTask<DataSourceOutcome> CreateAsync(DataSourceOperation operation, CancellationToken ct) => DataSourceOutcome.Pass;

    /// <summary>A changed row, locked, after any enabled checksum check; <see cref="DataSourceOperation.Values"/> is the whole row as it would be stored.</summary>
    ValueTask<DataSourceOutcome> UpdateAsync(DataSourceOperation operation, CancellationToken ct) => DataSourceOutcome.Pass;

    /// <summary>A row about to be deleted, locked, after any enabled checksum check.</summary>
    ValueTask<DataSourceOutcome> DeleteAsync(DataSourceOperation operation, CancellationToken ct) => DataSourceOutcome.Pass;

    /// <summary>The rows of one parent record, as a tabular form lists them; <see cref="DataSourceOperation.Rows"/> is what it shows.</summary>
    ValueTask<DataSourceOutcome> ListAsync(DataSourceOperation operation, CancellationToken ct) => DataSourceOutcome.Pass;

    /// <summary>
    /// A report, API endpoint, list of values, schedule or paper report about to read the data source. Only a refusal counts: to answer
    /// the read with rows of the class's own, use <see cref="ReadAsync"/>.
    /// </summary>
    ValueTask<DataSourceOutcome> QueryAsync(DataSourceOperation operation, CancellationToken ct) => DataSourceOutcome.Pass;

    /// <summary>
    /// Rows in place of the row source's, for the readers whose rows pass through AppBlocks: API endpoints, lists of values, schedules
    /// and paper reports. Return them by column name, as the row source names its columns, which also give the rows their field aliases
    /// and kinds; AppBlocks sorts them as an endpoint's Order by asks and holds them to the reader's row limit, as it does its own rows.
    /// Null, the default, reads the row source. It runs once the data source's access, <see cref="QueryAsync"/> and every policy have
    /// allowed the read, with <see cref="DataSourceOperation.Context"/> carrying the bound parameters. The row restriction is SQL, so it
    /// cannot apply to rows from code: the class applies its rule itself. An Interactive Report runs the row source's SQL itself and
    /// never asks this.
    /// </summary>
    ValueTask<IReadOnlyList<IReadOnlyDictionary<string, object?>>?> ReadAsync(DataSourceOperation operation, CancellationToken ct)
        => new((IReadOnlyList<IReadOnlyDictionary<string, object?>>?)null);
}

/// <summary>Carry on with the engine's own write, the class wrote it itself, or nothing is written.</summary>
public enum DataSourceOutcomeKind { Continue, Handled, Refused }

/// <summary>What a hook decided: carry on, the class wrote the row itself, or refuse with a status and field errors.</summary>
public sealed record DataSourceOutcome
{
    private DataSourceOutcome(DataSourceOutcomeKind kind, string? key = null, int status = 200, IReadOnlyDictionary<string, string[]>? errors = null)
        => (Kind, Key, Status, Errors) = (kind, key, status, errors);

    public DataSourceOutcomeKind Kind { get; }
    /// <summary>The key of the row a handled create wrote, in the record key format; null when the class did not say.</summary>
    public string? Key { get; }
    public int Status { get; }
    public IReadOnlyDictionary<string, string[]>? Errors { get; }

    /// <summary>The engine proceeds, with whatever the class changed in the operation's values.</summary>
    public static DataSourceOutcome Continue { get; } = new(DataSourceOutcomeKind.Continue);
    internal static ValueTask<DataSourceOutcome> Pass => new(Continue);

    /// <summary>
    /// The class wrote the row itself, so the engine writes nothing and reads the row back through the restriction. A create returns the
    /// new row's key, as a form shows it (several key columns joined by '|'), unless the operation's values already hold every key field.
    /// On a read hook this is the same as <see cref="Continue"/>.
    /// </summary>
    public static DataSourceOutcome Handled(string? key = null) => new(DataSourceOutcomeKind.Handled, key);

    /// <summary>Nothing is written, and the caller gets <paramref name="status"/>, a 4xx, with the field errors by field alias ("" for the record).</summary>
    public static DataSourceOutcome Refuse(int status = 403, IReadOnlyDictionary<string, string[]>? errors = null)
    {
        if (status is < 400 or > 499) throw new ArgumentOutOfRangeException(nameof(status), "A refusal is a 4xx status.");
        return new(DataSourceOutcomeKind.Refused, status: status, errors: errors);
    }

    /// <summary>A 422 refusal with one message against <paramref name="field"/>, as <see cref="FormResult.Invalid"/>.</summary>
    public static DataSourceOutcome Invalid(string field, string message) => Refuse(422, new Dictionary<string, string[]> { [field] = [message] });

    internal FormResult Result() => new(Status, Errors: Errors);
}

/// <summary>
/// One operation as a backing class sees it: who asks, for what, the stored and proposed values, and the engine's open connection and
/// transaction with a helper that runs the class's own SQL on them. Values are by field alias in the form's JSON shape: text, numbers as
/// invariant text, true or false, ISO dates and times. A class may set a field to any value of its kind, and the engine converts and
/// checks it as it does what a form sends.
/// </summary>
public sealed class DataSourceOperation
{
    internal DataSourceOperation(string dataSource, DataSourceAction action, DataSourceUser user, FormExecutionContext context, DatabaseDialect dialect)
        => (DataSource, Action, User, Context, Dialect) = (dataSource, action, user, context, dialect);

    /// <summary>The data source's alias.</summary>
    public string DataSource { get; }
    /// <summary>Initialize, Load, Create, Update, Delete, or Query for a tabular list and for the readers of <see cref="IDataSourceHandler.QueryAsync"/>.</summary>
    public DataSourceAction Action { get; }
    /// <summary>The anonymous visitor, Member, backoffice User or both; anonymous writes require the data source to opt in.</summary>
    public DataSourceUser User { get; }
    public FormExecutionContext Context { get; }
    /// <summary>The record's key (Load, Update, Delete); null otherwise.</summary>
    public string? Key { get; internal init; }
    /// <summary>The record as stored (Load, Update, Delete); null otherwise.</summary>
    public IReadOnlyDictionary<string, object?>? Current { get; internal init; }
    /// <summary>
    /// The values the engine is about to write (Create, Update) or show (Initialize, Load); null otherwise. Change, add or remove
    /// entries to change what is written or shown. On an update a removed field is left as stored.
    /// </summary>
    public IDictionary<string, object?>? Values { get; internal init; }
    /// <summary>The rows a tabular form is about to show, each changeable like <see cref="Values"/>; null outside a list.</summary>
    public IReadOnlyList<IDictionary<string, object?>>? Rows { get; internal init; }
    /// <summary>
    /// The detail blocks saved with this record in one submission, each with its pending changes, which a class may handle, change or
    /// refuse. Empty unless the operation is the master's create or update of such a submission.
    /// </summary>
    public IReadOnlyList<DataSourceDetail> Details { get; internal init; } = [];
    /// <summary>The engine's open connection, or null where the engine has none open yet (Initialize, a report's check); <see cref="Sql"/> opens one then.</summary>
    public DbConnection? Connection { get; internal set; }
    /// <summary>The write transaction (Create, Update, Delete); null otherwise. Never commit, roll back or dispose it: the engine owns it.</summary>
    public DbTransaction? Transaction { get; internal init; }
    public DatabaseDialect Dialect { get; }
    /// <summary>Runs the class's own statements on the engine's connection and transaction, with bound parameters.</summary>
    public DataSourceSql Sql { get; internal set; } = null!;
}

/// <summary>
/// One detail block of a master/detail submission: the tabular block, its data source, the field that links its rows to the master, and
/// the changes still to apply. Remove a change from <see cref="Changes"/> once the class has applied it itself; the engine applies the rest
/// after the master's hook, with the same checks a tabular save makes.
/// </summary>
public sealed class DataSourceDetail
{
    internal DataSourceDetail(Guid block, string dataSource, string linkField, List<DataSourceChange> changes)
        => (Block, DataSource, LinkField, Changes) = (block, dataSource, linkField, changes);

    public Guid Block { get; }
    public string DataSource { get; }
    public string LinkField { get; }
    /// <summary>The master's key as the link field holds it; null until a new master row is written.</summary>
    public string? MasterKey { get; internal set; }
    public IList<DataSourceChange> Changes { get; }
}

/// <summary>
/// The SQL a backing class runs: one statement or block, whole, as one command on the engine's connection and inside its transaction, with
/// every value bound as a parameter. It takes the bind variables an editor's custom statement does, <c>:USER_*</c>, <c>:PARAM.name</c>,
/// <c>:APP.name</c>, <c>:FIELD.alias</c> against the operation's values, <c>:NOW</c> and <c>:NOW_UTC</c>, and <c>:ARG.name</c> for a value the class passes
/// in <c>args</c>: a dictionary, or an object whose properties are the names. An Oracle anonymous block (<c>BEGIN ... END;</c>) runs as
/// written. <c>:USER_*</c> values need a signed-in account.
/// </summary>
public sealed class DataSourceSql : IAsyncDisposable
{
    private readonly DataSourcePlan plan;
    private readonly Func<BindVariable, object?> bind;
    private readonly DataSourceOperation operation;
    private readonly Func<CancellationToken, Task<DbConnection>> open;
    private DbConnection? opened;

    internal DataSourceSql(DataSourcePlan plan, DataSourceOperation operation, Func<BindVariable, object?> bind, Func<CancellationToken, Task<DbConnection>> open)
        => (this.plan, this.operation, this.bind, this.open) = (plan, operation, bind, open);

    /// <summary>The most rows <see cref="QueryAsync"/> returns unless told otherwise.</summary>
    public const int DefaultMaximumRows = 1000;

    /// <summary>Runs a statement that changes data and returns the rows it affected, as the provider counts them. Only a write may.</summary>
    public async Task<int> ExecuteAsync(string sql, object? args = null, CancellationToken ct = default)
    {
        if (operation.Transaction is null) throw new InvalidOperationException("A backing class changes data only in a create, update or delete, inside the engine's transaction.");
        await using var command = await CommandAsync(sql, args, ct);
        return await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The first column of the first row, or null.</summary>
    public async Task<object?> ScalarAsync(string sql, object? args = null, CancellationToken ct = default)
    {
        await using var command = await CommandAsync(sql, args, ct);
        return await command.ExecuteScalarAsync(ct) is { } value and not DBNull ? value : null;
    }

    /// <summary>The rows, each by column name in any letter case, as the provider returns the values; more than <paramref name="maximumRows"/> is an error.</summary>
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string sql, object? args = null, int maximumRows = DefaultMaximumRows, CancellationToken ct = default)
    {
        if (maximumRows < 1) throw new ArgumentOutOfRangeException(nameof(maximumRows));
        await using var command = await CommandAsync(sql, args, ct);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        while (await reader.ReadAsync(ct))
        {
            if (rows.Count == maximumRows) throw new DataSourceException($"The backing class's query returned more than {maximumRows} rows.");
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private async Task<DbCommand> CommandAsync(string sql, object? args, CancellationToken ct)
    {
        var parameters = Render(sql, args, out var text);
        var connection = operation.Connection ?? (operation.Connection = opened = await open(ct));
        return parameters.Command(connection, operation.Transaction, text);
    }

    /// <summary>The statement with a parameter marker for every bind variable, and the parameters bound.</summary>
    internal SqlParameters Render(string sql, object? args, out string text)
    {
        var errors = new List<string>();
        var template = SqlTemplate.Parse(sql, SqlUse.Handler, "The backing class's statement", errors) ?? throw new DataSourceException(string.Join(" ", errors));
        var arguments = Arguments(args);
        foreach (var variable in template.Variables)
        {
            if (variable.Kind is BindVariableKind.UserSid or BindVariableKind.UserName or BindVariableKind.UserKey or BindVariableKind.UserEmail or BindVariableKind.UserGroups
                && operation.User.IsAnonymous)
                throw new DataSourceException($"The backing class's statement uses {variable}, which needs a signed-in account.");
            if (variable.Kind == BindVariableKind.Arg && !arguments.ContainsKey(variable.Name!))
                throw new DataSourceException($"The backing class's statement uses {variable}, which the class did not pass.");
            if (variable.Kind == BindVariableKind.Field && plan.Field(variable.Name!) is null)
                throw new DataSourceException($"The backing class's statement uses {variable}, which is not a field.");
        }
        var parameters = plan.Parameters();
        text = template.Render(parameters, variable => variable.Kind switch {
            BindVariableKind.Arg => arguments[variable.Name!],
            BindVariableKind.Field => Field(variable.Name!),
            _ => bind(variable),
        });
        return parameters;
    }

    /// <summary>A field's value from the operation's values, or the stored record when there are none, as its column takes it.</summary>
    private object? Field(string alias)
    {
        var column = plan.Field(alias)!;
        IEnumerable<KeyValuePair<string, object?>>? values = operation.Values ?? (IEnumerable<KeyValuePair<string, object?>>?)operation.Current;
        var value = values?.FirstOrDefault(pair => pair.Key == column.Field).Value;
        var canonical = FieldValues.FromValue(value, column.Kind, out var valid, plan.Definition.DateTimes);
        if (!valid) throw new DataSourceFieldException(column.Field, "This field holds a value its kind cannot.");
        try { return plan.Dialect.ToDatabase(canonical, column.Kind, column.Target, plan.Definition.DateTimes); }
        catch (DataSourceException error) { throw new DataSourceFieldException(column.Field, error.Message); }
    }

    private static Dictionary<string, object?> Arguments(object? args)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        switch (args)
        {
            case null: break;
            case IEnumerable<KeyValuePair<string, object?>> pairs: foreach (var (name, value) in pairs) values[name] = value; break;
            case System.Collections.IDictionary dictionary:
                foreach (System.Collections.DictionaryEntry entry in dictionary) values[Convert.ToString(entry.Key, CultureInfo.InvariantCulture)!] = entry.Value;
                break;
            default:
                foreach (var property in args.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
                    values[property.Name] = property.GetValue(args);
                break;
        }
        return values;
    }

    /// <summary>Closes the connection this helper opened for an operation that had none; never the engine's own.</summary>
    public async ValueTask DisposeAsync()
    {
        if (opened is null) return;
        await opened.DisposeAsync();
        operation.Connection = opened = null;
    }
}

/// <summary>
/// The backing classes registered in code, by alias, which a data source names in its <c>handler</c> property. Classes compiled from the C#
/// folder follow, each made for the request from the generation serving, and keep only a valid alias still free: the site's own classes win.
/// The site fails to start when a registered class has an invalid alias or shares one (<see cref="DataSourceHandlerValidation"/>); should
/// such a class reach a request all the same, it is logged and left out, and the first class with the alias keeps it.
/// </summary>
public sealed class DataSourceHandlers(IEnumerable<IDataSourceHandler>? handlers = null, Code.CompiledTypes? compiled = null, ILogger<DataSourceHandlers>? logger = null)
{
    private readonly Dictionary<string, IDataSourceHandler> site = Index(handlers ?? [], logger);
    private Dictionary<string, IDataSourceHandler>? all;

    // Compiled classes are made on first use, not in the constructor, so one that takes this registry is given this instance.
    public IReadOnlyDictionary<string, IDataSourceHandler> All => all ??= WithCompiled(site, compiled);
    public IDataSourceHandler? Find(string alias) => All.GetValueOrDefault(alias);

    private static Dictionary<string, IDataSourceHandler> WithCompiled(Dictionary<string, IDataSourceHandler> site, Code.CompiledTypes? compiled)
    {
        var index = new Dictionary<string, IDataSourceHandler>(site, StringComparer.Ordinal);
        foreach (var handler in compiled?.Create<IDataSourceHandler>() ?? [])
            if (DataSourceDefinition.IsAlias(handler.Alias)) index.TryAdd(handler.Alias, handler);
        return index;
    }

    private static Dictionary<string, IDataSourceHandler> Index(IEnumerable<IDataSourceHandler> handlers, ILogger? logger)
    {
        var index = new Dictionary<string, IDataSourceHandler>(StringComparer.Ordinal);
        foreach (var handler in handlers)
            if (Problem(handler, index) is { } problem) logger?.LogError("{Problem} The class is left out.", problem);
            else index.Add(handler.Alias, handler);
        return index;
    }

    /// <summary>What is wrong with the registered classes' aliases, one message per class: an alias that is not one, or one an earlier class has. Empty when nothing is.</summary>
    internal static IReadOnlyList<string> Problems(IEnumerable<IDataSourceHandler> handlers)
    {
        var index = new Dictionary<string, IDataSourceHandler>(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var handler in handlers)
            if (Problem(handler, index) is { } problem) problems.Add(problem);
            else index.Add(handler.Alias, handler);
        return problems;
    }

    private static string? Problem(IDataSourceHandler handler, Dictionary<string, IDataSourceHandler> earlier)
        => !DataSourceDefinition.IsAlias(handler.Alias)
            ? $"The backing class {handler.GetType().Name} has the alias '{handler.Alias}', which is not one: use letters, digits, '.', '-' and '_', starting with a letter."
            : earlier.ContainsKey(handler.Alias) ? $"Two backing classes have the alias {handler.Alias}." : null;
}

/// <summary>
/// Checks the backing classes registered in code when the site starts, made in a scope of their own as a request would make them, and
/// stops the start with every problem <see cref="DataSourceHandlers"/> would otherwise log on each request: an alias that is not one, or
/// one two classes share. Classes compiled from the C# folder are checked when they compile.
/// </summary>
internal sealed class DataSourceHandlerValidation(IServiceScopeFactory scopes) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var problems = DataSourceHandlers.Problems(scope.ServiceProvider.GetServices<IDataSourceHandler>());
        if (problems.Count > 0) throw new InvalidOperationException(string.Join(" ", problems));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
