using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using AppBlocks.Blocks;
using AppBlocks.DataSources;

namespace AppBlocks.Forms;

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
        var configuration = endpoints.ServiceProvider.GetService<IConfiguration>();
        var limit = configuration?.GetValue<long?>("AppBlocks:Forms:MaxBodySize") ?? DefaultMaxBodySize;
        var group = endpoints.MapGroup(AppBlocksRoutes.Under(configuration, "forms/{pageKey:guid}/{formKey:guid}")).WithMetadata(new RequestSizeLimitAttribute(limit));
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
        var services = http.RequestServices;
        return (await FormDefinition.ResolveAsync(page, formKey, services.GetRequiredService<FormResourceRegistry>(), services.GetRequiredService<PublishedDataCatalog>(),
            AppBlockContent.RequestCulture(http), services.GetServices<IAppBlockPlugin>()), member);
    }

    private static IResult Result(FormResult result) => result.Status == 204 ? Results.NoContent() : Results.Json(result, statusCode: result.Status);

    // Compatibility helpers for sites compiled against the original endpoint API. New code uses FormRequestValues and FormAccess.
    public static IReadOnlyDictionary<string, string> Parameters(IQueryCollection query, params string[] reserved) => FormRequestValues.Parameters(query, reserved);
    public const int MaxFormDataNames = FormRequestValues.MaxFormDataNames;
    public const int MaxFormDataValueLength = FormRequestValues.MaxFormDataValueLength;
    public static readonly IReadOnlyDictionary<string, string> NoFormData = FormRequestValues.NoFormData;
    public static IReadOnlyDictionary<string, string> FormData(JsonElement? submitted, params string[] reserved) => FormRequestValues.FormData(submitted, reserved);
    public static Task<bool> RefusesAnonymousAsync(FormDefinition form, Guid pageKey, HttpContext http) => FormAccess.RefusesAnonymousAsync(form, pageKey, http);

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
        var record = form.Project(result.Record);
        http.Response.Headers.CacheControl = "no-store";
        return Results.Json(new {
            record, definitionVersion = form.Revision,
            antiforgeryToken = http.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(http).RequestToken,
        });
    }

    private static Task<IResult> Create(Guid pageKey, Guid formKey, FormSubmission submission, HttpContext http, CancellationToken ct)
        => Mutate(pageKey, formKey, null, submission, FormOperation.Create, http, ct);
    private static Task<IResult> Update(Guid pageKey, Guid formKey, string key, FormSubmission submission, HttpContext http, CancellationToken ct)
        => Mutate(pageKey, formKey, key, submission, FormOperation.Update, http, ct);
    private static async Task<IResult> Delete(Guid pageKey, Guid formKey, string key, HttpContext http, CancellationToken ct)
    {
        // Read by hand, as a DELETE gets no body binding, so the size limit and bad JSON are answered here as they are for the others.
        FormSubmission? submission;
        // A body that is not JSON is refused as the bound POST and PUT refuse it, before anything reads it.
        if (!http.Request.HasJsonContentType()) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        try { submission = await http.Request.ReadFromJsonAsync<FormSubmission>(ct); }
        catch (BadHttpRequestException error) { return Results.StatusCode(error.StatusCode); }
        catch (JsonException) { return Results.BadRequest(); }
        return submission is null ? Results.BadRequest() : await Mutate(pageKey, formKey, key, submission, FormOperation.Delete, http, ct);
    }

    private static async Task<IResult> Mutate(Guid pageKey, Guid formKey, string? key, FormSubmission submission, FormOperation operation, HttpContext http, CancellationToken ct)
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
        var result = await http.RequestServices.GetRequiredService<FormSubmissionService>().SubmitAsync(form, key, submission, operation, context,
            () => http.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(http).RequestToken, ct);
        return result.Body is null ? Results.StatusCode(result.Status) : Results.Json(result.Body, statusCode: result.Status);
    }
}
