using System.Data.Common;
using System.Globalization;
using AppBlocks.Forms;

namespace AppBlocks.DataSources;

public sealed record DataSourceQueryColumn(string Name, string Field, string Kind);
public sealed record DataSourceQueryResult(IReadOnlyList<DataSourceQueryColumn> Columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows);

/// <summary>The shared read path for lists and paper reports. All callers keep the source's own access rules.</summary>
public sealed class DataSourceQueries(DataSourceRuntime runtime, IDataSourceUserResolver users, IEnumerable<IDataSourcePolicy> policies, BindVariableProviders? providers = null,
    DataSourceHandlers? handlers = null)
{
    /// <summary>Whether this definition supports anonymous reads, before page access and custom policies are checked.</summary>
    public bool AllowsAnonymousRead(DataSourceDefinition definition) => CanRead(definition, DataSourceUser.Anonymous, runtime.Plan(definition).Plan);

    public Task<DataSourceQueryResult> QueryAsync(DataSourceDefinition definition, FormExecutionContext context,
        int maximumRows = 1000, CancellationToken ct = default)
        => QueryAsync(definition, context, maximumRows, null, firstRows: false, ct);

    /// <param name="order">Columns of the row source to sort by, as the plan spells them.</param>
    /// <param name="firstRows">Return the first <paramref name="maximumRows"/> rows rather than refusing a larger result, for a caller that wants only those.</param>
    internal async Task<DataSourceQueryResult> QueryAsync(DataSourceDefinition definition, FormExecutionContext context, int maximumRows,
        IReadOnlyList<(string Column, bool Descending)>? order, bool firstRows, CancellationToken ct)
    {
        if (maximumRows < 1) throw new ArgumentOutOfRangeException(nameof(maximumRows));
        var plan = runtime.Plan(definition).Plan ?? throw new DataSourceException("The data source is not available.");
        if (definition.Handler is { } alias && handlers?.Find(alias) is null) throw new DataSourceException("The data source is not available: its backing class is not registered.");
        // Opened before the policies are asked, so they can read other rows through the connection the query uses.
        await using var connection = await runtime.OpenAsync(definition.Connection, ct);
        var user = await AuthorizeAsync(definition, context, connection, ct);
        var app = providers is null ? null : await providers.ResolveAsync(context, ct);
        var parameters = plan.Parameters();
        var now = runtime.Now(definition);
        object? Value(BindVariable variable) => Bind(variable, user, context.Parameters, now, plan.Dialect, app);
        if (definition.Handler is { } named && handlers?.Find(named) is { } handler)
        {
            var operation = new DataSourceOperation(definition.Alias, DataSourceAction.Query, user, context, plan.Dialect.Kind) { Connection = connection };
            await using var helper = new DataSourceSql(plan, operation, Value, token => runtime.OpenAsync(definition.Connection, token));
            operation.Sql = helper;
            if (await handler.ReadAsync(operation, ct) is { } supplied) return Supplied(plan, supplied, maximumRows, order, firstRows);
        }
        var from = plan.From(parameters, Value);
        var predicate = plan.Restriction?.Render(parameters, Value) ?? "1 = 1";
        var sql = plan.Dialect.Select(from, plan.Columns.Select(c => c.Column), [], predicate, firstRows || maximumRows == int.MaxValue ? maximumRows : maximumRows + 1, order);
        await using var command = parameters.Command(connection, null, sql);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        while (await reader.ReadAsync(ct))
        {
            if (rows.Count == maximumRows && firstRows) break;
            if (rows.Count == maximumRows) throw new DataSourceException($"The query returned more than {maximumRows} rows. Narrow the query or increase its row limit.");
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < plan.Columns.Count; i++) row[plan.Columns[i].Column] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return new(plan.Columns.Select(c => new DataSourceQueryColumn(c.Column, c.Field, c.Kind)).ToArray(), rows);
    }

    /// <summary>
    /// Rows a backing class answered with (<see cref="IDataSourceHandler.ReadAsync"/>), shaped as the row source's would be: one entry per
    /// column of the plan, found by name in any letter case, sorted by <paramref name="order"/> and held to the row limit.
    /// </summary>
    private static DataSourceQueryResult Supplied(DataSourcePlan plan, IReadOnlyList<IReadOnlyDictionary<string, object?>> supplied, int maximumRows,
        IReadOnlyList<(string Column, bool Descending)>? order, bool firstRows)
    {
        IEnumerable<IReadOnlyDictionary<string, object?>> sequence = supplied;
        if (order is { Count: > 0 })
        {
            IOrderedEnumerable<IReadOnlyDictionary<string, object?>>? sorted = null;
            foreach (var (column, descending) in order)
            {
                object? Key(IReadOnlyDictionary<string, object?> row) => Lookup(row, column);
                sorted = sorted is null
                    ? descending ? sequence.OrderByDescending(Key, SuppliedValueComparer.Instance) : sequence.OrderBy(Key, SuppliedValueComparer.Instance)
                    : descending ? sorted.ThenByDescending(Key, SuppliedValueComparer.Instance) : sorted.ThenBy(Key, SuppliedValueComparer.Instance);
            }
            sequence = sorted!;
        }
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var source in sequence)
        {
            if (rows.Count == maximumRows && firstRows) break;
            if (rows.Count == maximumRows) throw new DataSourceException($"The backing class returned more than {maximumRows} rows. Narrow the query or increase its row limit.");
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in plan.Columns) row[column.Column] = Lookup(source, column.Column);
            rows.Add(row);
        }
        return new(plan.Columns.Select(c => new DataSourceQueryColumn(c.Column, c.Field, c.Kind)).ToArray(), rows);

        static object? Lookup(IReadOnlyDictionary<string, object?> row, string column)
            => row.TryGetValue(column, out var value) ? value is DBNull ? null : value
                : row.FirstOrDefault(pair => pair.Key.Equals(column, StringComparison.OrdinalIgnoreCase)).Value is var found && found is not DBNull ? found : null;
    }

    /// <summary>Orders values a class returns: nulls first, numbers by value whatever their type, then like types by their own order, then text ignoring case.</summary>
    private sealed class SuppliedValueComparer : IComparer<object?>
    {
        public static readonly SuppliedValueComparer Instance = new();

        public int Compare(object? x, object? y)
        {
            if (x is null || y is null) return x is null ? y is null ? 0 : -1 : 1;
            if (Number(x) is { } a && Number(y) is { } b) return a.CompareTo(b);
            if (x.GetType() == y.GetType() && x is IComparable comparable) return comparable.CompareTo(y);
            return string.Compare(Convert.ToString(x, CultureInfo.InvariantCulture), Convert.ToString(y, CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        }

        private static decimal? Number(object value) => value switch {
            decimal number => number, int number => number, long number => number, short number => number, byte number => number,
            double number when double.IsFinite(number) && Math.Abs(number) < 7.9e27 => (decimal)number,
            float number when float.IsFinite(number) && Math.Abs(number) < 7.9e27f => (decimal)number,
            _ => null,
        };
    }

    /// <summary>The check a report request makes before its own query, which has no connection to offer the policies.</summary>
    internal Task<DataSourceUser> AuthorizeAsync(DataSourceDefinition definition, FormExecutionContext context, CancellationToken ct)
        => AuthorizeAsync(definition, context, null, ct);

    internal async Task<DataSourceUser> AuthorizeAsync(DataSourceDefinition definition, FormExecutionContext context, DbConnection? connection, CancellationToken ct)
    {
        var user = await ResolveReaderAsync(users, context, ct);
        if (user is null || !CanRead(definition, user)
            || user.IsAnonymous && !AllowsAnonymousRead(definition)) throw new UnauthorizedAccessException();
        if (definition.Handler is { } alias)
        {
            // A backing class that is not there leaves the data source refused, never unguarded.
            if (handlers?.Find(alias) is not { } handler || runtime.Plan(definition).Plan is not { } plan)
            {
                runtime.Logger.LogWarning("Data source {Alias} refused a query: its backing class {Handler} is not registered or its plan is not available.", definition.Alias, alias);
                throw new UnauthorizedAccessException();
            }
            var operation = new DataSourceOperation(definition.Alias, DataSourceAction.Query, user, context, plan.Dialect.Kind) { Connection = connection };
            var now = runtime.Now(definition);
            var app = providers is null ? null : await providers.ResolveAsync(context, ct);
            await using var sql = new DataSourceSql(plan, operation, variable => Bind(variable, user, context.Parameters, now, plan.Dialect, app),
                token => runtime.OpenAsync(definition.Connection, token));
            operation.Sql = sql;
            if ((await handler.QueryAsync(operation, ct)).Kind == DataSourceOutcomeKind.Refused) throw new UnauthorizedAccessException();
        }
        if (!await policies.AllowAsync(new DataSourceRequest(definition.Alias, DataSourceAction.Query, user, context, null, null, null, connection), ct))
            throw new UnauthorizedAccessException();
        return user;
    }

    /// <summary>
    /// Resolve the account, or an anonymous visitor. An authenticated identity that failed resolution must not become
    /// anonymous: it may represent a disabled account. Page endpoints also enforce Umbraco Public Access.
    /// </summary>
    internal static async Task<DataSourceUser?> ResolveReaderAsync(IDataSourceUserResolver users, FormExecutionContext context, CancellationToken ct)
        => Reader(await users.ResolveAsync(context, ct), context.Member);

    internal static async Task<DataSourceUser?> ResolveReaderAsync(IDataSourceUserResolver users, System.Security.Claims.ClaimsPrincipal principal, CancellationToken ct)
        => Reader(await users.ResolveAsync(principal, ct), principal);

    private static DataSourceUser? Reader(DataSourceUser? user, System.Security.Claims.ClaimsPrincipal principal)
    {
        if (user is not null && !user.IsAnonymous) return user;
        return principal.Identities.Any(identity => identity.IsAuthenticated) ? null : DataSourceUser.Anonymous;
    }

    /// <summary>
    /// The access setting, and for an anonymous caller also a plan that binds nothing about the caller: a query that binds an identity means
    /// nothing without an account, even when marked Public. Callers must also enforce page access and policies.
    /// </summary>
    internal static bool CanRead(DataSourceDefinition definition, DataSourceUser user, DataSourcePlan? plan)
        => CanRead(definition, user) && (!user.IsAnonymous || plan is { BindsUser: false });

    /// <summary>The source's access setting. Callers must also enforce page access, identity binds and policies.</summary>
    public static bool CanRead(DataSourceDefinition definition, DataSourceUser user) => definition.Access switch {
        DataSourceAccess.Public => true,
        DataSourceAccess.NotPublic => !user.IsAnonymous,
        DataSourceAccess.SpecificGroups => !user.IsAnonymous && definition.ReadGroups.Count > 0
            && definition.ReadGroups.Concat(definition.WriteGroups).Any(user.IsInMemberGroup),
        _ => false,
    };

    /// <summary>What a query's SQL binds for a variable (<see cref="BindVariableValues"/>); null for <c>:FIELD.</c> and <c>:ARG.</c>, which a query has none of.</summary>
    internal static object? Bind(BindVariable variable, DataSourceUser user, IReadOnlyDictionary<string, string>? parameters, DateTimeOffset now, SqlDialect dialect, IReadOnlyDictionary<string, object?>? app = null)
        => new BindVariableValues(user, parameters, now, app).Bind(variable, dialect);

    internal static string ChoiceValue(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
}
