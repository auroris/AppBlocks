using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using AppBlocks.DataSources;
using AppBlocks.Forms;

namespace AppBlocks.Tabular;

/// <summary>
/// <c>/api/tabular/{page}/{block}</c>, or the same under the configured <see cref="AppBlocksRoutes.Root"/>: the rows of
/// the record a Tabular form block shows. Every call resolves the block through <see cref="TabularAccess"/> first, so page access and the owning record's own load decide, and the
/// rows are then read and written through the child data source with its own rules. A save is one batch, applied
/// whole or not at all, carrying the antiforgery token the list hands out; it is logged like form operations.
/// <c>/new</c> lists a block inside a form whose record is not saved yet: no rows, and what a new row starts from. Its
/// rows save with that form's record, through the form endpoints (<see cref="FormDetailSubmission"/>).
/// </summary>
public static class TabularEndpoints
{
    /// <summary>Where the endpoints answer: <c>tabular</c> under <see cref="AppBlocksRoutes.Root"/>, by default <c>/api/tabular</c>.</summary>
    public static string Route(IConfiguration? configuration) => AppBlocksRoutes.Under(configuration, "tabular");

    public static void MapTabular(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(Route(endpoints.ServiceProvider.GetService<IConfiguration>()) + "/{pageKey:guid}/{blockKey:guid}");
        group.MapGet("", List);
        group.MapGet("/new", ListNew);
        group.MapPost("", Save);
    }

    /// <summary>The revision a block's rows are listed under, which a save with its form sends back, so a changed data source is noticed.</summary>
    public static string Revision(SqlDataSource source) => TabularOperations.Revision(source);

    private static async Task<IResult> List(Guid pageKey, Guid blockKey, HttpContext http, TabularAccess access, CancellationToken ct)
    {
        var scope = await access.ResolveAsync(pageKey, blockKey, ct);
        if (scope.Status != 200) return Refusal(scope);
        http.Response.Headers.CacheControl = "no-store";
        var context = Context(scope, pageKey, blockKey, null);
        var rows = await scope.Source!.ListRowsAsync(scope.Scope!, context, scope.Block!.MaxRows, ct);
        if (rows.Status != 200) return Audited(http, "list", scope, Failure(rows), rows.Status);
        return Results.Json(await Answer(scope, rows, http, ct));
    }

    private static async Task<IResult> ListNew(Guid pageKey, Guid blockKey, HttpContext http, TabularAccess access, CancellationToken ct)
    {
        var scope = await access.ResolveNewAsync(pageKey, blockKey, ct);
        if (scope.Status != 200) return Refusal(scope);
        http.Response.Headers.CacheControl = "no-store";
        var rows = await scope.Source!.NewRowsAsync(scope.Block!.LinkField, Context(scope, pageKey, blockKey, null), ct);
        if (rows.Status != 200) return Audited(http, "list", scope, Failure(rows), rows.Status);
        return Results.Json(await Answer(scope, rows, http, ct));
    }

    private static async Task<IResult> Save(Guid pageKey, Guid blockKey, HttpContext http, TabularAccess access, CancellationToken ct)
    {
        var scope = await access.ResolveAsync(pageKey, blockKey, ct);
        if (scope.Status != 200) return Refusal(scope);
        if (scope.Block!.ReadOnly || !await scope.Source!.MayWriteAsync(scope.User!, ct))
            return Audited(http, "save", scope, Results.Problem("This tabular form is read only.", statusCode: 403), 403);
        try { await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http); }
        catch (AntiforgeryValidationException) { return Results.BadRequest(); }
        // ReadFromJsonAsync throws for any other content type, which would otherwise be a 500.
        if (!http.Request.HasJsonContentType()) return Results.Problem("Send the changes as JSON.", statusCode: 415);
        TabularSubmission? submission;
        try { submission = await http.Request.ReadFromJsonAsync<TabularSubmission>(ct); }
        catch (Exception error) when (error is JsonException or BadHttpRequestException) { return Results.BadRequest(); }
        if (submission?.Changes is null) return Results.BadRequest();
        http.Response.Headers.CacheControl = "no-store";
        var block = scope.Block;
        if (TabularOperations.Changes(block, scope.Source!, submission.Changes, out var changes) is { } refused)
            return refused.Status == 403 ? Audited(http, "save", scope, Problem(refused), 403) : Problem(refused);
        var context = Context(scope, pageKey, blockKey, submission.SubmissionKey);
        var result = await scope.Source!.SaveRowsAsync(scope.Scope!, changes!, context, block.MaxRows, ct);
        if (result.Status != 200) return Audited(http, "save", scope, Failure(result), result.Status);
        return Audited(http, "save", scope, Results.Json(await Answer(scope, result, http, ct)), 200);
    }

    /// <summary>The list, the capabilities and the columns, with a fresh antiforgery token.</summary>
    private static Task<object> Answer(TabularScope scope, DataSourceRowsResult rows, HttpContext http, CancellationToken ct)
        => TabularOperations.Answer(scope.Block!, scope.Source!, scope.User!, scope.OwnerAlias, scope.Scope?.Value, rows, () => http.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(http).RequestToken, ct);

    private static FormExecutionContext Context(TabularScope scope, Guid pageKey, Guid blockKey, string? submissionKey)
        => new(scope.User!, pageKey, blockKey, Revision(scope.Source!), submissionKey, scope.Parameters);

    private static IResult Failure(DataSourceRowsResult result)
        => Results.Json(new { status = result.Status, index = result.Index, errors = result.Errors ?? new Dictionary<string, string[]>() }, statusCode: result.Status);

    private static IResult Problem(TabularOperations.TabularRefusal refusal)
        => Results.Json(new { status = refusal.Status, index = refusal.Index, errors = new Dictionary<string, string[]> { [refusal.Field] = [refusal.Message] } }, statusCode: refusal.Status);

    private static IResult Refusal(TabularScope scope) => Results.Problem(scope.Problem, statusCode: scope.Status);

    private static IResult Audited(HttpContext http, string operation, TabularScope scope, IResult result, int status)
    {
        TabularOperations.Audit(http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ApplicationTabular"), operation, scope.Source?.Alias, scope.OwnerAlias, scope.Scope?.Value, scope.User, status);
        return result;
    }

}
