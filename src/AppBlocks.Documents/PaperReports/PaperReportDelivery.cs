using System.Net.Http.Headers;
using Pressroom.Contracts;

namespace AppBlocks.Documents.PaperReports;

/// <summary>HTTP adapter over the shared document gateway. GET requests only return files.</summary>
public sealed class PaperReportDelivery(DocumentOutputService outputs)
{
    public async Task<IResult> DeliverAsync(PaperReportDocument document, HttpContext http, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var request = new RenderRequest { Html = document.Html, FileName = document.FileName, Options = document.Options };
        var result = http.Request.Query["format"].ToString().Equals("html", StringComparison.OrdinalIgnoreCase)
            ? DocumentOutputService.Html(request)
            : await outputs.RenderDefaultPdfAsync(request, ct);
        http.Response.RegisterForDispose(result);
        http.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("inline") { FileNameStar = result.FileName }.ToString();
        return Results.Stream(result.Content, result.ContentType);
    }
}
