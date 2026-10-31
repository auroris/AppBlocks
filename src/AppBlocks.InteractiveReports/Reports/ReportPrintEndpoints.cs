using System.Security.Claims;
using System.Text.Json;
using AppBlocks.DocumentOutput;
using InteractiveReport.AspNetCore;
using InteractiveReport.Client.FileDownload;
using InteractiveReport.Core.Model;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace AppBlocks.InteractiveReports.Reports;

internal static class ReportPrintEndpoints
{
    internal sealed record PrepareRequest(ReportState? Document, Guid DestinationId, string Scope = "all", string PageSize = "A4", bool Landscape = false);
    internal sealed record SubmitRequest(string? Token);
    private sealed record RenderedReport(ReportResult Result, string Title, string Html);

    internal static void MapReportPrinting(this IEndpointRouteBuilder endpoints, string prefix)
    {
        var group = endpoints.MapGroup(prefix + "/{name}/print");
        group.AddEndpointFilter(async (context, next) => {
            var http = context.HttpContext;
            http.Response.Headers.CacheControl = "no-store";
            if (http.User.Identity?.IsAuthenticated != true || Owner(http, "") is null) return Results.Unauthorized();
            if (HttpMethods.IsPost(http.Request.Method))
            {
                try { await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http); }
                catch (AntiforgeryValidationException) { return Problem("Refresh the page before printing.", 400); }
            }
            return await next(context);
        });
        group.MapGet("/options", Options);
        group.MapPost("/preview", Prepare).Accepts<PrepareRequest>("application/json");
        group.MapPost("/submit", Submit).Accepts<SubmitRequest>("application/json");
    }

    private static IReadOnlyList<DocumentDestination> Destinations(HttpContext http) => http.RequestServices
        .GetService<IDocumentOutputService>()?.GetDestinations().Where(d => d.Output is DocumentOutputKind.Pdf or DocumentOutputKind.Printer).ToArray() ?? [];

    internal static async Task<IResult> Options(string name, HttpContext http, [FromServices] IInteractiveReportServer server, CancellationToken ct)
    {
        var schema = await server.GetSchema(name, InteractiveReportHttpRequest.Context(http), ct);
        if (schema.Failure is not null) return InteractiveReportHttpResult.Failure(schema.Failure, http);
        var destinations = schema.Value!.Features.Contains(ReportFeatures.Download) ? Destinations(http) : [];
        return Results.Json(new {
            destinations = destinations.Select(d => new { d.Id, d.Name, output = d.Output.ToString().ToLowerInvariant(), d.Printer }),
            antiforgeryToken = destinations.Count == 0 ? null : http.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(http).RequestToken,
        });
    }

    internal static async Task<IResult> Prepare(string name, HttpContext http, [FromServices] IInteractiveReportServer server,
        [FromServices] ReportPrintPreviews previews, CancellationToken ct)
    {
        PrepareRequest? request;
        try { request = await JsonSerializer.DeserializeAsync<PrepareRequest>(http.Request.Body, IrJson.Options, ct); }
        catch (JsonException) { return Problem("Invalid print settings.", 400); }
        if (request?.Document is null || request.Scope is not ("all" or "page") || request.PageSize is not ("A4" or "Letter" or "Legal"))
            return Problem("Choose a report, row scope, and supported paper size.", 400);
        if (request.Scope == "page" && request.Document.Page is not { Index: > 0, Size: > 0 })
            return Problem("This report is not paged. Choose all matching rows.", 400);
        var destination = Destinations(http).FirstOrDefault(d => d.Id == request.DestinationId);
        if (destination is null) return Problem("This print destination is no longer available.", 409);
        var (rendered, error) = await Render(name, request.Document, request.Scope, request.PageSize, request.Landscape, http, server, ct);
        if (error is not null) return error;
        var document = new DocumentRequest(rendered!.Html, InteractiveReportHttpRequest.SafeFileName(rendered.Title, ".pdf"),
            new DocumentRenderOptions { PageSize = new(request.PageSize), Landscape = request.Landscape,
                Margins = new(), JavaScript = false });
        var token = previews.Add(new(Owner(http, name)!, destination, document, rendered.Result));
        return Results.Json(new { token, html = rendered.Html, rows = rendered.Result.Rows.Count, totalRows = rendered.Result.TotalRows });
    }

    private static async Task<(RenderedReport? Document, IResult? Error)> Render(string name, ReportState state,
        string scope, string pageSize, bool landscape, HttpContext http, IInteractiveReportServer server, CancellationToken ct)
    {
        if (scope == "all") state.Page = new PageRequest { Index = 1, Size = 0 };
        var query = await server.QueryForDownload(name, state, InteractiveReportHttpRequest.Context(http), ct);
        if (query.Failure is not null) return (null, InteractiveReportHttpResult.Failure(query.Failure, http));
        if (query.Truncated) return (null, Problem("This report exceeds the export limit. Narrow the filters and try again.", 422));
        var schema = await server.GetSchema(name, InteractiveReportHttpRequest.Context(http), ct);
        if (schema.Failure is not null) return (null, InteractiveReportHttpResult.Failure(schema.Failure, http));
        string html;
        try { html = HtmlReportPresentation.Render(schema.Value!.Title, query.Value!, scope, pageSize, landscape); }
        catch (HtmlReportSizeLimitException ex) { return (null, Problem(ex.Message, 422)); }
        return (new(query.Value!, schema.Value.Title, html), null);
    }

    internal static async Task<IResult> Submit(string name, HttpContext http, [FromServices] IInteractiveReportServer server,
        [FromServices] ReportPrintPreviews previews, CancellationToken ct)
    {
        SubmitRequest? request;
        try { request = await http.Request.ReadFromJsonAsync<SubmitRequest>(ct); }
        catch (JsonException) { return Problem("Invalid print request.", 400); }
        var preview = request?.Token is { Length: 32 } token ? previews.Find(token, Owner(http, name)!) : null;
        if (preview is null) return Problem("The print preview has expired. Generate a new preview.", 409);
        // Re-check page, source, and report access before releasing the stored document.
        var query = await server.QueryForDownload(name, preview.State, InteractiveReportHttpRequest.Context(http), ct);
        if (query.Failure is not null) return InteractiveReportHttpResult.Failure(query.Failure, http);
        if (query.Truncated || ReportPrintPreviews.DataFingerprint(query.Value!) != preview.Fingerprint)
            return Problem("The report data changed. Generate a new preview before printing.", 409);
        if (!Destinations(http).Contains(preview.Destination)) return Problem("The print destination changed. Generate a new preview.", 409);
        if (!preview.Consume()) return Problem("This preview has already been submitted. Check the printer before creating another print job.", 409);
        try
        {
            var result = await http.RequestServices.GetRequiredService<IDocumentOutputService>()
                .SubmitAsync(preview.Destination.Id, preview.Document, ct);
            if (result is DocumentPrintResult print)
            {
                using (print) return Results.Json(new { status = "submitted", print.JobId, print.Printer, print.PageCount });
            }
            if (result is DocumentFileResult file) return new OwnedFile(file);
            result.Dispose();
            return Problem("Documents returned an unsupported result.", 502);
        }
        catch (DocumentPrintException ex)
        {
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ReportPrinting").LogWarning(ex, "Report print submission failed");
            return Results.Json(new { title = ex.MayHavePrinted
                ? "The printer may have received this job. Check the printer or queue before submitting again."
                : "The print job could not be submitted. Check the destination before trying again.", mayHavePrinted = ex.MayHavePrinted }, statusCode: 502);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ReportPrinting").LogWarning(ex, "Report document delivery failed");
            return Problem(preview.Destination.Output == DocumentOutputKind.Printer
                ? "The printer outcome is unknown. Check the printer or queue before submitting again."
                : "The document could not be delivered. Generate a new preview to try again.", 502);
        }
    }

    private static string? Owner(HttpContext http, string name) => http.User.FindFirstValue(ClaimTypes.NameIdentifier) is { } id
        ? JsonSerializer.Serialize(new[] { http.User.Identity?.AuthenticationType, id, name, http.Request.QueryString.Value }) : null;
    private static IResult Problem(string message, int status) => Results.Problem(title: message, statusCode: status);
    private sealed class OwnedFile(DocumentFileResult file) : IResult
    {
        public async Task ExecuteAsync(HttpContext http)
        {
            await using (file)
            {
                http.Response.Headers["X-AppBlocks-Html-Fallback"] = file.IsHtmlFallback ? "true" : "false";
                await Results.Stream(file.Content, file.ContentType, file.FileName).ExecuteAsync(http);
            }
        }
    }
}
