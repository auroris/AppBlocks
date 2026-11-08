using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AppBlocks.Forms;

namespace AppBlocks.DataSources;

/// <summary>
/// One detail block's part of a master/detail save: the block, its data source, the field of it that holds the master's key, the
/// changes, how many rows the block lists, and the block's own context, with the block's key as the form key and the batch's own
/// submission key.
/// </summary>
public sealed record DataSourceDetailBatch(Guid Block, SqlDataSource Source, string LinkField, IReadOnlyList<DataSourceChange> Changes, int MaximumRows,
    FormExecutionContext Context);

/// <summary>
/// What a master/detail save answers: the master's result and, on success, each detail block's rows as they are afterwards, in the
/// order the batches came. When a detail batch refused, <see cref="Detail"/> is its position and <see cref="Refusal"/> names the
/// change; nothing was written either way.
/// </summary>
public sealed record DataSourceSaveResult(FormResult Master, IReadOnlyList<DataSourceRowsResult>? Details = null, int? Detail = null, DataSourceRowsResult? Refusal = null)
{
    public int Status => Refusal?.Status ?? Master.Status;
}

/// <summary>
/// A master record and the rows of its detail blocks, saved in one transaction on one connection, as an Oracle APEX page saves a
/// form and its interactive grids. The master is written first, so a new master's key becomes the link value of its new rows; its
/// backing class sees every detail batch and may apply, change or refuse changes; the engine applies the rest with the checks a
/// tabular save makes, and one commit keeps all of it or none.
/// </summary>
public sealed partial class SqlDataSource
{
    /// <summary>Whether a detail data source runs on this one's connection, so the two can share a transaction.</summary>
    public bool SharesConnectionWith(SqlDataSource other) => string.Equals(definition.Connection, other.Definition.Connection, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Creates the master (<paramref name="key"/> null) or updates it, then applies each detail batch to the rows linked to it, all in
    /// one transaction. Each detail data source must share this one's connection. A retry with the same submission keys and the same
    /// values answers as the first save did and writes nothing again.
    /// </summary>
    public async Task<DataSourceSaveResult> SaveWithDetailsAsync(string? key, JsonElement input, string? checksum, IReadOnlyList<DataSourceDetailBatch> details,
        FormExecutionContext context, CancellationToken ct)
    {
        if (Ready is not { } plan) return new(Unavailable());
        var creating = key is null;
        if (await DataSourceQueries.ResolveReaderAsync(users, context, ct) is not { } user || !plan.Writable || !CanWrite(user) || !(creating ? definition.AllowInsert : definition.AllowUpdate))
            return new(FormResult.Denied());
        if (!Guid.TryParse(context.SubmissionKey, out var submission)) return new(FormResult.Invalid("", "A submission identifier is required."));
        if (details.FirstOrDefault(d => !SharesConnectionWith(d.Source)) is { } other)
            throw new ArgumentException($"The detail data source {other.Source.Alias} does not share {Alias}'s connection.", nameof(details));
        object?[] keys = [];
        Dictionary<PlanColumn, object?>? values = null;
        if (creating)
        {
            if (Parse(plan, input, null, out var rejected) is not { } parsed) return new(rejected!);
            values = parsed;
        }
        else if (!TryParseKey(plan, key!, out keys)) return new(FormResult.Missing());
        else if (input.ValueKind != JsonValueKind.Object) return new(FormResult.Invalid("", "Form values must be an object."));

        var batches = new List<PreparedBatch>(details.Count);
        for (var i = 0; i < details.Count; i++)
        {
            var prepared = await details[i].Source.PrepareAsync(details[i], ct);
            if (prepared.Refusal is { } refusal) return new(new(refusal.Status, Errors: refusal.Errors), Detail: i, Refusal: refusal);
            batches.Add(prepared);
        }
        // A new master's receipt covers its details too, so a retry is the same save or none.
        var receipt = creating ? $"{definition.Key:N}:{user.Key}:{context.PageKey:N}:{context.FormKey:N}:{submission:N}" : null;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            master = creating ? Json(plan, values!).OrderBy(p => p.Key, StringComparer.Ordinal).ToArray() : null,
            details = batches.Select(b => b.Hash).ToArray(),
        }))));
        using var gate = await runtime.Receipts.EnterAsync(batches.Select(b => b.Receipt).Concat(receipt is null ? [] : [receipt]), ct);
        if (receipt is not null && runtime.Receipts.Find(receipt) is { } earlier)
            return earlier.Hash == hash ? await AnswerAsync(await LoadAsync(earlier.Key, context with { SubmissionKey = null }, ct), batches, ct)
                : new(FormResult.Invalid("", "This submission was already used with different values. Reload the form."));
        for (var i = 0; i < batches.Count; i++)
        {
            if (runtime.Receipts.Find(batches[i].Receipt) is not { } applied) continue;
            // This batch was saved by an earlier attempt that lost its answer; it is not applied twice.
            if (applied.Hash == batches[i].Hash) { batches[i].Done = true; continue; }
            var reused = new DataSourceRowsResult(422, Errors: new Dictionary<string, string[]> { [""] = ["This submission was already used with different rows. Reload the page."] });
            return new(new(422, Errors: reused.Errors), Detail: i, Refusal: reused);
        }

        var now = runtime.Now();
        var app = await AppAsync(context, ct);
        var shared = batches.Select(b => b.Detail).ToArray();
        foreach (var detail in shared) detail.MasterKey = key;
        (int Index, DataSourceRowsResult Refusal)? failed = null;
        var master = await RunAsync(plan, write: true, async (connection, transaction) => {
            var written = creating
                ? await InsertRecordAsync(plan, user, context, values!, now, connection, transaction!, app, ct, shared)
                : await UpdateRecordAsync(plan, user, context, key!, keys, input, checksum, now, connection, transaction!, null, app, ct, shared);
            if (written.Status != 200) return written;
            for (var i = 0; i < batches.Count; i++)
            {
                batches[i].Detail.MasterKey = written.Record!.Key;
                if (batches[i].Done) continue;
                // Returning without a commit rolls the master and every earlier batch back.
                if (await batches[i].Source.ApplyBatchAsync(batches[i], written.Record.Key!, now, connection, transaction!, ct) is { } refusal)
                {
                    failed = (i, refusal);
                    return new FormResult(refusal.Status, Errors: refusal.Errors);
                }
            }
            await transaction!.CommitAsync(ct);
            return written;
        }, ct);
        if (failed is { } failure) return new(master, Detail: failure.Index, Refusal: failure.Refusal);
        if (master.Status != 200) return new(master);
        if (receipt is not null) runtime.Receipts.Add(receipt, master.Record!.Key!, hash);
        foreach (var batch in batches.Where(b => !b.Done)) runtime.Receipts.Add(batch.Receipt, "rows", batch.Hash);
        return await AnswerAsync(master, batches, ct);
    }

    /// <summary>The master's result with each detail block's rows as they are now, read after the commit.</summary>
    private static async Task<DataSourceSaveResult> AnswerAsync(FormResult master, IReadOnlyList<PreparedBatch> batches, CancellationToken ct)
    {
        if (master.Record?.Key is not { } key) return new(master);
        var rows = new List<DataSourceRowsResult>(batches.Count);
        foreach (var batch in batches)
            rows.Add(await batch.Source.ListRowsAsync(new DataSourceScope(batch.LinkField, key), batch.Context with { SubmissionKey = null }, batch.MaximumRows, ct));
        return new(master, rows);
    }

    /// <summary>A detail batch with the checks a tabular save makes before its transaction already made, ready to apply in the master's.</summary>
    internal sealed class PreparedBatch(SqlDataSource source, DataSourcePlan plan, DataSourceUser user, DataSourceDetailBatch batch, string receipt, string hash,
        IReadOnlyDictionary<string, object?>? app)
    {
        public SqlDataSource Source => source;
        public DataSourcePlan Plan => plan;
        public DataSourceUser User => user;
        public FormExecutionContext Context => batch.Context;
        public string LinkField => batch.LinkField;
        public int MaximumRows => batch.MaximumRows;
        public IReadOnlyList<DataSourceChange> Original => batch.Changes;
        /// <summary>What the master's backing class sees and may change.</summary>
        public DataSourceDetail Detail { get; } = new(batch.Block, source.Alias, batch.LinkField, batch.Changes.ToList());
        public string Receipt => receipt;
        public string Hash => hash;
        public IReadOnlyDictionary<string, object?>? App => app;
        /// <summary>An earlier attempt saved this batch and lost its answer.</summary>
        public bool Done { get; set; }
        public DataSourceRowsResult? Refusal { get; init; }
    }

    /// <summary>Checks a detail batch as <see cref="SaveRowsAsync"/> does before its transaction: the caller, the link field, the size, the submission and the allow flags.</summary>
    internal async Task<PreparedBatch> PrepareAsync(DataSourceDetailBatch batch, CancellationToken ct)
    {
        PreparedBatch Refused(DataSourceRowsResult refusal) => new(this, null!, null!, batch, "", "", null) { Refusal = refusal };
        if (Ready is not { } plan) return Refused(DataSourceRowsResult.From(Unavailable()));
        if (await DataSourceQueries.ResolveReaderAsync(users, batch.Context, ct) is not { } user || !plan.Writable || !CanWrite(user)) return Refused(new(403));
        if (plan.Columns.All(c => !c.Field.Equals(batch.LinkField, StringComparison.OrdinalIgnoreCase)))
            return Refused(DataSourceRowsResult.From(FormResult.Invalid("", $"'{batch.LinkField}' is not a field of the data source.")));
        if (batch.Changes.Count > MaxChanges) return Refused(new(422, Errors: new Dictionary<string, string[]> { [""] = [$"Save at most {MaxChanges} rows at a time."] }));
        if (!Guid.TryParse(batch.Context.SubmissionKey, out var submission))
            return Refused(new(422, Errors: new Dictionary<string, string[]> { [""] = ["A submission identifier is required."] }));
        if (Unaccepted(batch.Changes) is { } unaccepted) return Refused(unaccepted);
        var (receipt, hash) = BatchReceipt(user, batch.Context, submission, batch.Changes);
        return new(this, plan, user, batch, receipt, hash, await AppAsync(batch.Context, ct));
    }

    /// <summary>
    /// Applies what is left of a prepared batch, after the master's backing class, to the rows linked to the master inside its
    /// transaction; the first refusal, numbered by its place in the batch as sent, or null when every change was applied.
    /// </summary>
    internal async Task<DataSourceRowsResult?> ApplyBatchAsync(PreparedBatch batch, string masterKey, DateTimeOffset now, DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        var plan = batch.Plan;
        if (Link(plan, new DataSourceScope(batch.LinkField, masterKey), out var link, out var value) is { } problem) return DataSourceRowsResult.From(problem);
        // The master's class may have applied changes itself, changed others or added more; what is left gets the same checks.
        var changes = batch.Detail.Changes.ToArray();
        int? Sent(int i) => batch.Original.Select((change, index) => (change, index)).FirstOrDefault(p => ReferenceEquals(p.change, changes[i])) is { change: not null } found ? found.index : null;
        if (changes.Length > MaxChanges) return new(422, Errors: new Dictionary<string, string[]> { [""] = [$"Save at most {MaxChanges} rows at a time."] });
        if (Unaccepted(changes) is { } unaccepted) return unaccepted with { Index = Sent(unaccepted.Index!.Value) };
        return await ApplyAllAsync(plan, batch.User, batch.Context, link, value, changes, Sent, now, connection, transaction, batch.App, ct);
    }
}
