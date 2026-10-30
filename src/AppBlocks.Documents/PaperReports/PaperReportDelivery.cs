using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using AppBlocks.Documents;
using Pressroom.Client;
using Pressroom.Contracts;

namespace UmbracoAppHost.PaperReports;

/// <summary>Opens the document inline. Pressroom failures use the same HTML and Umbraco's normal logging pipeline.</summary>
public sealed class PaperReportDelivery(IConfiguration configuration, IHttpClientFactory clients, ILogger<PaperReportDelivery> logger,
    DocumentSettingsStore? settings = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(65);

    public async Task<IResult> DeliverAsync(PaperReportDocument document, HttpContext http, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (http.Request.Query["format"].ToString().Equals("html", StringComparison.OrdinalIgnoreCase)) return Html(document, http);
        try
        {
            var current = settings?.Resolve() ?? PressroomSettings.FromConfiguration(configuration);
            if (string.IsNullOrWhiteSpace(current.Url)) return Html(document, http);
            if (!DocumentSettingsStore.ValidAddress(current.Url.Trim()))
            {
                logger.LogError("The configured Pressroom URL is not a usable HTTP or HTTPS service address; opening the HTML document instead.");
                return Html(document, http);
            }
            var client = clients.CreateClient("Pressroom");
            http.Response.RegisterForDispose(client);
            client.BaseAddress = new Uri(current.Url.Trim().TrimEnd('/') + "/");
            client.Timeout = DocumentSettingsStore.ValidTimeout(current.TimeoutSeconds) ? TimeSpan.FromSeconds(current.TimeoutSeconds) : DefaultTimeout;
            if (!DocumentSettingsStore.ValidTimeout(current.TimeoutSeconds))
                logger.LogWarning("The Pressroom timeout is invalid; using {Seconds} seconds.", DefaultTimeout.TotalSeconds);
            client.DefaultRequestHeaders.Remove("X-Api-Key");
            if (!string.IsNullOrWhiteSpace(current.ApiKey)) client.DefaultRequestHeaders.Add("X-Api-Key", current.ApiKey);
            var pdf = await new PressroomClient(client).RenderStreamAsync(new RenderRequest {
                Html = document.Html, FileName = document.FileName, Options = document.Options, Disposition = PdfDisposition.Inline,
            }, ct);
            http.Response.RegisterForDispose(pdf);
            Inline(http, pdf.FileName);
            return Results.Stream(pdf.PdfStream, "application/pdf");
        }
        catch (Exception error) when (error is HttpRequestException or PressroomException or ArgumentException or FormatException
            or IOException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException
            || error is OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogError(error, "Pressroom could not render document {FileName} (status {StatusCode}); opening the HTML document instead.",
                document.FileName, (error as PressroomException)?.StatusCode);
            return Html(document, http);
        }
    }

    // Retained for callers migrating from AppHost's paper reports.
    public static Uri? ServiceAddress(IConfiguration configuration)
        => PressroomSettings.FromConfiguration(configuration).Url?.Trim() is { Length: > 0 } url && DocumentSettingsStore.ValidAddress(url)
            ? new Uri(url.TrimEnd('/') + "/") : null;

    public static TimeSpan? ServiceTimeout(IConfiguration configuration)
        => double.TryParse(configuration["AppBlocks:Documents:Pressroom:TimeoutSeconds"] ?? configuration["Pressroom:TimeoutSeconds"],
            NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && DocumentSettingsStore.ValidTimeout(seconds)
                ? TimeSpan.FromSeconds(seconds) : null;

    private static void Inline(HttpContext http, string fileName)
        => http.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("inline") {
            FileNameStar = RequestValidator.SanitizeFileName(fileName),
        }.ToString();

    private static IResult Html(PaperReportDocument document, HttpContext http)
    {
        // Keep the HTML extension; Pressroom's filename sanitizer intentionally always produces .pdf.
        http.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("inline") {
            FileNameStar = Path.ChangeExtension(RequestValidator.SanitizeFileName(document.FileName), ".html"),
        }.ToString();
        return Results.Text(document.Html, "text/html; charset=utf-8", Encoding.UTF8);
    }
}
