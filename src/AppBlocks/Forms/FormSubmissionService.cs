using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Options;
using AppBlocks.AntiSpam;
using AppBlocks.Blocks;
using AppBlocks.DataSources;
using AppBlocks.Tabular;

namespace AppBlocks.Forms;

internal enum FormOperation { Create, Update, Delete }
internal sealed record FormSubmissionResult(int Status, object? Body = null);

/// <summary>Validates and saves an admitted form submission, including its detail rows. HTTP admission and antiforgery belong to the caller.</summary>
internal sealed class FormSubmissionService(AntiSpamGate antiSpam, ListOfValuesService lists, FormResourceRegistry resources,
    IOptions<TabularOptions> limits, ILoggerFactory logs)
{
    private readonly ILogger logger = logs.CreateLogger("ApplicationForms");
    private readonly ILogger tabularLogger = logs.CreateLogger("ApplicationTabular");
    private static FormSubmissionResult Result(FormResult result) => new(result.Status, result.Status == 204 ? null : result);

    public async Task<FormSubmissionResult> SubmitAsync(FormDefinition form, string? key, FormSubmission submission, FormOperation operation,
        FormExecutionContext context, Func<string?> antiforgeryToken, CancellationToken ct)
    {
        var member = context.Member;
        var pageKey = context.PageKey;
        if (operation == FormOperation.Delete) {
            // Nothing to check on a delete, which only ever acts on a record the class allows; its token still stops here.
            context = context with { FormData = antiSpam.WithoutChecks(form, context.FormData) };
            var current = await form.Resource.LoadAsync(key!, context, ct);
            if (current.Record is null) return Result(current);
            if (current.Record.Checksum is { } expected && expected != submission.Checksum) return Result(FormResult.Conflict());
            var deletion = await form.Resource.DeleteAsync(key!, submission.Checksum, context, ct);
            Audit(operation, form.Resource.Alias, key, member, deletion.Status);
            // The runtime reads only an empty answer as "the record is gone", so a class that reports success with no record means just that.
            return deletion is { Status: >= 200 and < 300, Record: null } ? new(204) : Result(deletion);
        }
        var errors = form.Validate(submission.Values, key is null);
        if (errors.Count > 0) return Result(new(422, Errors: errors));
        // The rows sent with the record, checked as the tabular endpoint checks a batch before anything is written.
        IReadOnlyList<FormDetail>? details = null;
        if (submission.Details is { Count: > 0 } sent)
        {
            var (resolved, unresolved) = await DetailsAsync(form, sent, member, pageKey, context, ct);
            if (unresolved is not null) return unresolved;
            details = resolved;
        }
        // After the fields, so a typo does not spend a token that works once, and before anything can reach the class, the load of an update included.
        var check = await antiSpam.CheckAsync(form, context.FormData, ct);
        if (check.Refusal is { } refusal) {
            Audit(operation, form.Resource.Alias, key, member, refusal.Status);
            return Result(refusal);
        }
        context = context with { FormData = check.FormData };
        var values = submission.Values;
        FormRecord? stored = null;
        if (key is not null) {
            var current = await form.Resource.LoadAsync(key, context, ct);
            if (current.Record is null) return Result(current);
            stored = current.Record;
            if (stored.Checksum is { } expected && expected != submission.Checksum) return Result(FormResult.Conflict());
            // Omission preserves the current value. Explicit null remains a deliberate change.
            var merged = current.Record.Values.Where(p => form.Resource.Fields.Any(f => f.Key == p.Key && !f.ReadOnly)).ToDictionary(p => p.Key, p => p.Value);
            foreach (var value in values.EnumerateObject()) merged[value.Name] = value.Value.Clone();
            var writableBindings = form.Fields.Where(f => f.Content.ContentType.Alias != "appDisplay" && !f.Content.Value<bool>("readOnly"))
                .Select(f => f.Content.Value<string>("field")!).ToHashSet(StringComparer.Ordinal);
            var mergedErrors = form.Validate(JsonSerializer.SerializeToElement(merged.Where(p => writableBindings.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value)), false);
            if (mergedErrors.Count > 0) return Result(new(422, Errors: mergedErrors));
            values = JsonSerializer.SerializeToElement(merged);
        }
        var choiceErrors = await lists.ValidateAsync(form, values, context, ct, stored?.Values);
        if (choiceErrors.Count > 0) return Result(new(422, Errors: choiceErrors));
        if (details is not null) return await SaveWithDetailsAsync(form, key, values, submission.Checksum, details, context, operation, member, antiforgeryToken, ct);
        var result = key is null ? await form.Resource.CreateAsync(values, context, ct)
            : await form.Resource.UpdateAsync(key, values, submission.Checksum, context, ct);
        Audit(operation, form.Resource.Alias, key ?? result.Record?.Key, member, result.Status);
        return Result(result.Record is null ? result : result with { Record = form.Project(result.Record) });
    }

    /// <summary>One tabular block of a submission, resolved: the block, its data source, its changes as the engine takes them and its own context.</summary>
    private sealed record FormDetail(Guid Key, TabularBlock Block, SqlDataSource Source, List<DataSourceChange> Changes, FormExecutionContext Context);

    /// <summary>
    /// A refusal of a detail batch, naming the block and, when one change caused it, that change's position. When the record and earlier
    /// blocks were saved before it, as without a shared transaction, it carries the saved record and those blocks' rows.
    /// </summary>
    private static FormSubmissionResult DetailRefusal(Guid block, int status, int? index, IReadOnlyDictionary<string, string[]>? errors, FormRecord? record = null,
        IReadOnlyList<object>? saved = null)
        => new(status, new { status, block, index, errors = errors ?? new Dictionary<string, string[]>(), record, details = saved });

    private static FormSubmissionResult DetailRefusal(Guid block, int status, string message) => DetailRefusal(block, status, null, new Dictionary<string, string[]> { [""] = [message] });

    /// <summary>
    /// Resolves the tabular blocks a submission sends rows for: each a tabular block of this form, owned by the form's record, once;
    /// its data source available and writable to the caller, at the revision the rows were listed under; and its changes ones the
    /// block accepts. The engine makes the per-change checks a tabular save makes, in the save.
    /// </summary>
    private async Task<(IReadOnlyList<FormDetail>? Details, FormSubmissionResult? Refusal)> DetailsAsync(FormDefinition form, IReadOnlyList<FormDetailSubmission> sent,
        ClaimsPrincipal member, Guid pageKey, FormExecutionContext context, CancellationToken ct)
    {
        var blocks = AppBlockContent.Flatten(form.Block.Areas.SelectMany(a => a)).Where(b => b.Content.ContentType.Alias == AppBlockSchema.TabularAlias).ToArray();
        var details = new List<FormDetail>(sent.Count);
        foreach (var detail in sent)
        {
            if (detail is null) return (null, new FormSubmissionResult(400));
            if (details.Any(d => d.Key == detail.Block)) return (null, DetailRefusal(detail.Block, 422, "The rows of a block are sent once."));
            if (blocks.FirstOrDefault(b => b.ContentKey == detail.Block) is not { } item) return (null, DetailRefusal(detail.Block, 422, "This form has no such Tabular form block."));
            var block = TabularBlock.Read(item.Content, limits.Value.MaxRows);
            if (!block.OwnedByForm) return (null, DetailRefusal(detail.Block, 422, "This Tabular form block names its own owner, so its rows save on their own."));
            if (await resources.FindAsync(block.DataSource) is not SqlDataSource source || block.LinkField.Length == 0)
                return (null, DetailRefusal(detail.Block, 422, $"The data source {block.DataSource} is not a published data source."));
            if (source.Problem is { } problem) return (null, DetailRefusal(detail.Block, 503, $"The data source {block.DataSource} is not available: {problem}"));
            if (block.ReadOnly || !await source.MayWriteAsync(member, ct)) return (null, DetailRefusal(detail.Block, 403, "This tabular form is read only."));
            if (detail.Revision != TabularOperations.Revision(source)) return (null, DetailRefusal(detail.Block, 409, "These rows have changed. Reload the page before saving."));
            if (detail.Changes is null) return (null, new FormSubmissionResult(400));
            if (TabularOperations.Changes(block, source, detail.Changes, out var changes) is { } refused)
                return (null, DetailRefusal(detail.Block, refused.Status, refused.Index, new Dictionary<string, string[]> { [refused.Field] = [refused.Message] }));
            details.Add(new(detail.Block, block, source, changes!, new FormExecutionContext(member, pageKey, detail.Block, TabularOperations.Revision(source),
                detail.SubmissionKey ?? context.SubmissionKey, context.Parameters)));
        }
        return (details, null);
    }

    /// <summary>
    /// The record and its blocks' rows. A form on a data source whose blocks' data sources share its connection saves them in one
    /// transaction. Otherwise nothing can be shared, so the record is saved first and then each block's rows, each batch whole or not at
    /// all: a block that fails leaves the record and any earlier block saved, and the answer carries the saved record with the refusal.
    /// </summary>
    private async Task<FormSubmissionResult> SaveWithDetailsAsync(FormDefinition form, string? key, JsonElement values, string? checksum, IReadOnlyList<FormDetail> details,
        FormExecutionContext context, FormOperation operation, ClaimsPrincipal member, Func<string?> antiforgeryToken, CancellationToken ct)
    {
        FormResult result;
        var answers = new List<object>(details.Count);
        if (form.Resource is SqlDataSource master && details.All(d => master.SharesConnectionWith(d.Source)))
        {
            var saved = await master.SaveWithDetailsAsync(key, values, checksum, details.Select(d => new DataSourceDetailBatch(d.Key, d.Source, d.Block.LinkField, d.Changes,
                d.Block.MaxRows, d.Context)).ToArray(), context, ct);
            result = saved.Master;
            Audit(operation, form.Resource.Alias, key ?? result.Record?.Key, member, saved.Status);
            if (saved.Refusal is { } refusal)
            {
                var failed = details[saved.Detail!.Value];
                TabularOperations.Audit(tabularLogger, "save", failed.Source.Alias, form.Resource.Alias, key, member, refusal.Status);
                return DetailRefusal(failed.Key, refusal.Status, refusal.Index, refusal.Errors);
            }
            if (result.Status != 200 || result.Record is null) return Result(result);
            for (var i = 0; i < details.Count; i++)
            {
                TabularOperations.Audit(tabularLogger, "save", details[i].Source.Alias, form.Resource.Alias, result.Record.Key, member, 200);
                answers.Add(await DetailAnswerAsync(details[i], form, result.Record.Key, saved.Details![i], member, antiforgeryToken, ct));
            }
            return new(200, new { record = form.Project(result.Record), details = answers });
        }

        result = key is null ? await form.Resource.CreateAsync(values, context, ct) : await form.Resource.UpdateAsync(key, values, checksum, context, ct);
        Audit(operation, form.Resource.Alias, key ?? result.Record?.Key, member, result.Status);
        if (result.Status != 200 || result.Record is null) return Result(result);
        var record = form.Project(result.Record);
        if (result.Record.Key is not { } owner)
            return DetailRefusal(details[0].Key, 422, null, new Dictionary<string, string[]> { [""] = ["This record keeps no key, so its rows cannot be saved with it."] }, record);
        foreach (var detail in details)
        {
            var rows = await detail.Source.SaveRowsAsync(new DataSourceScope(detail.Block.LinkField, owner), detail.Changes, detail.Context, detail.Block.MaxRows, ct);
            TabularOperations.Audit(tabularLogger, "save", detail.Source.Alias, form.Resource.Alias, owner, member, rows.Status);
            if (rows.Status != 200) return DetailRefusal(detail.Key, rows.Status, rows.Index, rows.Errors, record, answers);
            answers.Add(await DetailAnswerAsync(detail, form, owner, rows, member, antiforgeryToken, ct));
        }
        return new(200, new { record, details = answers });
    }

    /// <summary>A block's rows as its own endpoint answers them, with the block's key.</summary>
    private static async Task<object> DetailAnswerAsync(FormDetail detail, FormDefinition form, string? owner, DataSourceRowsResult rows, ClaimsPrincipal member,
        Func<string?> antiforgeryToken, CancellationToken ct)
        => new { block = detail.Key, list = await TabularOperations.Answer(detail.Block, detail.Source, member, form.Resource.Alias, owner, rows, antiforgeryToken, ct) };

    private void Audit(FormOperation operation, string resource, string? key, ClaimsPrincipal member, int status)
        => logger.LogInformation(
            "Form operation {Operation} on {Resource}/{Key} by {Member} returned {Status}", operation.ToString().ToLowerInvariant(), resource, key,
            member.FindFirstValue(ClaimTypes.NameIdentifier) ?? BlockOwnerAccess.AnonymousActor, status);
}
