using System.Net;
using System.Text;
using System.Text.Json;
using Pressroom.Client;
using Pressroom.Contracts;
using AppBlocks.DocumentOutput;

namespace AppBlocks.Documents;

/// <summary>The only Pressroom transport used by Documents and its consumers.</summary>
public sealed class DocumentOutputService(IHttpClientFactory clients, ILogger<DocumentOutputService> logger, DocumentSettingsStore settings)
    : IDocumentOutputService
{
    internal const string ClientName = "AppBlocks.Documents.Pressroom";

    public IReadOnlyList<DocumentDestination> GetDestinations()
    {
        var configurations = settings.List().Configurations;
        // A hand-edited settings file can hold any output: that configuration is left out, and the rest are still offered.
        foreach (var invalid in configurations.Where(c => !DocumentSettingsStore.ValidOutput(c.Output)))
            logger.LogWarning("Document configuration {Name} is left out: its output {Output} is neither PDF nor a printer. Choose its output again in Settings.", invalid.Name, invalid.Output);
        return configurations.Where(c => DocumentSettingsStore.ValidOutput(c.Output)).Select(c => new DocumentDestination(c.Id, c.Name,
            c.Output == "pdf" ? DocumentOutputKind.Pdf : DocumentOutputKind.Printer, c.Output == "pdf" ? null : c.Output[8..])).ToArray();
    }

    public Task<DocumentOutputResult> SubmitAsync(Guid configurationId, DocumentRequest document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(document.Html);
        cancellationToken.ThrowIfCancellationRequested();
        var destination = settings.Resolve(configurationId);
        return SubmitAsync(destination, new RenderRequest { Html = document.Html, FileName = document.FileName,
            Options = Convert(document.Options) }, cancellationToken);
    }

    internal async Task<DocumentFileResult> RenderDefaultPdfAsync(RenderRequest request, CancellationToken ct)
    {
        try
        {
            // A browser GET must never submit to a printer, even if it is the only configured output.
            var destination = settings.DefaultPdf();
            return destination is null ? Html(request) : (DocumentFileResult)await SubmitAsync(destination, request, ct);
        }
        catch (Exception error) when (Recoverable(error, ct))
        {
            logger.LogError(error, "The document PDF configuration could not be read; opening the HTML document instead.");
            return Html(request);
        }
    }

    private async Task<DocumentOutputResult> SubmitAsync(ResolvedPressroomConfiguration destination, RenderRequest request, CancellationToken ct)
    {
        HttpClient? client = null;
        var sentToPrinter = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (destination.Printer is not null && string.IsNullOrWhiteSpace(destination.ApiKey))
                throw new ArgumentException("Printer output requires a Pressroom API key.");
            client = CreateClient(destination);
            var pressroom = new PressroomClient(client);
            if (destination.Printer is { } printer)
            {
                request = request with { Printer = printer };
                var errors = RequestValidator.Validate(request, maxTimeoutMs: 300_000);
                if (errors.Count != 0) throw new ArgumentException("The document's print options are invalid.");
                sentToPrinter = true;
                var receipt = await pressroom.PrintAsync(request, ct);
                return new DocumentPrintResult(receipt.JobId, receipt.Printer, receipt.PageCount);
            }
            var pdf = await pressroom.RenderStreamAsync(request with { Printer = null }, ct);
            var owner = new PdfOwner(pdf, client);
            client = null;
            return new DocumentFileResult(pdf.PdfStream, "application/pdf", pdf.FileName, false, owner);
        }
        catch (Exception error) when (Recoverable(error, ct))
        {
            logger.LogError(error, "Pressroom output failed for document {FileName}, configuration {ConfigurationId} (status {StatusCode}).",
                RequestValidator.SanitizeFileName(request.FileName), destination.Id, (error as PressroomException)?.StatusCode);
            if (destination.Printer is null) return Html(request);
            var rejected = error is PressroomException { StatusCode: >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError }
                and not PressroomException { StatusCode: HttpStatusCode.RequestTimeout };
            var uncertain = sentToPrinter && !rejected;
            throw new DocumentPrintException(uncertain
                ? "The printer submission could not be confirmed. Check the printer or its queue before submitting again."
                : "The document was not submitted to the printer. Check its configuration and try again.", uncertain, error);
        }
        catch (OperationCanceledException) when (sentToPrinter)
        {
            logger.LogWarning("Document printer submission was cancelled for configuration {ConfigurationId}; its outcome may be unknown.", destination.Id);
            throw;
        }
        finally { client?.Dispose(); }
    }

    public async Task<PressroomPrinterList> DiscoverPrintersAsync(DiscoverPressroomPrinters request, CancellationToken ct = default)
    {
        var destination = settings.ForDiscovery(request);
        if (string.IsNullOrWhiteSpace(destination.ApiKey)) throw new ArgumentException("An API key is required to list Pressroom printers. PDF output can still use anonymous rendering.");
        try
        {
            using var client = CreateClient(destination);
            client.Timeout = TimeSpan.FromSeconds(Math.Min(15, client.Timeout.TotalSeconds));
            using var response = await client.GetAsync("v1/printers", HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(1024 * 1024, ct);
            using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
            if (!json.RootElement.TryGetProperty("printers", out var printers) || printers.ValueKind != JsonValueKind.Array)
                throw new JsonException("Pressroom returned an invalid printer list.");
            var values = printers.EnumerateArray().Select(p => p.ValueKind == JsonValueKind.String ? p.GetString() : null).ToArray();
            if (values.Any(p => p is null || !RequestValidator.IsPrinterIdentifier(p))) throw new JsonException("Pressroom returned an invalid printer identifier.");
            return new(values.Select(p => p!).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray());
        }
        catch (Exception error) when (Recoverable(error, ct))
        {
            logger.LogError(error, "Pressroom printers could not be loaded for configuration {ConfigurationId}.", request.Id);
            throw;
        }
    }

    private HttpClient CreateClient(ResolvedPressroomConfiguration destination)
    {
        if (!DocumentSettingsStore.ValidAddress(destination.Url.Trim())) throw new ArgumentException("The configured Pressroom URL is not a usable HTTP or HTTPS service address.");
        if (destination.ApiKey is { } key && (key.Length > 4096 || key.Any(char.IsControl))) throw new ArgumentException("The configured Pressroom API key is not a valid HTTP header.");
        var client = clients.CreateClient(ClientName);
        try
        {
            client.BaseAddress = new Uri(destination.Url.Trim().TrimEnd('/') + "/");
            client.Timeout = DocumentSettingsStore.ValidTimeout(destination.TimeoutSeconds) ? TimeSpan.FromSeconds(destination.TimeoutSeconds) : TimeSpan.FromSeconds(65);
            if (!DocumentSettingsStore.ValidTimeout(destination.TimeoutSeconds)) logger.LogWarning("The Pressroom timeout is invalid; using 65 seconds.");
            client.DefaultRequestHeaders.Remove("X-Api-Key");
            if (!string.IsNullOrWhiteSpace(destination.ApiKey)) client.DefaultRequestHeaders.Add("X-Api-Key", destination.ApiKey);
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    internal static DocumentFileResult Html(RenderRequest request)
        => new(new MemoryStream(Encoding.UTF8.GetBytes(request.Html)), "text/html; charset=utf-8",
            Path.ChangeExtension(RequestValidator.SanitizeFileName(request.FileName), ".html"), true);
    private static bool Recoverable(Exception error, CancellationToken ct)
        => error is HttpRequestException or PressroomException or ArgumentException or FormatException or IOException
            or System.Security.Cryptography.CryptographicException or JsonException || error is OperationCanceledException && !ct.IsCancellationRequested;

    /// <summary>
    /// Pressroom's options for a consumer's: a locale or time zone the consumer leaves out is the submitting request's culture and the
    /// web server's time zone, and no options at all are Pressroom's defaults with those two and a page-number footer.
    /// </summary>
    internal static RenderOptions Convert(DocumentRenderOptions? options)
    {
        options ??= new DocumentRenderOptions();
        return new() {
            PageSize = options.PageSize is { } page ? new PageSize { Name = page.Name, Width = page.Width, Height = page.Height } : null,
            Landscape = options.Landscape, Margins = options.Margins is { } m ? new PageMargins { Top = m.Top, Right = m.Right, Bottom = m.Bottom, Left = m.Left } : new(),
            PreferCssPageSize = options.PreferCssPageSize, PrintBackground = options.PrintBackground, Scale = options.Scale,
            DisplayHeaderFooter = options.DisplayHeaderFooter, HeaderTemplate = options.HeaderTemplate, FooterTemplate = options.FooterTemplate,
            Tagged = options.Tagged, Outline = options.Outline, Media = options.Media, JavaScript = options.JavaScript,
            Locale = options.Locale ?? DocumentRenderOptions.LocaleName(System.Globalization.CultureInfo.CurrentCulture),
            TimeZone = options.TimeZone ?? DocumentRenderOptions.ServerTimeZone, TimeoutMs = options.TimeoutMs,
            Readiness = options.Readiness is { } r ? new ReadinessOptions { Selector = r.Selector, Function = r.Function, DelayMs = r.DelayMs, TimeoutMs = r.TimeoutMs } : new(),
        };
    }

    private sealed class PdfOwner(IDisposable pdf, HttpClient client) : IDisposable
    {
        public void Dispose() { try { pdf.Dispose(); } finally { client.Dispose(); } }
    }
}
