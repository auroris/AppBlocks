using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using AppBlocks.AntiSpam;
using AppBlocks.Blocks;
using AppBlocks.DataSources;
using AppBlocks.Tabular;

namespace AppBlocks.Forms;

/// <param name="Checksum">The record's checksum as the browser loaded it, null under last-save-wins; an update or delete compares it with the current row.</param>
/// <param name="FormData">The form's other named controls by name, apart from <paramref name="Values"/> so validation and strict binding never see them.</param>
/// <param name="Details">The pending changes of the form's tabular blocks, saved with the record in the same request.</param>
public sealed record FormSubmission(string DefinitionVersion, string? Checksum, JsonElement Values, string? SubmissionKey, JsonElement? FormData = null,
    IReadOnlyList<FormDetailSubmission>? Details = null);

/// <summary>
/// A tabular block's pending changes, sent with its form's record so the two save together: in one transaction when the form is on
/// a data source and the block's data source shares its connection, otherwise the record first and then the rows.
/// </summary>
/// <param name="Block">The tabular block's key, inside this form.</param>
/// <param name="Revision">The revision its rows were listed under, so a changed data source is noticed before anything is written.</param>
/// <param name="SubmissionKey">The block's own key for this batch, so a retry does not write it twice; the form's when absent.</param>
/// <param name="Changes">The changes in the shape <c>/api/tabular</c> takes.</param>
public sealed record FormDetailSubmission(Guid Block, string? Revision, string? SubmissionKey, IReadOnlyList<TabularChange>? Changes);

public static class FormEndpoints
{
    /// <summary>
    /// The largest form submission, in bytes, unless <c>AppBlocks:Forms:MaxBodySize</c> says otherwise. A submission is parsed
    /// before any other check, so this bounds what an anonymous client can make the server read. A megabyte leaves room for
    /// long text fields beside the other controls at their limits; files go through Attachments, which has limits of its own.
    /// </summary>
    public const long DefaultMaxBodySize = 1024 * 1024;

    public static void MapApplicationForms(this IEndpointRouteBuilder endpoints)
    {
        var limit = endpoints.ServiceProvider.GetService<IConfiguration>()?.GetValue<long?>("AppBlocks:Forms:MaxBodySize") ?? DefaultMaxBodySize;
        var group = endpoints.MapGroup("/api/forms/{pageKey:guid}/{formKey:guid}").WithMetadata(new RequestSizeLimitAttribute(limit));
        group.MapGet("", Load);
        group.MapPost("", Create);
        group.MapPut("/{key}", Update);
        group.MapDelete("/{key}", Delete);
    }

    private static async Task<(FormDefinition? Form, ClaimsPrincipal? Member)> Resolve(Guid pageKey, Guid formKey, HttpContext http)
    {
        if (await http.RequestServices.GetRequiredService<AppBlockContent>().PageAsync(pageKey) is not { } page) return (null, null);
        // On an open page an anonymous visitor reaches the form's class, which decides from here on.
        var member = await http.RequestServices.GetRequiredService<AppPageAccess>().VisitorAsync(page);
        if (member is null) return (null, null);
        // Bind antiforgery tokens to the actual Umbraco Member or backoffice User, or to nobody.
        http.User = member;
        return (await FormDefinition.ResolveAsync(page, formKey, http.RequestServices.GetRequiredService<FormResourceRegistry>(), http.RequestServices.GetRequiredService<PublishedDataCatalog>(), AppBlockContent.RequestCulture(http)), member);
    }

    private static IResult Result(FormResult result) => result.Status == 204 ? Results.NoContent() : Results.Json(result, statusCode: result.Status);

    /// <summary>
    /// The request's query parameters, which data sources bind as <c>:PARAM.name</c>. Bounded like any other
    /// input: at most 50 names of identifier characters, one value each of at most 4000 characters. A repeated
    /// name is ambiguous and left out rather than guessed. The culture parameter, and any <paramref name="reserved"/>
    /// name an endpoint reads for itself, are not parameters. Paper reports bind theirs the same way.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Parameters(IQueryCollection query, params string[] reserved)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in query)
        {
            if (parameters.Count == 50) break;
            if (name.Equals(AppBlockContent.CultureParameter, StringComparison.OrdinalIgnoreCase) || reserved.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (values.Count == 1 && values[0] is { Length: <= 4000 } value && IsParameterName(name)) parameters[name] = value;
        }
        return parameters;

        static bool IsParameterName(string name) => name.Length is > 0 and <= 64 && (char.IsAsciiLetter(name[0]) || name[0] == '_')
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    }

    /// <summary>The most names <see cref="FormData"/> keeps.</summary>
    public const int MaxFormDataNames = 50;

    /// <summary>The longest value <see cref="FormData"/> keeps: room for a real captcha token, which runs to several KB.</summary>
    public const int MaxFormDataValueLength = 16384;

    /// <summary>What a request with no other controls carries.</summary>
    public static readonly IReadOnlyDictionary<string, string> NoFormData = new Dictionary<string, string>(0);

    /// <summary>
    /// The submitted part of a form that is not its fields, bounded like <see cref="Parameters"/>: at most
    /// <see cref="MaxFormDataNames"/> names of letters, digits, <c>_</c>, <c>-</c>, <c>.</c> and <c>:</c>, up to 64 characters, one
    /// text value each of at most <see cref="MaxFormDataValueLength"/> characters. Names ignore case. A repeated name is
    /// ambiguous and left out entirely rather than guessed, and so is anything else over a limit or not a string. A
    /// <paramref name="reserved"/> name, such as the antiforgery field, is never passed on.
    /// </summary>
    public static IReadOnlyDictionary<string, string> FormData(JsonElement? submitted, params string[] reserved)
    {
        if (submitted is not { ValueKind: JsonValueKind.Object } controls) return NoFormData;
        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var repeated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var control in controls.EnumerateObject())
        {
            if (!seen.Add(control.Name)) { repeated.Add(control.Name); continue; }
            if (data.Count < MaxFormDataNames && control.Value.ValueKind == JsonValueKind.String && control.Value.GetString() is { Length: <= MaxFormDataValueLength } value
                && IsControlName(control.Name) && !reserved.Contains(control.Name, StringComparer.OrdinalIgnoreCase)) data[control.Name] = value;
        }
        foreach (var name in repeated) data.Remove(name);
        return data;

        static bool IsControlName(string name) => name.Length is > 0 and <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':');
    }

    /// <summary>
    /// Whether a form's class refuses an anonymous visitor on the load the runtime is about to make, so the page can show the
    /// sign-in notice in place of a form that would only ever fail. A class that throws, or answers anything else, gets the form:
    /// the runtime's own load then reports what happened. The class is asked once more by that load, so its Initialize and Load must be safe to repeat.
    /// </summary>
    public static async Task<bool> RefusesAnonymousAsync(FormDefinition form, Guid pageKey, HttpContext http)
    {
        if (FormResourceAccess.Refusal(form.Resource, http.User) is not null) return true;
        var query = http.Request.Query[form.KeyParameter];
        if (query.Count > 1 || query.Count == 1 && string.IsNullOrWhiteSpace(query[0])) return false;
        var context = new FormExecutionContext(http.User, pageKey, form.Block.ContentKey, form.Revision, Parameters: Parameters(http.Request.Query));
        try { return (query.Count == 0 ? await form.Resource.InitializeAsync(context, http.RequestAborted) : await form.Resource.LoadAsync(query[0]!, context, http.RequestAborted)).Status == 403; }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ApplicationForms").LogWarning(error, "Form resource {Resource} failed while the page rendered for an anonymous visitor.", form.Resource.Alias);
            return false;
        }
    }

    private static FormRecord Project(FormRecord record, FormDefinition form)
    {
        var fields = form.Fields.Select(f => f.Content.Value<string>("field")!).ToHashSet(StringComparer.Ordinal);
        return record with {
            Values = record.Values.Where(p => fields.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value),
            Capabilities = form.ViewOnly ? new() : record.Capabilities,
        };
    }

    private static async Task<IResult> Load(Guid pageKey, Guid formKey, HttpContext http, CancellationToken ct)
    {
        var (form, member) = await Resolve(pageKey, formKey, http);
        if (member is null) return Results.StatusCode(403);
        if (form is null) return Results.Problem("The form definition is not valid.", statusCode: 422);
        if (FormResourceAccess.Refusal(form.Resource, member) is { } refused) return Results.StatusCode(refused);
        var query = http.Request.Query[form.KeyParameter];
        if (query.Count > 1 || query.Count == 1 && string.IsNullOrWhiteSpace(query[0])) return Results.BadRequest();
        var context = new FormExecutionContext(member, pageKey, formKey, form.Revision, Parameters: Parameters(http.Request.Query));
        var result = query.Count == 0 ? await form.Resource.InitializeAsync(context, ct) : await form.Resource.LoadAsync(query[0]!, context, ct);
        if (result.Record is null) return Result(result);
        var record = Project(result.Record, form);
        http.Response.Headers.CacheControl = "no-store";
        return Results.Json(new {
            record, definitionVersion = form.Revision,
            antiforgeryToken = http.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(http).RequestToken,
        });
    }

    private static Task<IResult> Create(Guid pageKey, Guid formKey, FormSubmission submission, HttpContext http, CancellationToken ct)
        => Mutate(pageKey, formKey, null, submission, "create", http, ct);
    private static Task<IResult> Update(Guid pageKey, Guid formKey, string key, FormSubmission submission, HttpContext http, CancellationToken ct)
        => Mutate(pageKey, formKey, key, submission, "update", http, ct);
    private static async Task<IResult> Delete(Guid pageKey, Guid formKey, string key, HttpContext http, CancellationToken ct)
    {
        // Read by hand, as a DELETE gets no body binding, so the size limit and bad JSON are answered here as they are for the others.
        FormSubmission? submission;
        try { submission = await http.Request.ReadFromJsonAsync<FormSubmission>(ct); }
        catch (BadHttpRequestException error) { return Results.StatusCode(error.StatusCode); }
        catch (JsonException) { return Results.BadRequest(); }
        return submission is null ? Results.BadRequest() : await Mutate(pageKey, formKey, key, submission, "delete", http, ct);
    }

    private static async Task<IResult> Mutate(Guid pageKey, Guid formKey, string? key, FormSubmission submission, string operation, HttpContext http, CancellationToken ct)
    {
        var (form, member) = await Resolve(pageKey, formKey, http);
        if (member is null) return Results.StatusCode(403);
        // As a load answers it: a form whose definition does not resolve is 422, a read-only one refuses every change.
        if (form is null) return Results.Problem("The form definition is not valid.", statusCode: 422);
        if (form.ViewOnly) return Results.StatusCode(403);
        if (FormResourceAccess.Refusal(form.Resource, member) is { } refused) return Results.StatusCode(refused);
        try { await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http); }
        catch (AntiforgeryValidationException) { return Results.BadRequest(); }
        if (submission.DefinitionVersion != form.Revision)
            return Result(new FormResult(409, Errors: new Dictionary<string, string[]> { [""] = ["This form has changed. Reload the page before saving."] }));
        var context = new FormExecutionContext(member, pageKey, formKey, form.Revision, submission.SubmissionKey, Parameters(http.Request.Query)) {
            FormData = FormData(submission.FormData, http.RequestServices.GetRequiredService<IOptions<AntiforgeryOptions>>().Value.FormFieldName),
        };
        http.Response.Headers.CacheControl = "no-store";
        var antiSpam = http.RequestServices.GetRequiredService<AntiSpamGate>();
        if (operation == "delete") {
            // Nothing to check on a delete, which only ever acts on a record the class allows; its token still stops here.
            context = context with { FormData = antiSpam.WithoutChecks(form, context.FormData) };
            var current = await form.Resource.LoadAsync(key!, context, ct);
            if (current.Record is null) return Result(current);
            if (current.Record.Checksum is { } expected && expected != submission.Checksum) return Result(FormResult.Conflict());
            var deletion = await form.Resource.DeleteAsync(key!, submission.Checksum, context, ct);
            Audit(http, operation, form.Resource.Alias, key, member, deletion.Status);
            return Result(deletion);
        }
        var errors = form.Validate(submission.Values, key is null);
        if (errors.Count > 0) return Result(new(422, Errors: errors));
        // The rows sent with the record, checked as the tabular endpoint checks a batch before anything is written.
        IReadOnlyList<FormDetail>? details = null;
        if (submission.Details is { Count: > 0 } sent)
        {
            var (resolved, unresolved) = await DetailsAsync(form, sent, member, pageKey, context, http, ct);
            if (unresolved is not null) return unresolved;
            details = resolved;
        }
        // After the fields, so a typo does not spend a token that works once, and before anything can reach the class, the load of an update included.
        var check = await antiSpam.CheckAsync(form, context.FormData, ct);
        if (check.Refusal is { } refusal) {
            Audit(http, operation, form.Resource.Alias, key, member, refusal.Status);
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
        var choiceErrors = await http.RequestServices.GetRequiredService<ListOfValuesService>().ValidateAsync(form, values, context, ct, stored?.Values);
        if (choiceErrors.Count > 0) return Result(new(422, Errors: choiceErrors));
        if (details is not null) return await SaveWithDetailsAsync(form, key, values, submission.Checksum, details, context, operation, member, http, ct);
        var result = key is null ? await form.Resource.CreateAsync(values, context, ct)
            : await form.Resource.UpdateAsync(key, values, submission.Checksum, context, ct);
        Audit(http, operation, form.Resource.Alias, key ?? result.Record?.Key, member, result.Status);
        return Result(result.Record is null ? result : result with { Record = Project(result.Record, form) });
    }

    /// <summary>One tabular block of a submission, resolved: the block, its data source, its changes as the engine takes them and its own context.</summary>
    private sealed record FormDetail(Guid Key, TabularBlock Block, SqlDataSource Source, List<DataSourceChange> Changes, FormExecutionContext Context);

    /// <summary>
    /// A refusal of a detail batch, naming the block and, when one change caused it, that change's position. When the record and earlier
    /// blocks were saved before it, as without a shared transaction, it carries the saved record and those blocks' rows.
    /// </summary>
    private static IResult DetailRefusal(Guid block, int status, int? index, IReadOnlyDictionary<string, string[]>? errors, FormRecord? record = null,
        IReadOnlyList<object>? saved = null)
        => Results.Json(new { status, block, index, errors = errors ?? new Dictionary<string, string[]>(), record, details = saved }, statusCode: status);

    private static IResult DetailRefusal(Guid block, int status, string message) => DetailRefusal(block, status, null, new Dictionary<string, string[]> { [""] = [message] });

    /// <summary>
    /// Resolves the tabular blocks a submission sends rows for: each a tabular block of this form, owned by the form's record, once;
    /// its data source available and writable to the caller, at the revision the rows were listed under; and its changes ones the
    /// block accepts. The engine makes the per-change checks a tabular save makes, in the save.
    /// </summary>
    private static async Task<(IReadOnlyList<FormDetail>? Details, IResult? Refusal)> DetailsAsync(FormDefinition form, IReadOnlyList<FormDetailSubmission> sent,
        ClaimsPrincipal member, Guid pageKey, FormExecutionContext context, HttpContext http, CancellationToken ct)
    {
        var blocks = AppBlockContent.Flatten(form.Block.Areas.SelectMany(a => a)).Where(b => b.Content.ContentType.Alias == AppBlockSchema.TabularAlias).ToArray();
        var resources = http.RequestServices.GetRequiredService<FormResourceRegistry>();
        var details = new List<FormDetail>(sent.Count);
        foreach (var detail in sent)
        {
            if (detail is null) return (null, Results.BadRequest());
            if (details.Any(d => d.Key == detail.Block)) return (null, DetailRefusal(detail.Block, 422, "The rows of a block are sent once."));
            if (blocks.FirstOrDefault(b => b.ContentKey == detail.Block) is not { } item) return (null, DetailRefusal(detail.Block, 422, "This form has no such Tabular form block."));
            var block = TabularBlock.Read(item.Content);
            if (!block.OwnedByForm) return (null, DetailRefusal(detail.Block, 422, "This Tabular form block names its own owner, so its rows save on their own."));
            if (await resources.FindAsync(block.DataSource) is not SqlDataSource source || block.LinkField.Length == 0)
                return (null, DetailRefusal(detail.Block, 422, $"The data source {block.DataSource} is not a published data source."));
            if (source.Problem is { } problem) return (null, DetailRefusal(detail.Block, 503, $"The data source {block.DataSource} is not available: {problem}"));
            if (block.ReadOnly || !await source.MayWriteAsync(member, ct)) return (null, DetailRefusal(detail.Block, 403, "This tabular form is read only."));
            if (detail.Revision != TabularEndpoints.Revision(source)) return (null, DetailRefusal(detail.Block, 409, "These rows have changed. Reload the page before saving."));
            if (detail.Changes is null) return (null, Results.BadRequest());
            if (TabularEndpoints.Changes(block, source, detail.Changes, out var changes) is { } refused)
                return (null, DetailRefusal(detail.Block, refused.Status, refused.Index, new Dictionary<string, string[]> { [refused.Field] = [refused.Message] }));
            details.Add(new(detail.Block, block, source, changes!, new FormExecutionContext(member, pageKey, detail.Block, TabularEndpoints.Revision(source),
                detail.SubmissionKey ?? context.SubmissionKey, context.Parameters)));
        }
        return (details, null);
    }

    /// <summary>
    /// The record and its blocks' rows. A form on a data source whose blocks' data sources share its connection saves them in one
    /// transaction. Otherwise nothing can be shared, so the record is saved first and then each block's rows, each batch whole or not at
    /// all: a block that fails leaves the record and any earlier block saved, and the answer carries the saved record with the refusal.
    /// </summary>
    private static async Task<IResult> SaveWithDetailsAsync(FormDefinition form, string? key, JsonElement values, string? checksum, IReadOnlyList<FormDetail> details,
        FormExecutionContext context, string operation, ClaimsPrincipal member, HttpContext http, CancellationToken ct)
    {
        FormResult result;
        var answers = new List<object>(details.Count);
        if (form.Resource is SqlDataSource master && details.All(d => master.SharesConnectionWith(d.Source)))
        {
            var saved = await master.SaveWithDetailsAsync(key, values, checksum, details.Select(d => new DataSourceDetailBatch(d.Key, d.Source, d.Block.LinkField, d.Changes,
                d.Block.MaxRows, d.Context)).ToArray(), context, ct);
            result = saved.Master;
            Audit(http, operation, form.Resource.Alias, key ?? result.Record?.Key, member, saved.Status);
            if (saved.Refusal is { } refusal)
            {
                var failed = details[saved.Detail!.Value];
                TabularEndpoints.Audit(http, "save", failed.Source.Alias, form.Resource.Alias, key, member, refusal.Status);
                return DetailRefusal(failed.Key, refusal.Status, refusal.Index, refusal.Errors);
            }
            if (result.Status != 200 || result.Record is null) return Result(result);
            for (var i = 0; i < details.Count; i++)
            {
                TabularEndpoints.Audit(http, "save", details[i].Source.Alias, form.Resource.Alias, result.Record.Key, member, 200);
                answers.Add(await DetailAnswerAsync(details[i], form, result.Record.Key, saved.Details![i], member, http, ct));
            }
            return Results.Json(new { record = Project(result.Record, form), details = answers });
        }

        result = key is null ? await form.Resource.CreateAsync(values, context, ct) : await form.Resource.UpdateAsync(key, values, checksum, context, ct);
        Audit(http, operation, form.Resource.Alias, key ?? result.Record?.Key, member, result.Status);
        if (result.Status != 200 || result.Record is null) return Result(result);
        var record = Project(result.Record, form);
        if (result.Record.Key is not { } owner)
            return DetailRefusal(details[0].Key, 422, null, new Dictionary<string, string[]> { [""] = ["This record keeps no key, so its rows cannot be saved with it."] }, record);
        foreach (var detail in details)
        {
            var rows = await detail.Source.SaveRowsAsync(new DataSourceScope(detail.Block.LinkField, owner), detail.Changes, detail.Context, detail.Block.MaxRows, ct);
            TabularEndpoints.Audit(http, "save", detail.Source.Alias, form.Resource.Alias, owner, member, rows.Status);
            if (rows.Status != 200) return DetailRefusal(detail.Key, rows.Status, rows.Index, rows.Errors, record, answers);
            answers.Add(await DetailAnswerAsync(detail, form, owner, rows, member, http, ct));
        }
        return Results.Json(new { record, details = answers });
    }

    /// <summary>A block's rows as its own endpoint answers them, with the block's key.</summary>
    private static async Task<object> DetailAnswerAsync(FormDetail detail, FormDefinition form, string? owner, DataSourceRowsResult rows, ClaimsPrincipal member,
        HttpContext http, CancellationToken ct)
        => new { block = detail.Key, list = await TabularEndpoints.Answer(detail.Block, detail.Source, member, form.Resource.Alias, owner, rows, http, ct) };

    private static void Audit(HttpContext http, string operation, string resource, string? key, ClaimsPrincipal member, int status)
        => http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ApplicationForms").LogInformation(
            "Form operation {Operation} on {Resource}/{Key} by {Member} returned {Status}", operation, resource, key,
            member.FindFirstValue(ClaimTypes.NameIdentifier) ?? BlockOwnerAccess.AnonymousActor, status);
}
