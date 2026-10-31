using System.Globalization;
using System.Net.Http.Headers;
using Pressroom.Contracts;

namespace AppBlocks.Documents.PaperReports;

/// <summary>HTTP adapter over the shared document gateway. GET requests only return files.</summary>
public sealed class PaperReportDelivery(IConfiguration configuration, IHttpClientFactory clients, ILogger<PaperReportDelivery> logger,
    DocumentSettingsStore? settings = null, DocumentOutputService? outputs = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(65);

    public async Task<IResult> DeliverAsync(PaperReportDocument document, HttpContext http, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var request = new RenderRequest { Html = document.Html, FileName = document.FileName, Options = document.Options };
        var result = http.Request.Query["format"].ToString().Equals("html", StringComparison.OrdinalIgnoreCase)
            ? DocumentOutputService.Html(request)
            : await (outputs ?? new DocumentOutputService(configuration, clients, (ILogger)logger, settings)).RenderDefaultPdfAsync(request, ct);
        http.Response.RegisterForDispose(result);
        http.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("inline") { FileNameStar = result.FileName }.ToString();
        return Results.Stream(result.Content, result.ContentType);
    }

    public static Uri? ServiceAddress(IConfiguration configuration)
        => PressroomSettings.FromConfiguration(configuration).Url?.Trim() is { Length: > 0 } url && DocumentSettingsStore.ValidAddress(url)
            ? new Uri(url.TrimEnd('/') + "/") : null;
    public static TimeSpan? ServiceTimeout(IConfiguration configuration)
        => double.TryParse(configuration["AppBlocks:Documents:Pressroom:TimeoutSeconds"] ?? configuration["Pressroom:TimeoutSeconds"],
            NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && DocumentSettingsStore.ValidTimeout(seconds)
                ? TimeSpan.FromSeconds(seconds) : null;
}
