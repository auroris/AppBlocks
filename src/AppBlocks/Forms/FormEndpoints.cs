using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;
using AppBlocks.Blocks;
using AppBlocks.DataSources;

namespace AppBlocks.Forms;

/// <param name="FormData">The form's other named controls by name, apart from <paramref name="Values"/> so validation and strict binding never see them.</param>
public sealed record FormSubmission(string DefinitionVersion, string? Version, JsonElement Values, string? SubmissionKey, JsonElement? FormData = null);

public static class FormEndpoints
{
    public static void MapApplicationForms(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/forms/{pageKey:guid}/{formKey:guid}");
        group.MapGet("", Load);
        group.MapPost("", Create);
        group.MapPut("/{key}", Update);
        group.MapDelete("/{key}", Delete);
    }

    private static async Task<(FormDefinition? Form, ClaimsPrincipal? Member)> Resolve(Guid pageKey, Guid formKey, HttpContext http)
    {
        if (http.RequestServices.GetRequiredService<AppBlockContent>().Page(pageKey) is not { } page) return (null, null);
        // On an open page an anonymous visitor reaches the form's class, which decides from here on.
        var member = await http.RequestServices.GetRequiredService<AppPageAccess>().VisitorAsync(page);
        if (member is null) return (null, null);
        // Bind antiforgery tokens to the actual Umbraco Member or backoffice User, or to nobody.
        http.User = member;
        return (FormDefinition.Resolve(page, formKey, http.RequestServices.GetRequiredService<FormResourceRegistry>(), http.RequestServices.GetRequiredService<PublishedDataCatalog>(), AppBlockContent.RequestCulture(http)), member);
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
        var submission = await http.Request.ReadFromJsonAsync<FormSubmission>(ct);
        return submission is null ? Results.BadRequest() : await Mutate(pageKey, formKey, key, submission, "delete", http, ct);
    }

    private static async Task<IResult> Mutate(Guid pageKey, Guid formKey, string? key, FormSubmission submission, string operation, HttpContext http, CancellationToken ct)
    {
        var (form, member) = await Resolve(pageKey, formKey, http);
        if (member is null || form?.ViewOnly != false) return Results.StatusCode(403);
        try { await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http); }
        catch (AntiforgeryValidationException) { return Results.BadRequest(); }
        if (submission.DefinitionVersion != form.Revision) return Results.Json(FormResult.Invalid("", "This form has changed. Reload the page before saving."), statusCode: 409);
        if (key is not null && string.IsNullOrWhiteSpace(submission.Version)) return Results.BadRequest();
        var context = new FormExecutionContext(member, pageKey, formKey, form.Revision, submission.SubmissionKey, Parameters(http.Request.Query)) {
            FormData = FormData(submission.FormData, http.RequestServices.GetRequiredService<IOptions<AntiforgeryOptions>>().Value.FormFieldName),
        };
        http.Response.Headers.CacheControl = "no-store";
        if (operation == "delete") {
            var deletion = await form.Resource.DeleteAsync(key!, submission.Version!, context, ct);
            Audit(http, operation, form.Resource.Alias, key, member, deletion.Status);
            return Result(deletion);
        }
        var errors = form.Validate(submission.Values, key is null);
        if (errors.Count > 0) return Result(new(422, Errors: errors));
        var values = submission.Values;
        FormRecord? stored = null;
        if (key is not null) {
            var current = await form.Resource.LoadAsync(key, context, ct);
            if (current.Record is null) return Result(current);
            stored = current.Record;
            if (stored.Version != submission.Version) return Result(FormResult.Conflict());
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
        var result = key is null ? await form.Resource.CreateAsync(values, context, ct)
            : await form.Resource.UpdateAsync(key, values, submission.Version!, context, ct);
        Audit(http, operation, form.Resource.Alias, key ?? result.Record?.Key, member, result.Status);
        return Result(result.Record is null ? result : result with { Record = Project(result.Record, form) });
    }

    private static void Audit(HttpContext http, string operation, string resource, string? key, ClaimsPrincipal member, int status)
        => http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ApplicationForms").LogInformation(
            "Form operation {Operation} on {Resource}/{Key} by {Member} returned {Status}", operation, resource, key,
            member.FindFirstValue(ClaimTypes.NameIdentifier) ?? BlockOwnerAccess.AnonymousActor, status);
}
