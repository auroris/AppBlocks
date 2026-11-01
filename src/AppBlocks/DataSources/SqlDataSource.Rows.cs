using System.Data.Common;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AppBlocks.Forms;

namespace AppBlocks.DataSources;

/// <summary>The rows that belong to one parent record: those whose <paramref name="Field"/> holds the parent's key, given as text.</summary>
public sealed record DataSourceScope(string Field, string Value);

/// <summary>
/// One change of a batch: <c>create</c> with the new row's values, <c>update</c> with the row's key, the version the
/// caller read and the values that changed, or <c>delete</c> with the key and the version.
/// </summary>
public sealed record DataSourceChange(string Operation, string? Key, string? Version, JsonElement Values);

/// <summary>
/// What a list or a batch answers: the rows of the scope, in key order, and the record a new row starts from; or the
/// status of the change that failed, its position in the batch and its field errors. A batch writes all of its
/// changes or none of them.
/// </summary>
public sealed record DataSourceRowsResult(int Status, IReadOnlyList<FormRecord>? Rows = null, FormRecord? NewRow = null,
    int? Index = null, IReadOnlyDictionary<string, string[]>? Errors = null)
{
    public static DataSourceRowsResult From(FormResult result, int? index = null) => new(result.Status, Index: index, Errors: result.Errors);
}

/// <summary>
/// The rows of a parent record, as a tabular form edits them: a list of the rows linked to one key, and a batch of
/// creates, updates and deletes applied in one transaction. Every row is read through the restriction and written
/// with the same checks a single form operation makes; a row that is not linked to the parent is not found, and a new
/// row is linked to it whatever the change says.
/// </summary>
public sealed partial class SqlDataSource
{
    /// <summary>The most changes one batch takes.</summary>
    public const int MaxChanges = 200;

    /// <summary>Whether the caller may write this data source at all: a writable table, and membership of the write groups.</summary>
    public async Task<bool> MayWriteAsync(ClaimsPrincipal member, CancellationToken ct)
        => Entry.Plan is { Writable: true } && await users.ResolveAsync(member, ct) is { } user && CanWrite(user);

    /// <summary>The rows linked to the scope's key, at most <paramref name="maximumRows"/>, and the record a new row starts from.</summary>
    public async Task<DataSourceRowsResult> ListRowsAsync(DataSourceScope scope, FormExecutionContext context, int maximumRows, CancellationToken ct)
    {
        if (Entry.Plan is not { } plan) return DataSourceRowsResult.From(Unavailable());
        if (await users.ResolveAsync(context, ct) is not { } user || !CanRead(user)) return new(403);
        if (Link(plan, scope, out var link, out var value) is { } problem) return DataSourceRowsResult.From(problem);
        var binder = new Binder(plan, user, context, runtime.Now(), null);
        return await RunAsync(plan, write: false, async (connection, _) => {
            if (!await AllowedAsync(DataSourceAction.Query, user, context, null, null, null, connection, null, ct)) return new DataSourceRowsResult(403);
            var rows = await ReadScopeAsync(plan, connection, null, link, value, binder, maximumRows, ct);
            return new DataSourceRowsResult(200, rows.Select(row => Record(plan, row, user)).ToArray(), NewRow(plan, user, binder, link, value));
        }, result => DataSourceRowsResult.From(result), ct);
    }

    /// <summary>
    /// Applies the changes in one transaction, in order, and answers with the rows as they are afterwards. The first
    /// change the engine refuses stops the batch, nothing is written, and the answer names the change and its errors.
    /// A retry with the same submission key and the same changes answers with the rows and writes nothing again.
    /// </summary>
    public async Task<DataSourceRowsResult> SaveRowsAsync(DataSourceScope scope, IReadOnlyList<DataSourceChange> changes, FormExecutionContext context,
        int maximumRows, CancellationToken ct)
    {
        if (Entry.Plan is not { } plan) return DataSourceRowsResult.From(Unavailable());
        if (await users.ResolveAsync(context, ct) is not { } user || !plan.Writable || !CanWrite(user)) return new(403);
        if (Link(plan, scope, out var link, out var value) is { } problem) return DataSourceRowsResult.From(problem);
        if (changes.Count == 0) return await ListRowsAsync(scope, context, maximumRows, ct);
        if (changes.Count > MaxChanges) return new(422, Errors: new Dictionary<string, string[]> { [""] = [$"Save at most {MaxChanges} rows at a time."] });
        if (!Guid.TryParse(context.SubmissionKey, out var submission)) return new(422, Errors: new Dictionary<string, string[]> { [""] = ["A submission identifier is required."] });
        for (var i = 0; i < changes.Count; i++)
        {
            var refused = changes[i].Operation switch {
                "create" => definition.AllowInsert ? null : "New rows are not accepted here.",
                "update" => definition.AllowUpdate ? null : "Rows cannot be changed here.",
                "delete" => definition.AllowDelete ? null : "Rows cannot be deleted here.",
                _ => "Each change is a create, an update or a delete.",
            };
            if (refused is not null)
                return new(changes[i].Operation is "create" or "update" or "delete" ? 403 : 422, Index: i, Errors: new Dictionary<string, string[]> { [""] = [refused] });
        }
        // One receipt for the whole batch, so a retried request neither writes twice nor fails on versions it already moved.
        var receipt = $"{definition.Key:N}:{user.Key}:{context.PageKey:N}:{context.FormKey:N}:{submission:N}:rows";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(changes))));
        using var gate = await runtime.Receipts.EnterAsync(receipt, ct);
        if (runtime.Receipts.Find(receipt) is { } earlier)
            return earlier.Hash == hash ? await ListRowsAsync(scope, context with { SubmissionKey = null }, maximumRows, ct)
                : new(422, Errors: new Dictionary<string, string[]> { [""] = ["This submission was already used with different rows. Reload the page."] });
        var now = runtime.Now();
        var outcome = await RunAsync(plan, write: true, async (connection, transaction) => {
            for (var i = 0; i < changes.Count; i++)
            {
                FormResult result;
                try { result = await ApplyAsync(plan, user, context, link, value, changes[i], now, connection, transaction!, ct); }
                catch (DataSourceFieldException error) { result = FormResult.Invalid(error.Field, error.Message); }
                catch (DataSourceException error) { result = FormResult.Invalid("", error.Message); }
                catch (DbException error)
                {
                    runtime.Logger.LogWarning(error, "Data source {Alias}: the database refused a change of a batch.", definition.Alias);
                    result = FormResult.Invalid("", "The database refused this change. Check the values and try again.");
                }
                // Returning without a commit rolls every earlier change of the batch back.
                if (result.Status is not (200 or 204)) return DataSourceRowsResult.From(result, i);
            }
            await transaction!.CommitAsync(ct);
            return new DataSourceRowsResult(200);
        }, result => DataSourceRowsResult.From(result), ct);
        if (outcome.Status != 200) return outcome;
        runtime.Receipts.Add(receipt, "rows", hash);
        return await ListRowsAsync(scope, context with { SubmissionKey = null }, maximumRows, ct);
    }

    private async Task<FormResult> ApplyAsync(DataSourcePlan plan, DataSourceUser user, FormExecutionContext context, PlanColumn link, object? value,
        DataSourceChange change, DateTime now, DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        var scope = new ScopeMatch(link, value);
        switch (change.Operation)
        {
            case "create":
                if (Parse(plan, change.Values, null, out var rejected) is not { } values) return rejected!;
                // The new row belongs to the parent, whatever the change says.
                values[link] = value;
                return await InsertRecordAsync(plan, user, context, values, now, connection, transaction, ct);
            case "update":
                if (change.Key is null || !TryParseKey(plan, change.Key, out var keys)) return FormResult.Missing();
                if (string.IsNullOrWhiteSpace(change.Version)) return FormResult.Invalid("", "The row's version is required.");
                if (change.Values.ValueKind != JsonValueKind.Object) return FormResult.Invalid("", "Row values must be an object.");
                return await UpdateRecordAsync(plan, user, context, change.Key, keys, change.Values, change.Version, now, connection, transaction, scope, ct);
            default:
                if (change.Key is null || !TryParseKey(plan, change.Key, out var deleteKeys)) return FormResult.Missing();
                if (string.IsNullOrWhiteSpace(change.Version)) return FormResult.Invalid("", "The row's version is required.");
                return await DeleteRecordAsync(plan, user, context, change.Key, deleteKeys, change.Version, connection, transaction, scope, ct);
        }
    }

    /// <summary>The scope's column and the parent key as that column's canonical value; a result when the scope cannot be applied.</summary>
    private static FormResult? Link(DataSourcePlan plan, DataSourceScope scope, out PlanColumn link, out object? value)
    {
        link = null!;
        value = null;
        if (plan.Columns.FirstOrDefault(c => c.Field.Equals(scope.Field, StringComparison.OrdinalIgnoreCase)) is not { } column)
            return FormResult.Invalid("", $"'{scope.Field}' is not a field of the data source.");
        link = column;
        value = FieldValues.FromValue(scope.Value, column.Kind, out var valid);
        if (!valid || value is null || value is string { Length: 0 }) return FormResult.Missing();
        try { plan.Dialect.ToDatabase(value, column.Kind, column.Target); }
        catch (DataSourceException) { return FormResult.Missing(); }
        return null;
    }

    /// <summary>The record a new row starts from: the data source's defaults, and the link field already set to the parent.</summary>
    private FormRecord NewRow(DataSourcePlan plan, DataSourceUser user, Binder binder, PlanColumn link, object? value)
    {
        var values = plan.Columns.ToDictionary(c => c.Field, c => (object?)null, StringComparer.Ordinal);
        foreach (var @default in plan.Defaults) values[@default.Column.Field] = FieldValues.ToJson(binder.Default(@default, out _));
        values[link.Field] = FieldValues.ToJson(value);
        return new(null, null, values, new(Create: definition.AllowInsert && plan.Writable && CanWrite(user)));
    }

    /// <summary>The rows whose link column holds the parent's key, through the restriction, sorted by their keys.</summary>
    private static async Task<List<Row>> ReadScopeAsync(DataSourcePlan plan, DbConnection connection, DbTransaction? transaction, PlanColumn link, object? value,
        Binder binder, int maximumRows, CancellationToken ct)
    {
        var parameters = new SqlParameters(plan.Dialect);
        var from = plan.From(parameters, binder.Value);
        var match = new[] { (link.Column, parameters.Add(ToDatabase(plan, link, value))) };
        var restriction = plan.Restriction?.Render(parameters, binder.Value);
        await using var command = parameters.Command(connection, transaction, plan.Dialect.Select(from, plan.ReadColumns, match, restriction, maximumRows + 1));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<Row>();
        while (await reader.ReadAsync(ct))
        {
            if (rows.Count == maximumRows) throw new DataSourceException($"The record has more than {maximumRows} rows here. Narrow the data source or raise the block's row limit.");
            var values = new Dictionary<PlanColumn, object?>();
            for (var i = 0; i < plan.Columns.Count; i++)
            {
                var cell = FieldValues.FromDatabase(reader.GetValue(i), plan.Columns[i].Kind);
                if (cell is string { Length: > FieldKinds.MaxTextLength }) throw new DataSourceException($"The value of {plan.Columns[i].Label} is larger than a form can hold.");
                values[plan.Columns[i]] = cell;
            }
            rows.Add(new Row(values, plan.RowVersionColumn is null ? null : reader.GetValue(plan.Columns.Count)));
        }
        // Key order whatever the table's physical order: numbers as numbers, everything else as text.
        rows.Sort((a, b) => CompareKeys(plan, a.Values, b.Values));
        return rows;
    }

    private static int CompareKeys(DataSourcePlan plan, IReadOnlyDictionary<PlanColumn, object?> a, IReadOnlyDictionary<PlanColumn, object?> b)
    {
        foreach (var key in plan.Keys)
        {
            var (x, y) = (a.GetValueOrDefault(key), b.GetValueOrDefault(key));
            var result = (x, y) switch {
                (null, null) => 0,
                (null, _) => -1,
                (_, null) => 1,
                (decimal m, decimal n) => m.CompareTo(n),
                _ => string.CompareOrdinal(Convert.ToString(FieldValues.ToJson(x), CultureInfo.InvariantCulture), Convert.ToString(FieldValues.ToJson(y), CultureInfo.InvariantCulture)),
            };
            if (result != 0) return result;
        }
        return 0;
    }
}
