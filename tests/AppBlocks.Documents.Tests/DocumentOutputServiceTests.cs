using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AppBlocks.DocumentOutput;

namespace AppBlocks.Documents.Tests;

public sealed class DocumentOutputServiceTests
{
    [Fact]
    public async Task Discovery_uses_saved_credentials_and_returns_only_real_printer_identifiers()
    {
        using var site = new DocumentSettingsTests.SettingsSite();
        var factory = new Factory(request => {
            Assert.Equal("https://configured.example/v1/printers", request.RequestUri!.ToString());
            Assert.Equal("configured-secret", request.Headers.GetValues("X-Api-Key").Single());
            return Json("{\"printers\":[\"office\",\"reception\",\"office\"]}");
        });
        var result = await Service(site, factory).DiscoverPrintersAsync(new(DocumentSettingsStore.LegacyId, "https://configured.example/"));
        Assert.Equal(new[] { "office", "reception" }, result.Printers);
        Assert.Equal(1, factory.Calls);
        Assert.Equal(1, factory.Disposals);
    }

    [Fact]
    public async Task Discovery_without_a_key_does_not_call_the_service()
    {
        using var site = new DocumentSettingsTests.SettingsSite();
        var factory = new Factory(_ => throw new Exception("Unexpected network call."));
        await Assert.ThrowsAsync<ArgumentException>(() => Service(site, factory).DiscoverPrintersAsync(new(null, "https://anonymous.example/")));
        Assert.Equal(0, factory.Calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"printers\":[\"bad printer\"]}")]
    [InlineData("{\"printers\":[12]}")]
    public async Task Malformed_printer_responses_are_rejected(string body)
    {
        using var site = new DocumentSettingsTests.SettingsSite();
        await Assert.ThrowsAsync<JsonException>(() => Service(site, new(_ => Json(body))).DiscoverPrintersAsync(new(DocumentSettingsStore.LegacyId, "https://configured.example/")));
    }

    [Fact]
    public async Task Pdf_is_anonymous_and_stream_ownership_lasts_until_the_result_is_disposed()
    {
        using var site = new DocumentSettingsTests.SettingsSite();
        var config = await site.Store.SaveConfigurationAsync(new(null, "Anonymous PDF", "https://anonymous.example/root/"));
        var content = new TrackedContent(Encoding.ASCII.GetBytes("%PDF-1.7"));
        content.Headers.ContentType = new("application/pdf");
        var factory = new Factory(request => {
            Assert.Equal("https://anonymous.example/root/v1/render", request.RequestUri!.ToString());
            Assert.False(request.Headers.Contains("X-Api-Key"));
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("inline", body.RootElement.GetProperty("disposition").GetString());
            Assert.Equal("A4", body.RootElement.GetProperty("options").GetProperty("pageSize").GetString());
            Assert.True(body.RootElement.GetProperty("options").GetProperty("landscape").GetBoolean());
            Assert.True(!body.RootElement.TryGetProperty("printer", out var printer) || printer.ValueKind == JsonValueKind.Null);
            return new(HttpStatusCode.OK) { Content = content };
        });
        var service = Service(site, factory);
        var result = Assert.IsType<DocumentFileResult>(await service.SubmitAsync(config.Id, new("<html>Report</html>", "report.pdf",
            new() { PageSize = new("A4"), Landscape = true })));
        Assert.Equal(DocumentOutputKind.Pdf, result.Output);
        Assert.False(content.Disposed);
        Assert.Equal(0, factory.Disposals);
        Assert.Equal("%PDF-1.7", await new StreamReader(result.Content).ReadToEndAsync());
        await result.DisposeAsync();
        Assert.True(content.Disposed);
        Assert.Equal(1, factory.Disposals);
        Assert.DoesNotContain("anonymous.example", JsonSerializer.Serialize(service.GetDestinations()));
        Assert.Equal("AppBlocks", typeof(IDocumentOutputService).Assembly.GetName().Name);
    }

    [Fact]
    public async Task Printer_submission_uses_its_selected_configuration_and_returns_a_receipt_once()
    {
        using var site = new DocumentSettingsTests.SettingsSite();
        var config = await site.Store.SaveConfigurationAsync(new(null, "Reception", "https://printing.example/", "printer:reception", "print-key"));
        var factory = new Factory(request => {
            Assert.Equal("print-key", request.Headers.GetValues("X-Api-Key").Single());
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("reception", body.RootElement.GetProperty("printer").GetString());
            return Json("{\"jobId\":\"submission-1\",\"printer\":\"reception\",\"status\":\"submitted\",\"pageCount\":2}", HttpStatusCode.Accepted);
        });
        using var result = Assert.IsType<DocumentPrintResult>(await Service(site, factory).SubmitAsync(config.Id, new("<html>Report</html>")));
        Assert.Equal("submission-1", result.JobId);
        Assert.Equal(2, result.PageCount);
        Assert.Equal(DocumentOutputKind.Printer, result.Output);
        Assert.Equal(1, factory.Calls);
        Assert.Equal(1, factory.Disposals);
    }

    [Theory]
    [InlineData(401, false)]
    [InlineData(400, false)]
    [InlineData(503, true)]
    [InlineData(0, true)]
    [InlineData(-1, true)]
    public async Task Print_failures_report_uncertainty_and_never_retry_or_fall_back(int status, bool uncertain)
    {
        using var site = new DocumentSettingsTests.SettingsSite();
        var config = await site.Store.SaveConfigurationAsync(new(null, "Office", "https://printing.example/", "printer:office", "key"));
        var factory = new Factory(_ => status switch {
            0 => throw new HttpRequestException("Connection lost"),
            -1 => throw new TaskCanceledException("Timed out"),
            _ => new((HttpStatusCode)status),
        });
        var error = await Assert.ThrowsAsync<DocumentPrintException>(() => Service(site, factory).SubmitAsync(config.Id, new("<html>Report</html>")));
        Assert.Equal(uncertain, error.MayHavePrinted);
        Assert.Equal(1, factory.Calls);
        Assert.Equal(1, factory.Disposals);
    }

    [Fact]
    public async Task Missing_configuration_never_uses_another_destination()
    {
        using var site = new DocumentSettingsTests.SettingsSite();
        var factory = new Factory(_ => throw new Exception("Unexpected request"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Service(site, factory).SubmitAsync(Guid.NewGuid(), new("<html>Report</html>")));
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public async Task Only_printer_configurations_do_not_turn_template_document_GETs_into_print_jobs()
    {
        using var site = new DocumentSettingsTests.SettingsSite();
        await site.Store.DeleteAsync(DocumentSettingsStore.LegacyId);
        await site.Store.SaveConfigurationAsync(new(null, "Office", "https://printing.example/", "printer:office", "key"));
        var factory = new Factory(_ => throw new Exception("GET must not print"));
        using var result = await Service(site, factory).RenderDefaultPdfAsync(new() { Html = "<h1>Print in browser</h1>", FileName = "report.pdf" }, default);
        Assert.Equal(DocumentOutputKind.Html, result.Output);
        Assert.Equal(0, factory.Calls);
    }

    private static DocumentOutputService Service(DocumentSettingsTests.SettingsSite site, Factory factory)
        => new(site.Configuration, factory, NullLogger<DocumentOutputService>.Instance, site.Store);
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Factory(Func<HttpRequestMessage, HttpResponseMessage> respond) : IHttpClientFactory
    {
        public int Calls { get; private set; }
        public int Disposals { get; private set; }
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(DocumentOutputService.ClientName, name);
            return new(new Handler(request => { Calls++; return respond(request); }, () => Disposals++));
        }
        private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond, Action dispose) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
            protected override void Dispose(bool disposing) { if (disposing) dispose(); base.Dispose(disposing); }
        }
    }
    private sealed class TrackedContent(byte[] bytes) : ByteArrayContent(bytes)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
    }
}
