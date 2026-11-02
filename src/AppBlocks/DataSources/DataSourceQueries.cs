using System.Data.Common;
using System.Globalization;
using AppBlocks.Forms;

namespace AppBlocks.DataSources;

public sealed record DataSourceQueryColumn(string Name, string Field, string Kind);
public sealed record DataSourceQueryResult(IReadOnlyList<DataSourceQueryColumn> Columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows);

/// <summary>The shared read path for lists and paper reports. All callers keep the source's own access rules.</summary>
public sealed class DataSourceQueries(DataSourceRuntime runtime, IDataSourceUserResolver users, IEnumerable<IDataSourcePolicy> policies, BindVariableProviders? providers = null)
{
    public async Task<DataSourceQueryResult> QueryAsync(DataSourceDefinition definition, FormExecutionContext context,
        int maximumRows = 1000, CancellationToken ct = default)
    {
        if (maximumRows < 1) throw new ArgumentOutOfRangeException(nameof(maximumRows));
        var plan = runtime.Plan(definition).Plan ?? throw new DataSourceException("The data source is not available.");
        // Opened before the policies are asked, so they can read other rows through the connection the query uses.
        await using var connection = await runtime.OpenAsync(definition.Connection, ct);
        var user = await AuthorizeAsync(definition, context, connection, ct);
        var app = providers is null ? null : await providers.ResolveAsync(context, ct);
        var parameters = new SqlParameters(plan.Dialect);
        var now = runtime.Now();
        object? Value(BindVariable variable) => Bind(variable, user, context.Parameters, now, plan.Dialect, app);
        var from = plan.From(parameters, Value);
        var predicate = plan.Restriction?.Render(parameters, Value) ?? "1 = 1";
        var sql = plan.Dialect.Select(from, plan.Columns.Select(c => c.Column), [], predicate, maximumRows == int.MaxValue ? maximumRows : maximumRows + 1);
        await using var command = parameters.Command(connection, null, sql);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        while (await reader.ReadAsync(ct))
        {
            if (rows.Count == maximumRows) throw new DataSourceException($"The query returned more than {maximumRows} rows. Narrow the query or increase its row limit.");
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < plan.Columns.Count; i++) row[plan.Columns[i].Column] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return new(plan.Columns.Select(c => new DataSourceQueryColumn(c.Column, c.Field, c.Kind)).ToArray(), rows);
    }

    /// <summary>The check a report request makes before its own query, which has no connection to offer the policies.</summary>
    internal Task<DataSourceUser> AuthorizeAsync(DataSourceDefinition definition, FormExecutionContext context, CancellationToken ct)
        => AuthorizeAsync(definition, context, null, ct);

    internal async Task<DataSourceUser> AuthorizeAsync(DataSourceDefinition definition, FormExecutionContext context, DbConnection? connection, CancellationToken ct)
    {
        var user = await users.ResolveAsync(context, ct);
        if (user is null || !CanRead(definition, user)) throw new UnauthorizedAccessException();
        foreach (var policy in policies)
            if (!await policy.IsAllowedAsync(new DataSourceRequest(definition.Alias, DataSourceAction.Query, user, context, null, null, null, connection), ct))
                throw new UnauthorizedAccessException();
        return user;
    }

    /// <summary>
    /// The check a public report block's request makes for a visitor with no account. Read groups can name no one here, and
    /// every policy is still asked, with <see cref="DataSourceUser.Anonymous"/>. A source that binds anything about the
    /// caller cannot be read at all: <c>:USER_KEY</c> has no value to give it, and SQL such as <c>:USER_NAME IS NULL</c> would otherwise read as everyone's rows.
    /// </summary>
    internal async Task AuthorizeAnonymousAsync(DataSourceDefinition definition, FormExecutionContext context, CancellationToken ct)
    {
        if (definition.ReadGroups.Count > 0 || runtime.Plan(definition).Plan is not { BindsUser: false }) throw new UnauthorizedAccessException();
        foreach (var policy in policies)
            if (!await policy.IsAllowedAsync(new DataSourceRequest(definition.Alias, DataSourceAction.Query, DataSourceUser.Anonymous, context, null, null, null, null), ct))
                throw new UnauthorizedAccessException();
    }

    public static bool CanRead(DataSourceDefinition definition, DataSourceUser user) => definition.ReadGroups.Count == 0
        || definition.ReadGroups.Concat(definition.WriteGroups).Any(user.IsInMemberGroup);

    internal static object? Bind(BindVariable variable, DataSourceUser user, IReadOnlyDictionary<string, string>? parameters, DateTime now, SqlDialect dialect, IReadOnlyDictionary<string, object?>? app = null)
        => variable.Kind switch {
            BindVariableKind.UserSid => user.Sid, BindVariableKind.UserName => user.Name, BindVariableKind.UserKey => user.Key,
            BindVariableKind.UserEmail => user.Email, BindVariableKind.UserGroups => user.Groups, BindVariableKind.Now => dialect.Now(now),
            BindVariableKind.Param => parameters?.FirstOrDefault(pair => pair.Key.Equals(variable.Name, StringComparison.OrdinalIgnoreCase)).Value,
            BindVariableKind.App => app?.GetValueOrDefault(variable.Name!),
            _ => null,
        };

    internal static string ChoiceValue(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
}
