using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AppBlocks.Forms;

namespace AppBlocks.DataSources;

/// <summary>
/// A published <c>sqlDataSource</c> node as a form resource, so the form endpoints, antiforgery, concurrency
/// handling, field blocks and pickers work with it as they do with a C# resource. The engine fetches a row by key
/// through the row restriction and writes it back with statements it generates from the schema, or with the
/// editor's custom statements. Every operation re-checks the caller, the groups, the allow flags and the policies,
/// whatever the displayed capabilities said; every value is a bound parameter. A backing class the definition names
/// (<see cref="IDataSourceHandler"/>) is asked between the engine's own checks and the policies.
/// </summary>
public sealed partial class SqlDataSource(DataSourceDefinition definition, DataSourceRuntime runtime, IDataSourceUserResolver users,
    IEnumerable<IDataSourcePolicy> policies, BindVariableProviders? providers = null, DataSourceHandlers? handlers = null) : IFormResource, Attachments.IAttachmentOwner
{
    private DataSourceRuntime.PlanEntry? entry;
    private DataSourceRuntime.PlanEntry Entry => entry ??= runtime.Plan(definition);
    /// <summary>The backing class the definition names; null for none, and for one that is not registered, which <see cref="Problem"/> reports.</summary>
    private IDataSourceHandler? Handler => definition.Handler is { } alias ? handlers?.Find(alias) : null;
    private string? HandlerProblem => definition.Handler is { } alias && Handler is null
        ? $"The backing class {alias} is not registered in code. Register it, or clear the data source's backing class and publish it again." : null;
    /// <summary>The plan, unless the data source cannot run: its schema cannot be read, or its backing class is missing.</summary>
    private DataSourcePlan? Ready => HandlerProblem is null ? Entry.Plan : null;

    public DataSourceDefinition Definition => definition;
    /// <summary>Whether anonymous reads can reach the policies, subject to the calling page and owning record's access.</summary>
    public bool AllowsAnonymousRead => CanRead(DataSourceUser.Anonymous);
    /// <summary>What :APP. binds, asked once per operation and once per batch of rows, so every row of a save sees the same answer; nothing without a provider, as in a test.</summary>
    private async Task<IReadOnlyDictionary<string, object?>?> AppAsync(FormExecutionContext context, CancellationToken ct) => providers is null ? null : await providers.ResolveAsync(context, ct);
    public string Alias => definition.Alias;
    public string ContractVersion => Entry.Plan?.ContractVersion ?? "unavailable";
    public IReadOnlyList<FormField> Fields => Entry.Plan?.Fields ?? [];
    /// <summary>Why the data source cannot run, such as an unreachable database, a column that is gone or a missing backing class; null when it can.</summary>
    public string? Problem => HandlerProblem ?? Entry.Problem;

    /// <summary>Each field with the column behind it and its kind, as a backing class sees them; empty while the plan is unavailable.</summary>
    internal IReadOnlyList<Samples.CodeRecipeField> FieldColumns => Entry.Plan?.Columns.Select(c => new Samples.CodeRecipeField(c.Field, c.Column, c.Kind)).ToArray() ?? [];

    /// <summary>What the back-office catalogue shows: the columns behind the fields and what the data source allows.</summary>
    public object Describe() => new {
        key = definition.Key, name = definition.Name, connection = definition.Connection, source = definition.Source,
        allowInsert = definition.AllowInsert, allowUpdate = definition.AllowUpdate, allowDelete = definition.AllowDelete,
        keyColumns = definition.KeyColumns, handler = definition.Handler, problem = Problem,
        columns = Entry.Plan?.Columns.Select(c => new { field = c.Field, column = c.Column, kind = c.Kind, readOnly = c.ReadOnly, required = c.Required,
            key = Entry.Plan.Keys.Contains(c) }) ?? [],
    };

    public async Task<FormResult> InitializeAsync(FormExecutionContext context, CancellationToken ct)
    {
        if (Ready is not { } plan) return Unavailable();
        if (await DataSourceQueries.ResolveReaderAsync(users, context, ct) is not { } user || !CanRead(user)) return FormResult.Denied();
        var binder = new Binder(plan, user, context, runtime.Now(), null, await AppAsync(context, ct));
        var values = plan.Columns.ToDictionary(c => c.Field, c => (object?)null, StringComparer.Ordinal);
        foreach (var value in plan.Defaults) values[value.Column.Field] = FieldValues.ToJson(binder.Default(value, out _));
        if (Handler is not null)
        {
            // No connection is open yet: the class's statements open one, closed again once it answers.
            var operation = Operation(plan, DataSourceAction.Initialize, user, context, null, null, values: new Dictionary<string, object?>(values));
            try
            {
                var outcome = await AskAsync(plan, operation, binder, static (handler, operation, ct) => handler.InitializeAsync(operation, ct), ct);
                if (outcome.Kind == DataSourceOutcomeKind.Refused) return outcome.Result();
                values = Shown(plan, operation.Values!);
            }
            catch (DataSourceFieldException error) { return FormResult.Invalid(error.Field, error.Message); }
            catch (DataSourceException error) { return FormResult.Invalid("", error.Message); }
            catch (DbException error)
            {
                runtime.Logger.LogWarning(error, "Data source {Alias}: the database refused a statement of the backing class.", definition.Alias);
                return FormResult.Invalid("", "The record could not be read with these values.");
            }
        }
        if (!await AllowedAsync(DataSourceAction.Initialize, user, context, null, null, values, null, null, ct)) return FormResult.Denied();
        return FormResult.Ok(new(null, null, values, new(Create: definition.AllowInsert && plan.Writable && CanWrite(user))));
    }

    public async Task<FormResult> LoadAsync(string key, FormExecutionContext context, CancellationToken ct)
    {
        if (Ready is not { } plan) return Unavailable();
        if (await DataSourceQueries.ResolveReaderAsync(users, context, ct) is not { } user || !CanRead(user)) return FormResult.Denied();
        if (!TryParseKey(plan, key, out var keys)) return FormResult.Missing();
        var binder = new Binder(plan, user, context, runtime.Now(), null, await AppAsync(context, ct));
        return await RunAsync(plan, write: false, async (connection, _) => {
            if (await ReadAsync(plan, connection, null, keys, binder, ct) is not { } row) return FormResult.Missing();
            var stored = Json(plan, row.Values);
            var shown = stored;
            if (Handler is not null)
            {
                var operation = Operation(plan, DataSourceAction.Load, user, context, connection, null, key, stored, new Dictionary<string, object?>(stored));
                var outcome = await AskAsync(plan, operation, binder, static (handler, operation, ct) => handler.LoadAsync(operation, ct), ct);
                if (outcome.Kind == DataSourceOutcomeKind.Refused) return outcome.Result();
                shown = Shown(plan, operation.Values!);
            }
            if (!await AllowedAsync(DataSourceAction.Load, user, context, key, stored, null, connection, null, ct)) return FormResult.Denied();
            return FormResult.Ok(Record(plan, row, user) with { Values = shown });
        }, ct);
    }

    public async Task<FormResult> CreateAsync(JsonElement input, FormExecutionContext context, CancellationToken ct)
    {
        if (Ready is not { } plan) return Unavailable();
        if (await DataSourceQueries.ResolveReaderAsync(users, context, ct) is not { } user || !definition.AllowInsert || !plan.Writable || !CanWrite(user)) return FormResult.Denied();
        if (!Guid.TryParse(context.SubmissionKey, out var submission)) return FormResult.Invalid("", "A submission identifier is required.");
        if (Parse(plan, input, null, out var rejected) is not { } values) return rejected!;
        var now = runtime.Now();
        var receipt = $"{definition.Key:N}:{user.Key}:{context.PageKey:N}:{context.FormKey:N}:{submission:N}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Json(plan, values).OrderBy(p => p.Key, StringComparer.Ordinal)))));
        using var gate = await runtime.Receipts.EnterAsync(receipt, ct);
        if (runtime.Receipts.Find(receipt) is { } earlier)
            return earlier.Hash == hash ? await LoadAsync(earlier.Key, context with { SubmissionKey = null }, ct)
                : FormResult.Invalid("", "This submission was already used with different values. Reload the form.");

        var app = await AppAsync(context, ct);
        var result = await RunAsync(plan, write: true, async (connection, transaction) => {
            var created = await InsertRecordAsync(plan, user, context, values, now, connection, transaction!, app, ct);
            if (created.Status == 200) await transaction!.CommitAsync(ct);
            return created;
        }, ct);
        if (result.Record?.Key is { } key) runtime.Receipts.Add(receipt, key, hash);
        return result;
    }

    /// <summary>
    /// One insert inside the caller's transaction: the defaults fill the row, the backing class may change it or write it
    /// itself, the audit columns and the version are set, the required fields are checked, the policies are asked, the row
    /// is written and read back through the restriction. The caller commits on 200; any other result means nothing should be.
    /// </summary>
    /// <param name="details">The detail blocks of a master/detail submission, which the backing class sees with the master.</param>
    private async Task<FormResult> InsertRecordAsync(DataSourcePlan plan, DataSourceUser user, FormExecutionContext context,
        Dictionary<PlanColumn, object?> values, DateTime now, DbConnection connection, DbTransaction transaction, IReadOnlyDictionary<string, object?>? app, CancellationToken ct,
        IReadOnlyList<DataSourceDetail>? details = null)
    {
        var binder = new Binder(plan, user, context, now, values, app);
        var row = new Dictionary<PlanColumn, object?>(values);
        foreach (var value in plan.Defaults.Where(d => !values.ContainsKey(d.Column)))
        {
            var filled = binder.Default(value, out var valid);
            if (!valid) return FormResult.Invalid(value.Column.Field, "The default value of this field is not valid.");
            // A default with no value, such as a request parameter that was not sent, leaves the column to the database.
            if (filled is null && value.Column.Target is { HasDefault: true }) continue;
            row[value.Column] = filled;
        }
        var outcome = DataSourceOutcome.Continue;
        if (Handler is not null)
        {
            var shown = Json(plan, row);
            var operation = Operation(plan, DataSourceAction.Create, user, context, connection, transaction, values: new Dictionary<string, object?>(shown), details: details);
            outcome = await AskAsync(plan, operation, binder.With(row), static (handler, operation, ct) => handler.CreateAsync(operation, ct), ct);
            if (outcome.Kind == DataSourceOutcomeKind.Refused) return outcome.Result();
            row = Written(plan, row, shown, operation.Values!);
            binder = binder.With(row);
        }
        if (plan.CreatedBy is { } createdBy) row[createdBy] = user.Name;
        if (plan.UpdatedBy is { } updatedBy) row[updatedBy] = user.Name;
        if (plan.CreatedAt is { } createdAt) row[createdAt] = FieldValues.FromValue(now, createdAt.Kind, out _);
        if (plan.UpdatedAt is { } updatedAt) row[updatedAt] = FieldValues.FromValue(now, updatedAt.Kind, out _);
        // Required and still empty after the defaults, with nothing in the database to fill it.
        var missing = plan.Columns.Where(c => c.Required && c.Target is { HasDefault: false, AutoValue: false, Computed: false }
            && (!row.TryGetValue(c, out var v) || v is null)).ToArray();
        if (missing.Length > 0) return new(422, Errors: missing.ToDictionary(c => c.Field, _ => new[] { "A value is required." }));
        // Asked inside the transaction, so a rule that counts other rows sees what the insert will join; a refusal also undoes what the class wrote.
        var handled = outcome.Kind == DataSourceOutcomeKind.Handled ? HandledKey(plan, outcome, row) : null;
        var handledKey = handled is null ? null : FormatKey(plan, plan.Keys.Zip(handled).ToDictionary(p => p.First, p => p.Second));
        if (!await AllowedAsync(DataSourceAction.Create, user, context, handledKey, null, Json(plan, row), connection, transaction, ct)) return FormResult.Denied();
        var keys = handled
            ?? (plan.InsertStatement is { } statement
                ? await InsertCustomAsync(plan, statement, connection, transaction, row, binder.With(row), ct)
                : await InsertAsync(plan, connection, transaction, row, ct));
        // The new row must be one the caller may see, or a write could place rows outside the restriction.
        if (await ReadAsync(plan, connection, transaction, keys, binder, ct) is not { } created) return FormResult.Denied();
        return FormResult.Ok(Record(plan, created, user));
    }

    public async Task<FormResult> UpdateAsync(string key, JsonElement input, string? version, FormExecutionContext context, CancellationToken ct)
    {
        if (Ready is not { } plan) return Unavailable();
        if (await DataSourceQueries.ResolveReaderAsync(users, context, ct) is not { } user || !definition.AllowUpdate || !plan.Writable || !CanWrite(user)) return FormResult.Denied();
        if (!TryParseKey(plan, key, out var keys)) return FormResult.Missing();
        if (input.ValueKind != JsonValueKind.Object) return FormResult.Invalid("", "Form values must be an object.");
        var now = runtime.Now();
        var app = await AppAsync(context, ct);
        return await RunAsync(plan, write: true, async (connection, transaction) => {
            var updated = await UpdateRecordAsync(plan, user, context, key, keys, input, version, now, connection, transaction!, null, app, ct);
            if (updated.Status == 200) await transaction!.CommitAsync(ct);
            return updated;
        }, ct);
    }

    /// <summary>
    /// One update inside the caller's transaction, on the locked row: an enabled version check must pass, only what differs from
    /// the stored record is checked and written, the backing class may change the row or write it itself, a key or a
    /// scope's link field cannot change, and the row is read back through the restriction. A row outside
    /// <paramref name="scope"/> is not found. The caller commits on 200.
    /// </summary>
    /// <param name="details">The detail blocks of a master/detail submission, which the backing class sees with the master.</param>
    private async Task<FormResult> UpdateRecordAsync(DataSourcePlan plan, DataSourceUser user, FormExecutionContext context, string key, object?[] keys,
        JsonElement input, string? version, DateTime now, DbConnection connection, DbTransaction transaction, ScopeMatch? scope, IReadOnlyDictionary<string, object?>? app, CancellationToken ct,
        IReadOnlyList<DataSourceDetail>? details = null)
    {
        var binder = new Binder(plan, user, context, now, null, app);
        await LockAsync(plan, connection, transaction, keys, ct);
        if (await ReadAsync(plan, connection, transaction, keys, binder, ct) is not { } current || !InScope(current, scope)) return FormResult.Missing();
        if (Token(plan, current) is { } expected && expected != version) return FormResult.Conflict();
        // Only what differs from the stored record is checked and written: the endpoint sends every field back.
        if (Parse(plan, input, current.Values, out var rejected) is not { } values) return rejected!;
        if (plan.Keys.FirstOrDefault(values.ContainsKey) is { } changedKey)
            return FormResult.Invalid(changedKey.Field, "This field cannot be changed once the record exists.");
        if (scope is not null && values.TryGetValue(scope.Column, out var moved) && !FieldValues.Same(moved, scope.Value))
            return FormResult.Invalid(scope.Column.Field, "This field cannot be changed here.");
        var proposed = new Dictionary<PlanColumn, object?>(current.Values);
        foreach (var (column, value) in values) proposed[column] = value;
        var outcome = DataSourceOutcome.Continue;
        if (Handler is not null)
        {
            var shown = Json(plan, proposed);
            var operation = Operation(plan, DataSourceAction.Update, user, context, connection, transaction, key, Json(plan, current.Values),
                new Dictionary<string, object?>(shown), details);
            outcome = await AskAsync(plan, operation, binder.With(proposed), static (handler, operation, ct) => handler.UpdateAsync(operation, ct), ct);
            if (outcome.Kind == DataSourceOutcomeKind.Refused) return outcome.Result();
            // A field the class removed stays as stored.
            var written = Written(plan, proposed, shown, operation.Values!);
            foreach (var (column, value) in current.Values) written.TryAdd(column, value);
            proposed = written;
            if (plan.Keys.FirstOrDefault(k => !FieldValues.Same(proposed[k], current.Values[k])) is { } rekeyed)
                return FormResult.Invalid(rekeyed.Field, "This field cannot be changed once the record exists.");
            if (scope is not null && !FieldValues.Same(proposed[scope.Column], scope.Value))
                return FormResult.Invalid(scope.Column.Field, "This field cannot be changed here.");
        }
        if (!await AllowedAsync(DataSourceAction.Update, user, context, key, Json(plan, current.Values), Json(plan, proposed), connection, transaction, ct))
            return FormResult.Denied();
        var changes = proposed.Where(v => !FieldValues.Same(v.Value, current.Values.GetValueOrDefault(v.Key))).ToArray();
        // A class that wrote the row itself leaves the engine only the read back.
        if (outcome.Kind != DataSourceOutcomeKind.Handled)
        {
            if (plan.UpdateStatement is { } statement)
            {
                var parameters = new SqlParameters(plan.Dialect);
                await using var command = parameters.Command(connection, transaction, statement.Render(parameters, binder.With(proposed).Value));
                await command.ExecuteNonQueryAsync(ct);
            }
            // Nothing changed, so nothing is written: no new version and no audit entry.
            else if (changes.Length == 0) return FormResult.Ok(Record(plan, current, user));
            else if (await UpdateRowAsync(plan, connection, transaction, keys, changes, user, now, binder, ct) != 1) return FormResult.Conflict();
        }
        // The row must still be one the caller may see; an update cannot hand it to someone else.
        if (await ReadAsync(plan, connection, transaction, keys, binder, ct) is not { } updated) return FormResult.Denied();
        return FormResult.Ok(Record(plan, updated, user));
    }

    public async Task<FormResult> DeleteAsync(string key, string? version, FormExecutionContext context, CancellationToken ct)
    {
        if (Ready is not { } plan) return Unavailable();
        if (await DataSourceQueries.ResolveReaderAsync(users, context, ct) is not { } user || !definition.AllowDelete || !plan.Writable || !CanWrite(user)) return FormResult.Denied();
        if (!TryParseKey(plan, key, out var keys)) return FormResult.Missing();
        var app = await AppAsync(context, ct);
        return await RunAsync(plan, write: true, async (connection, transaction) => {
            var deleted = await DeleteRecordAsync(plan, user, context, key, keys, version, connection, transaction!, null, app, ct);
            if (deleted.Status == 204) await transaction!.CommitAsync(ct);
            return deleted;
        }, ct);
    }

    /// <summary>
    /// One delete inside the caller's transaction, on the locked row, with any enabled version check, the backing class and the
    /// policies; a row outside <paramref name="scope"/> is not found. The caller commits on 204.
    /// </summary>
    private async Task<FormResult> DeleteRecordAsync(DataSourcePlan plan, DataSourceUser user, FormExecutionContext context, string key, object?[] keys,
        string? version, DbConnection connection, DbTransaction transaction, ScopeMatch? scope, IReadOnlyDictionary<string, object?>? app, CancellationToken ct)
    {
        var binder = new Binder(plan, user, context, runtime.Now(), null, app);
        await LockAsync(plan, connection, transaction, keys, ct);
        if (await ReadAsync(plan, connection, transaction, keys, binder, ct) is not { } current || !InScope(current, scope)) return FormResult.Missing();
        if (Token(plan, current) is { } expected && expected != version) return FormResult.Conflict();
        var outcome = DataSourceOutcome.Continue;
        if (Handler is not null)
        {
            var operation = Operation(plan, DataSourceAction.Delete, user, context, connection, transaction, key, Json(plan, current.Values));
            outcome = await AskAsync(plan, operation, binder.With(current.Values), static (handler, operation, ct) => handler.DeleteAsync(operation, ct), ct);
            if (outcome.Kind == DataSourceOutcomeKind.Refused) return outcome.Result();
        }
        if (!await AllowedAsync(DataSourceAction.Delete, user, context, key, Json(plan, current.Values), null, connection, transaction, ct)) return FormResult.Denied();
        if (outcome.Kind == DataSourceOutcomeKind.Handled) return new FormResult(204);
        var parameters = new SqlParameters(plan.Dialect);
        if (plan.DeleteStatement is { } statement)
        {
            await using var command = parameters.Command(connection, transaction, statement.Render(parameters, binder.With(current.Values).Value));
            await command.ExecuteNonQueryAsync(ct);
        }
        else
        {
            var (targetKeys, exists) = Where(plan, parameters, keys, binder);
            if (await CountAsync(plan, parameters, connection, transaction, plan.Dialect.Delete(plan.Target!.Name, targetKeys, exists), ct) != 1)
                return FormResult.Conflict();
        }
        return new FormResult(204);
    }

    /// <summary>The rows of one parent record: the column that links them to it and the parent's key as a canonical value.</summary>
    private sealed record ScopeMatch(PlanColumn Column, object? Value);

    private static bool InScope(Row row, ScopeMatch? scope) => scope is null || FieldValues.Same(row.Values.GetValueOrDefault(scope.Column), scope.Value);

    private Task<FormResult> RunAsync(DataSourcePlan plan, bool write, Func<DbConnection, DbTransaction?, Task<FormResult>> work, CancellationToken ct)
        => RunAsync(plan, write, work, result => result, ct);

    /// <summary>
    /// Opens the connection, for a write inside a transaction, and turns failures into results: a database that
    /// cannot be reached is 503, a statement the database refuses is 422 with the details in the log, each given to
    /// <paramref name="failed"/> to shape. A write commits explicitly when it succeeds; returning without committing,
    /// or failing, rolls everything back.
    /// </summary>
    private async Task<T> RunAsync<T>(DataSourcePlan plan, bool write, Func<DbConnection, DbTransaction?, Task<T>> work, Func<FormResult, T> failed, CancellationToken ct)
    {
        DbConnection? connection = null;
        DbTransaction? transaction = null;
        try
        {
            try
            {
                connection = await runtime.OpenAsync(definition.Connection, ct);
                if (write) transaction = await connection.BeginTransactionAsync(plan.Dialect.WriteIsolation, ct);
            }
            catch (DbException error)
            {
                runtime.Logger.LogWarning(error, "Data source {Alias} could not open connection {Connection}.", definition.Alias, definition.Connection);
                return failed(new FormResult(503, Errors: new Dictionary<string, string[]> { [""] = ["The database is not available right now. Try again in a moment."] }));
            }
            return await work(connection, transaction);
        }
        catch (DataSourceFieldException error) { return failed(FormResult.Invalid(error.Field, error.Message)); }
        catch (DataSourceException error) { return failed(FormResult.Invalid("", error.Message)); }
        catch (DbException error)
        {
            runtime.Logger.LogWarning(error, "Data source {Alias}: the database refused a statement.", definition.Alias);
            return failed(FormResult.Invalid("", write ? "The database refused this change. Check the values and try again." : "The record could not be read with these values."));
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
            if (connection is not null) await connection.DisposeAsync();
        }
    }

    private bool InAny(DataSourceUser user, IReadOnlyList<string> groups) => groups.Any(user.IsInMemberGroup);

    /// <summary>Shared access rules; a query that binds an identity still needs an account even when marked Public.</summary>
    private bool CanRead(DataSourceUser user) => DataSourceQueries.CanRead(definition, user)
        && (!user.IsAnonymous || Entry.Plan is { BindsUser: false });

    /// <summary>Writers: readers allowed by the anonymous-write setting and the configured write Member Groups.</summary>
    private bool CanWrite(DataSourceUser user) => (!user.IsAnonymous || definition.AllowAnonymousWrites && Entry.Plan is { WritesBindUser: false })
        && CanRead(user) && (definition.WriteGroups.Count == 0 || InAny(user, definition.WriteGroups));

    /// <summary>A record's files follow its groups: the write groups add, replace and remove them, whatever the allow flags say about the row itself.</summary>
    async ValueTask<bool> Attachments.IAttachmentOwner.MayWriteAttachmentsAsync(FormRecord record, FormExecutionContext context, CancellationToken ct)
        => await DataSourceQueries.ResolveReaderAsync(users, context, ct) is { } user && CanWrite(user);

    private async Task<bool> AllowedAsync(DataSourceAction action, DataSourceUser user, FormExecutionContext context, string? key,
        IReadOnlyDictionary<string, object?>? current, IReadOnlyDictionary<string, object?>? proposed, DbConnection? connection, DbTransaction? transaction,
        CancellationToken ct)
    {
        var request = new DataSourceRequest(definition.Alias, action, user, context, key, current, proposed, connection, transaction);
        foreach (var policy in policies)
            if (!await policy.IsAllowedAsync(request, ct)) return false;
        return true;
    }

    private DataSourceOperation Operation(DataSourcePlan plan, DataSourceAction action, DataSourceUser user, FormExecutionContext context,
        DbConnection? connection, DbTransaction? transaction, string? key = null, IReadOnlyDictionary<string, object?>? current = null,
        IDictionary<string, object?>? values = null, IReadOnlyList<DataSourceDetail>? details = null, IReadOnlyList<IDictionary<string, object?>>? rows = null)
        => new(definition.Alias, action, user, context, plan.Dialect.Kind) {
            Connection = connection, Transaction = transaction, Key = key, Current = current, Values = values, Details = details ?? [], Rows = rows,
        };

    /// <summary>Asks the backing class one hook, with the SQL helper bound to the operation; a connection the helper opened closes when it answers.</summary>
    private async Task<DataSourceOutcome> AskAsync(DataSourcePlan plan, DataSourceOperation operation, Binder binder,
        Func<IDataSourceHandler, DataSourceOperation, CancellationToken, ValueTask<DataSourceOutcome>> hook, CancellationToken ct)
    {
        if (Handler is not { } handler) return DataSourceOutcome.Continue;
        await using var sql = new DataSourceSql(plan, operation, binder.Value, token => runtime.OpenAsync(definition.Connection, token));
        operation.Sql = sql;
        return await hook(handler, operation, ct) ?? DataSourceOutcome.Continue;
    }

    /// <summary>What a form shows of the values a class left: each in its field's JSON shape. A name that is not a field is an error.</summary>
    private static Dictionary<string, object?> Shown(DataSourcePlan plan, IDictionary<string, object?> values)
    {
        var shown = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (alias, value) in values)
        {
            var column = plan.Field(alias) ?? throw new DataSourceException($"The backing class set {alias}, which is not a field of the data source.");
            shown[column.Field] = FieldValues.ToJson(Canonical(column, value));
        }
        return shown;
    }

    /// <summary>
    /// The row a write hook left. A value the class did not change keeps the engine's own; one it changed or added is converted and
    /// checked by its field's rules, as a submitted value is; a field it removed is left out. A class may write a read-only field of
    /// the written table, never the version, an audit column, or a column the table does not let anyone write.
    /// </summary>
    private static Dictionary<PlanColumn, object?> Written(DataSourcePlan plan, IReadOnlyDictionary<PlanColumn, object?> before,
        IReadOnlyDictionary<string, object?> shown, IDictionary<string, object?> after)
    {
        var row = new Dictionary<PlanColumn, object?>();
        foreach (var (alias, value) in after)
        {
            var column = plan.Field(alias) ?? throw new DataSourceException($"The backing class set {alias}, which is not a field of the data source.");
            if (before.TryGetValue(column, out var kept) && shown.TryGetValue(alias, out var original) && Equals(original, value)) { row[column] = kept; continue; }
            if (column.Target is not { Computed: false, RowVersion: false } || plan.IsAudit(column))
                throw new DataSourceFieldException(column.Field, "The backing class cannot set this field: the database or the engine fills it.");
            var canonical = Canonical(column, value);
            if (canonical is not null && !FieldValues.Check(canonical, column, out var error)) throw new DataSourceFieldException(column.Field, error!);
            row[column] = canonical;
        }
        return row;
    }

    private static object? Canonical(PlanColumn column, object? value)
    {
        var canonical = FieldValues.FromValue(value is JsonElement json ? JsonValue(json) : value, column.Kind, out var valid);
        if (!valid) throw new DataSourceFieldException(column.Field, "The backing class set a value this field cannot hold.");
        return canonical;
    }

    private static object? JsonValue(JsonElement json) => json.ValueKind switch {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => json.GetString(),
        _ => json.GetRawText(),
    };

    /// <summary>The key of a row the class wrote itself: the key it returned, or the key fields of the row when it returned none.</summary>
    private static object?[] HandledKey(DataSourcePlan plan, DataSourceOutcome outcome, IReadOnlyDictionary<PlanColumn, object?> row)
    {
        if (outcome.Key is { } key)
            return TryParseKey(plan, key, out var keys) ? keys : throw new DataSourceException($"The backing class returned '{key}', which is not a key of the data source.");
        if (plan.Keys.All(k => row.GetValueOrDefault(k) is not null)) return plan.Keys.Select(k => row[k]).ToArray();
        throw new DataSourceException("The backing class wrote the row without returning its key.");
    }

    /// <summary>A definition whose schema cannot be read right now; the plan is retried after a few seconds.</summary>
    private FormResult Unavailable()
    {
        runtime.Logger.LogWarning("Data source {Alias} refused a request because it is not available: {Problem}", definition.Alias, Problem);
        return new FormResult(503, Errors: new Dictionary<string, string[]> { [""] = ["This form's data source is not available. Try again later or ask an administrator."] });
    }

    /// <summary>
    /// The submitted values by field, checked as the engine's own rules say whatever the form already checked:
    /// unknown and read-only fields are refused, values must suit their kind, length, range and choices. On an
    /// update a value equal to the stored one, as loaded, is no change and is left out unchecked.
    /// </summary>
    private static Dictionary<PlanColumn, object?>? Parse(DataSourcePlan plan, JsonElement input, IReadOnlyDictionary<PlanColumn, object?>? stored,
        out FormResult? rejected)
    {
        rejected = null;
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var values = new Dictionary<PlanColumn, object?>();
        var seen = new HashSet<PlanColumn>();
        if (input.ValueKind != JsonValueKind.Object) errors[""] = ["Form values must be an object."];
        else
            foreach (var property in input.EnumerateObject())
            {
                var column = plan.Field(property.Name);
                if (column is null) errors[property.Name] = ["This field is not part of the data source."];
                else if (!seen.Add(column)) errors[property.Name] = ["Duplicate field."];
                else if (column.ReadOnly) errors[property.Name] = ["This field cannot be changed through this form."];
                else if (stored is not null && FieldValues.Unchanged(property.Value, stored.GetValueOrDefault(column))) continue;
                else
                {
                    var value = FieldValues.FromJson(property.Value, column, out var error);
                    if (error is not null) errors[column.Field] = [error];
                    else values[column] = value;
                }
            }
        if (errors.Count == 0) return values;
        rejected = new FormResult(422, Errors: errors);
        return null;
    }

    /// <summary>
    /// Reads a record key: one value, or for a key of several columns the values joined by '|', with '%' and '|'
    /// inside a value written %25 and %7C. A key that cannot be one is simply not found.
    /// </summary>
    private static bool TryParseKey(DataSourcePlan plan, string key, out object?[] values)
    {
        values = [];
        if (plan.Keys.Count == 0 || key.Length is 0 or > 1000) return false;
        var parts = plan.Keys.Count == 1 ? [key] : key.Split('|').Select(part => part.Replace("%7C", "|", StringComparison.OrdinalIgnoreCase).Replace("%25", "%")).ToArray();
        if (parts.Length != plan.Keys.Count) return false;
        values = new object?[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var column = plan.Keys[i];
            var value = FieldValues.FromValue(parts[i], column.Kind, out var valid);
            if (!valid || value is null || value is string { Length: 0 } || !FieldValues.Check(value, column with { Choices = null }, out _)) return false;
            try { plan.Dialect.ToDatabase(value, column.Kind, column.Target); }
            catch (DataSourceException) { return false; }
            values[i] = value;
        }
        return true;
    }

    private static string FormatKey(DataSourcePlan plan, IReadOnlyDictionary<PlanColumn, object?> values)
    {
        var parts = plan.Keys.Select(k => Convert.ToString(FieldValues.ToJson(values[k]), CultureInfo.InvariantCulture) ?? "").ToArray();
        return parts.Length == 1 ? parts[0] : string.Join('|', parts.Select(part => part.Replace("%", "%25").Replace("|", "%7C")));
    }

    /// <summary>No token for last-save-wins; a row checksum when opted in.</summary>
    private static string? Token(DataSourcePlan plan, Row row) => plan.Definition.Concurrency == DataSourceConcurrency.Checksum
        ? FieldValues.Checksum(plan.Columns.Select(c => (c.Column, row.Values[c]))) : null;

    private FormRecord Record(DataSourcePlan plan, Row row, DataSourceUser user)
    {
        var write = plan.Writable && CanWrite(user);
        return new(FormatKey(plan, row.Values), Token(plan, row), Json(plan, row.Values),
            new(Update: write && definition.AllowUpdate, Delete: write && definition.AllowDelete));
    }

    private static Dictionary<string, object?> Json(DataSourcePlan plan, IReadOnlyDictionary<PlanColumn, object?> values)
        => plan.Columns.Where(values.ContainsKey).ToDictionary(c => c.Field, c => FieldValues.ToJson(values[c]), StringComparer.Ordinal);

    private sealed record Row(IReadOnlyDictionary<PlanColumn, object?> Values);

    /// <summary>Fetches a row by key through the row restriction; reads at most two rows so a key that is not unique is noticed, not guessed.</summary>
    private static async Task<Row?> ReadAsync(DataSourcePlan plan, DbConnection connection, DbTransaction? transaction, object?[] keys, Binder binder, CancellationToken ct)
    {
        var parameters = new SqlParameters(plan.Dialect);
        var from = plan.From(parameters, binder.Value);
        var keyMatch = plan.Keys.Select((k, i) => (k.Column, parameters.Add(ToDatabase(plan, k, keys[i])))).ToArray();
        var restriction = plan.Restriction?.Render(parameters, binder.Value);
        await using var command = parameters.Command(connection, transaction, plan.Dialect.Select(from, plan.ReadColumns, keyMatch, restriction, limit: 2));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var values = new Dictionary<PlanColumn, object?>();
        for (var i = 0; i < plan.Columns.Count; i++)
        {
            var value = FieldValues.FromDatabase(reader.GetValue(i), plan.Columns[i].Kind);
            // A form shows a record; a cell larger than any field may hold is refused rather than sent or cut short.
            if (value is string { Length: > FieldKinds.MaxTextLength })
                throw new DataSourceException($"The value of {plan.Columns[i].Label} is larger than a form can hold.");
            values[plan.Columns[i]] = value;
        }
        if (await reader.ReadAsync(ct)) throw new DataSourceException("The record key matches more than one row; the data source's key columns must identify one row.");
        return new Row(values);
    }

    private static object? ToDatabase(DataSourcePlan plan, PlanColumn column, object? value)
    {
        try { return plan.Dialect.ToDatabase(value, column.Kind, column.Target); }
        catch (DataSourceException error) { throw new DataSourceFieldException(column.Field, error.Message); }
    }

    private static async Task LockAsync(DataSourcePlan plan, DbConnection connection, DbTransaction transaction, object?[] keys, CancellationToken ct)
    {
        var parameters = new SqlParameters(plan.Dialect);
        var match = plan.Keys.Select((k, i) => (k.Target!.Name, parameters.Add(ToDatabase(plan, k, keys[i])))).ToArray();
        if (plan.Dialect.Lock(plan.Target!.Name, match) is not { } text) return;
        await using var command = parameters.Command(connection, transaction, text);
        await command.ExecuteScalarAsync(ct);
    }

    /// <summary>Runs a generated update or delete and returns the rows it changed itself, not counting what triggers do.</summary>
    private static async Task<int> CountAsync(DataSourcePlan plan, SqlParameters parameters, DbConnection connection, DbTransaction transaction, string statement,
        CancellationToken ct)
    {
        await using var command = parameters.Command(connection, transaction, plan.Dialect.CountRows(statement));
        return plan.Dialect.CountsRowsBySelect ? Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) : await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The written table's key and the row restriction seen through the row source.</summary>
    private static (IReadOnlyList<(string, string)> Keys, string? Exists) Where(DataSourcePlan plan, SqlParameters parameters,
        object?[] keys, Binder binder)
    {
        var markers = plan.Keys.Select((k, i) => parameters.Add(ToDatabase(plan, k, keys[i]))).ToArray();
        var targetKeys = plan.Keys.Select((k, i) => (k.Target!.Name, markers[i])).ToArray();
        string? exists = null;
        if (plan.Restriction is { } restriction)
        {
            var from = plan.From(parameters, binder.Value);
            exists = plan.Dialect.Exists(from, plan.Keys.Select((k, i) => (k.Column, markers[i])).ToArray(), restriction.Render(parameters, binder.Value));
        }
        return (targetKeys, exists);
    }

    /// <summary>The generated update: changed fields, audit columns, and a WHERE that only the read row passes.</summary>
    private static Task<int> UpdateRowAsync(DataSourcePlan plan, DbConnection connection, DbTransaction transaction, object?[] keys,
        IEnumerable<KeyValuePair<PlanColumn, object?>> changes, DataSourceUser user, DateTime now, Binder binder, CancellationToken ct)
    {
        var parameters = new SqlParameters(plan.Dialect);
        var set = changes.Select(c => (c.Key.Target!.Name, parameters.Add(ToDatabase(plan, c.Key, c.Value)))).ToList();
        if (plan.UpdatedBy is { } by) set.Add((by.Target!.Name, parameters.Add(ToDatabase(plan, by, user.Name))));
        if (plan.UpdatedAt is { } at) set.Add((at.Target!.Name, parameters.Add(ToDatabase(plan, at, FieldValues.FromValue(now, at.Kind, out _)))));
        var (targetKeys, exists) = Where(plan, parameters, keys, binder);
        return CountAsync(plan, parameters, connection, transaction, plan.Dialect.Update(plan.Target!.Name, set, targetKeys, exists), ct);
    }

    /// <summary>The generated insert; a key the database assigns comes back through RETURNING or OUTPUT INSERTED.</summary>
    private static async Task<object?[]> InsertAsync(DataSourcePlan plan, DbConnection connection, DbTransaction transaction,
        IReadOnlyDictionary<PlanColumn, object?> row, CancellationToken ct)
    {
        var parameters = new SqlParameters(plan.Dialect);
        var values = row.Where(r => r.Key.Target is { Computed: false, RowVersion: false })
            .Select(r => (r.Key.Target!.Name, parameters.Add(ToDatabase(plan, r.Key, r.Value)))).ToArray();
        var assigned = plan.Keys.Where(k => !row.ContainsKey(k)).Select(k => k.Target!).ToArray();
        await using var command = parameters.Command(connection, transaction, plan.Dialect.Insert(plan.Target!.Name, values, assigned));
        var returned = await plan.Dialect.ExecuteInsertAsync(command, assigned, ct);
        var keys = plan.Keys.Select(k => row.GetValueOrDefault(k)).ToArray();
        foreach (var (column, ordinal) in assigned.Select((c, i) => (c, i)))
        {
            var index = plan.Keys.ToList().FindIndex(k => k.Target == column);
            keys[index] = FieldValues.FromDatabase(returned[ordinal], plan.Keys[index].Kind);
        }
        return keys;
    }

    /// <summary>
    /// The editor's insert statement, run whole. The new key is the record's own key fields when the form supplies
    /// them; otherwise the statement returns it as its first value (RETURNING, OUTPUT INSERTED or a final SELECT).
    /// </summary>
    private static async Task<object?[]> InsertCustomAsync(DataSourcePlan plan, SqlTemplate statement, DbConnection connection, DbTransaction transaction,
        IReadOnlyDictionary<PlanColumn, object?> row, Binder binder, CancellationToken ct)
    {
        var parameters = new SqlParameters(plan.Dialect);
        var text = statement.Render(parameters, binder.Value);
        if (plan.Dialect is OracleDialect oracle && plan.Keys.Any(k => row.GetValueOrDefault(k) is null))
        {
            var assigned = plan.Keys.Where(k => row.GetValueOrDefault(k) is null).ToArray();
            var returning = assigned.Select(k => k.Target!).ToArray();
            await using var returningCommand = parameters.Command(connection, transaction, oracle.WithReturning(text, returning));
            var returned = await oracle.ExecuteInsertAsync(returningCommand, returning, ct);
            return plan.Keys.Select(k => row.GetValueOrDefault(k) ?? FieldValues.FromDatabase(returned[Array.IndexOf(assigned, k)], k.Kind)).ToArray();
        }
        await using var command = parameters.Command(connection, transaction, text);
        if (plan.Keys.All(k => row.GetValueOrDefault(k) is not null))
        {
            await command.ExecuteNonQueryAsync(ct);
            return plan.Keys.Select(k => row[k]).ToArray();
        }
        if (plan.Keys.Count != 1) throw new DataSourceException("A custom insert for a key of several columns needs every key field in the form.");
        var key = FieldValues.FromDatabase(await command.ExecuteScalarAsync(ct), plan.Keys[0].Kind)
            ?? throw new DataSourceException("The custom insert statement did not return the new record's key.");
        return [key];
    }

    /// <summary>The values of the bind variables for one operation: the caller's facts, the request's parameters, the clock and the record's fields.</summary>
    private sealed class Binder(DataSourcePlan plan, DataSourceUser user, FormExecutionContext context, DateTime now, IReadOnlyDictionary<PlanColumn, object?>? fields, IReadOnlyDictionary<string, object?>? app)
    {
        public Binder With(IReadOnlyDictionary<PlanColumn, object?> values) => new(plan, user, context, now, values, app);

        public object? Value(BindVariable variable) => variable.Kind switch {
            BindVariableKind.UserSid => user.Sid,
            BindVariableKind.UserName => user.Name,
            BindVariableKind.UserKey => user.Key,
            BindVariableKind.UserEmail => user.Email,
            BindVariableKind.UserGroups => user.Groups,
            BindVariableKind.Now => plan.Dialect.Now(now),
            BindVariableKind.Param => Parameter(variable.Name!),
            BindVariableKind.App => app?.GetValueOrDefault(variable.Name!),
            BindVariableKind.Arg => null,
            _ => Field(variable.Name!) is { } column && fields?.TryGetValue(column, out var value) == true ? ToDatabase(plan, column, value) : null,
        };

        /// <summary>An insert default's value as the canonical value of its field; invalid when a parameter or field does not convert.</summary>
        public object? Default(InsertValue value, out bool valid)
        {
            valid = true;
            if (value.Variable is not { } variable) return value.Literal;
            var raw = variable.Kind switch {
                BindVariableKind.Now => now,
                BindVariableKind.Param => Parameter(variable.Name!),
                BindVariableKind.App => app?.GetValueOrDefault(variable.Name!),
                BindVariableKind.Field => Field(variable.Name!) is { } column && fields?.TryGetValue(column, out var current) == true ? FieldValues.ToJson(current) : null,
                BindVariableKind.UserSid => user.Sid,
                BindVariableKind.UserName => user.Name,
                BindVariableKind.UserKey => user.Key,
                BindVariableKind.UserEmail => user.Email,
                _ => null,
            };
            var result = FieldValues.FromValue(raw is bool flag ? (flag ? "true" : "false") : raw, value.Column.Kind, out valid);
            if (valid && result is not null && !FieldValues.Check(result, value.Column, out _)) valid = false;
            return valid ? result : null;
        }

        private string? Parameter(string name) => context.Parameters?.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        private PlanColumn? Field(string alias) => plan.Columns.FirstOrDefault(c => c.Field.Equals(alias, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>A value that cannot be written to its field's column, reported against that field.</summary>
internal sealed class DataSourceFieldException(string field, string message) : Exception(message)
{
    public string Field { get; } = field;
}
