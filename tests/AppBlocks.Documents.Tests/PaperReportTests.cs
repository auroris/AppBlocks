using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Pressroom.Contracts;
using AppBlocks.Blocks;
using AppBlocks.DataSources;
using AppBlocks.Documents.PaperReports;

namespace AppBlocks.Documents.Tests;

public sealed class PaperReportTests
{
    [Fact]
    public async Task Unreadable_assets_keep_their_references_and_the_rest_still_packages()
    {
        var root = Directory.CreateTempSubdirectory("paper-best-effort-");
        Directory.CreateDirectory(Path.Combine(root.FullName, "media"));
        await File.WriteAllBytesAsync(Path.Combine(root.FullName, "media", "logo.png"), [1, 2, 3]);
        using var provider = new PhysicalFileProvider(root.FullName);
        var requests = new HttpFactory(request => request.RequestUri!.Host switch {
            "missing.example" => new HttpResponseMessage(HttpStatusCode.NotFound),
            "offline.example" => throw new HttpRequestException("No such host is known."),
            _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout."),
        });
        var log = new RecordingLogger<SelfContainedHtml>();
        var renderer = new SelfContainedHtml(new EnvironmentStub { WebRootFileProvider = provider }, requests, log, Configuration());
        var html = await renderer.EmbedAsync("""
            <html><head><link rel="stylesheet" href="https://offline.example/site.css"><style>@import url("https://slow.example/base.css"); body { background: url('https://missing.example/paper.png') }</style></head>
            <body><img src="https://missing.example/a.png"><img src="https://missing.example/a.png"><img src="file:///C:/Windows/win.ini"><img src="http://[bad"><img src="/media/logo.png"></body></html>
            """, new Uri("https://example.invalid/"), "", null, default);
        Assert.Contains("""<link rel="stylesheet" href="https://offline.example/site.css">""", html);
        Assert.Contains("""@import url("https://slow.example/base.css");""", html);
        Assert.Contains("url('https://missing.example/paper.png')", html);
        Assert.Equal(2, Regex.Matches(html, Regex.Escape("src=\"https://missing.example/a.png\"")).Count);
        Assert.Contains("src=\"file:///C:/Windows/win.ini\"", html);
        Assert.Contains("src=\"http://[bad\"", html);
        Assert.Contains("data:image/png;base64,AQID", html);
        // Each unreadable address is asked once: the stylesheet, the import, the background and the repeated image.
        Assert.Equal(4, requests.Calls);
        Assert.Contains(log.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("https://offline.example/site.css"));
        Assert.Contains(log.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("https://slow.example/base.css"));
    }

    [Fact]
    public async Task Site_assets_come_only_from_disk_and_loopback_or_link_local_assets_are_never_requested()
    {
        var requests = new HttpFactory(_ => throw new InvalidOperationException("No asset may be requested."));
        var log = new RecordingLogger<SelfContainedHtml>();
        var renderer = new SelfContainedHtml(new EnvironmentStub(), requests, log, Configuration());
        // A forged Host header names the origin, so a site asset missing from disk is never requested from it.
        string[] sources = ["/media/absent.png", "https://attacker.example/media/absent.png", "http://127.0.0.1/a.png", "http://localhost:8080/a.png",
            "http://[::1]/a.png", "http://169.254.169.254/latest/meta-data/", "http://[fe80::1]/a.png", "http://0.0.0.0/a.png", "http://printer.localhost/a.png"];
        var html = await renderer.EmbedAsync(string.Concat(sources.Select(source => $"<img src=\"{source}\">")), new Uri("https://attacker.example/"), "", null, default);
        Assert.Equal(0, requests.Calls);
        Assert.All(sources, source => Assert.Contains($"src=\"{source}\"", html));
        Assert.Contains(log.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("169.254.169.254"));
        Assert.DoesNotContain(log.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("absent.png"));
    }

    [Fact]
    public async Task The_asset_client_never_connects_to_a_loopback_address()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new HttpClient(SelfContainedHtml.CreateHandler());
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"http://127.0.0.1:{port}/asset.png"));
        Assert.False(listener.Pending());
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.42.0.9", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("fe80::1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("192.168.1.20", true)]
    [InlineData("::ffff:192.168.1.20", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.127.255.254", true)]
    [InlineData("fc00::1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("172.15.255.254", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("100.63.255.254", false)]
    [InlineData("100.128.0.1", false)]
    [InlineData("192.169.0.1", false)]
    [InlineData("203.0.113.7", false)]
    [InlineData("2001:db8::1", false)]
    public void Assets_are_refused_from_loopback_link_local_unspecified_and_private_addresses(string address, bool refused) =>
        Assert.Equal(refused, SelfContainedHtml.IsRefusedAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("10.20.30.40", true)]
    [InlineData("10.21.0.1", false)]
    [InlineData("192.168.1.20", true)]
    [InlineData("::ffff:192.168.1.20", true)]
    [InlineData("192.168.1.21", false)]
    [InlineData("fd00::7", true)]
    [InlineData("127.0.0.1", false)]
    public void The_allowed_asset_hosts_setting_re_allows_the_addresses_and_ranges_it_names(string address, bool allowed)
    {
        var hosts = SelfContainedHtml.AssetHosts.From(Configuration(($"{SelfContainedHtml.AllowedHostsSetting}:0", "10.20.0.0/16"),
            ($"{SelfContainedHtml.AllowedHostsSetting}:1", "192.168.1.20"), ($"{SelfContainedHtml.AllowedHostsSetting}:2", "fd00::/8"),
            ($"{SelfContainedHtml.AllowedHostsSetting}:3", "reports.intranet")));
        Assert.Equal(!allowed, SelfContainedHtml.IsRefusedAddress(IPAddress.Parse(address), hosts));
        Assert.True(hosts.Allows("REPORTS.intranet."));
        Assert.False(hosts.Allows("other.intranet"));
    }

    [Fact]
    public async Task A_host_the_setting_names_is_fetched_from_and_others_on_private_addresses_are_not()
    {
        var requests = new HttpFactory(request => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("image/png") } },
        });
        var renderer = new SelfContainedHtml(new EnvironmentStub(), requests, NullLogger<SelfContainedHtml>.Instance,
            Configuration(($"{SelfContainedHtml.AllowedHostsSetting}:0", "printer.localhost"), ($"{SelfContainedHtml.AllowedHostsSetting}:1", "10.1.0.0/16")));
        var html = await renderer.EmbedAsync("<img src='http://printer.localhost/a.png'><img src='http://10.1.2.3/b.png'><img src='http://10.2.0.1/c.png'>",
            new Uri("https://example.invalid/"), "", null, default);
        Assert.Equal(2, requests.Calls);
        Assert.Equal(2, Regex.Matches(html, Regex.Escape("data:image/png;base64,AQID")).Count);
        Assert.Contains("src='http://10.2.0.1/c.png'", html);
    }

    [Theory]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("[::ffff:10.0.0.1]")]
    public void A_mapped_IPv6_asset_host_allows_exactly_its_IPv4_address(string entry)
    {
        var hosts = SelfContainedHtml.AssetHosts.From(Configuration(($"{SelfContainedHtml.AllowedHostsSetting}:0", entry)));
        Assert.False(SelfContainedHtml.IsRefusedAddress(IPAddress.Parse("10.0.0.1"), hosts));
        Assert.False(SelfContainedHtml.IsRefusedAddress(IPAddress.Parse("::ffff:10.0.0.1"), hosts));
        Assert.True(SelfContainedHtml.IsRefusedAddress(IPAddress.Parse("10.0.0.2"), hosts));
        Assert.True(SelfContainedHtml.IsRefusedAddress(IPAddress.Parse("::ffff:10.0.0.2"), hosts));
    }

    [Fact]
    public void A_paper_report_reads_a_bounded_row_limit_and_a_culture_that_exists()
    {
        static Func<string, object?> Values(params (string Alias, string Value)[] values)
        {
            var map = values.ToDictionary(value => value.Alias, value => (object?)value.Value);
            map.TryAdd("dataSource", Guid.NewGuid().ToString());
            return alias => map.GetValueOrDefault(alias);
        }
        Assert.Equal(PaperReportDefinition.DefaultMaximumRows, PaperReportDefinition.Read(Values()).MaximumRows);
        Assert.Equal(PaperReportDefinition.MaximumRowsLimit, PaperReportDefinition.Read(Values(("maximumRows", "50000"))).MaximumRows);
        Assert.Contains("50000", Assert.Throws<DataSourceException>(() => PaperReportDefinition.Read(Values(("maximumRows", "50001")))).Message);
        Assert.Throws<DataSourceException>(() => PaperReportDefinition.Read(Values(("maximumRows", "0"))));
        Assert.Equal("fr-CA", PaperReportDefinition.Read(Values(("culture", "fr-CA"))).Culture);
        Assert.Equal(":PARAM.culture", PaperReportDefinition.Read(Values(("culture", ":PARAM.culture"))).Culture);
        Assert.Contains("'english'", Assert.Throws<DataSourceException>(() => PaperReportDefinition.Read(Values(("culture", "english")))).Message);
    }

    [Fact]
    public void A_paper_report_without_a_culture_time_zone_or_footer_uses_the_request_the_server_and_page_numbers()
    {
        static Func<string, object?> Values(params (string Alias, string Value)[] values)
        {
            var map = values.ToDictionary(value => value.Alias, value => (object?)value.Value);
            map.TryAdd("dataSource", Guid.NewGuid().ToString());
            return alias => map.GetValueOrDefault(alias);
        }
        var variables = new AppBindVariables("1", null, "pilot", null, DateTimeOffset.Now, new QueryCollection());
        var request = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
        var plain = PaperReportDefinition.Read(Values());
        Assert.Equal("", plain.Culture);
        Assert.Same(request, plain.ResolveCulture(variables, request));
        Assert.Equal("fr-FR", PaperReportDefinition.Read(Values(("culture", "fr-FR"))).ResolveCulture(variables, request).Name);
        // A bind variable that resolves to nothing falls back to the request's culture too.
        Assert.Same(request, PaperReportDefinition.Read(Values(("culture", ":PARAM.culture"))).ResolveCulture(variables, request));
        Assert.Equal(AppBlocks.DocumentOutput.DocumentRenderOptions.ServerTimeZone, plain.Options.TimeZone);
        Assert.Equal(AppBlocks.DocumentOutput.DocumentRenderOptions.PageNumberFooter, plain.Options.FooterTemplate);
        Assert.DoesNotContain("Page ", plain.Options.FooterTemplate);
        var chosen = PaperReportDefinition.Read(Values(("pageOptions", """{ "timeZone": "Europe/Paris", "footerTemplate": "<div>Seite</div>" }""")));
        Assert.Equal(("Europe/Paris", "<div>Seite</div>"), (chosen.Options.TimeZone, chosen.Options.FooterTemplate));
    }

    [Theory]
    [InlineData("{}", "@page { margin: 20mm 15mm 20mm 15mm; }")]
    [InlineData("""{ "landscape": true }""", "@page { size: landscape; margin: 20mm 15mm 20mm 15mm; }")]
    [InlineData("""{ "pageSize": "A5", "landscape": true }""", "@page { size: A5 landscape; margin: 20mm 15mm 20mm 15mm; }")]
    [InlineData("""{ "pageSize": { "width": "100mm", "height": "200mm" }, "margins": { "top": "1cm", "right": "1cm", "bottom": "1cm", "left": "1cm" } }""", "@page { size: 100mm 200mm; margin: 1cm 1cm 1cm 1cm; }")]
    [InlineData("""{ "preferCssPageSize": true }""", null)]
    public void The_page_rule_names_a_paper_only_when_the_options_do(string options, string? expected)
        => Assert.Equal(expected, PaperReportRenderer.PageStyle(PaperReportDefinition.Read(alias => alias switch {
            "dataSource" => Guid.NewGuid().ToString(), "pageOptions" => options, _ => null,
        }).Options));

    [Fact]
    public async Task A_template_or_the_site_can_leave_the_print_stylesheet_out()
    {
        var renderer = new SelfContainedHtml(new EnvironmentStub(), new HttpFactory(_ => new HttpResponseMessage(HttpStatusCode.NotFound)),
            NullLogger<SelfContainedHtml>.Instance, Configuration());
        var uri = new Uri("https://example.invalid/");
        var kept = await renderer.EmbedAsync("<html><head></head><body></body></html>", uri, "", DocumentPresentation.PackageStylesheet, default);
        Assert.Contains(DocumentPresentation.PackageStylesheet, kept);
        var optedOut = await renderer.EmbedAsync($"<html><head><meta name=\"{SelfContainedHtml.PrintStylesheetMeta}\" content=\"none\"></head><body></body></html>",
            uri, "", DocumentPresentation.PackageStylesheet, default);
        Assert.DoesNotContain(DocumentPresentation.PackageStylesheet, optedOut);

        async Task<string?> Stylesheet(params (string Key, string? Value)[] settings)
            => (await new DefaultDocumentPresentationProvider(Configuration(settings)).ResolveAsync(null!, default)).PrintStylesheet;
        Assert.Equal(DocumentPresentation.PackageStylesheet, await Stylesheet());
        Assert.Null(await Stylesheet((DocumentPresentation.PrintStylesheetSetting, "")));
        Assert.Equal("/css/print.css", await Stylesheet((DocumentPresentation.PrintStylesheetSetting, "/css/print.css")));
    }

    [Fact]
    public void Installed_templates_are_added_to_the_paper_report_type_unless_the_site_turns_it_off()
    {
        static Umbraco.Cms.Core.Models.ITemplate Template(int id)
        {
            var template = System.Reflection.DispatchProxy.Create<Umbraco.Cms.Core.Models.ITemplate, TemplateStub>();
            ((TemplateStub)(object)template).Id = id;
            return template;
        }
        var (one, two, three) = (Template(1), Template(2), Template(3));
        Assert.Null(PaperReportTemplates.WithAllInstalled([one, two], [one, two]));
        // Only adds: the type's own order and choices stay, and a template no longer installed drops out.
        Assert.Equal([2, 1, 3], PaperReportTemplates.WithAllInstalled([two, one, Template(9)], [one, two, three])!.Select(t => t.Id));
        Assert.True(PaperReportTemplates.AllowsAllTemplates(Configuration()));
        Assert.False(PaperReportTemplates.AllowsAllTemplates(Configuration((PaperReportTemplates.AllowAllTemplatesSetting, "false"))));
    }

    [Fact]
    public void A_document_that_names_no_locale_or_time_zone_gets_the_request_culture_and_the_server_zone()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("nl-NL");
            var options = DocumentOutputService.Convert(null);
            Assert.Equal(("nl-NL", AppBlocks.DocumentOutput.DocumentRenderOptions.ServerTimeZone), (options.Locale, options.TimeZone));
            Assert.Equal(AppBlocks.DocumentOutput.DocumentRenderOptions.PageNumberFooter, options.FooterTemplate);
            var named = DocumentOutputService.Convert(new() { Locale = "ja-JP", TimeZone = "Asia/Tokyo" });
            Assert.Equal(("ja-JP", "Asia/Tokyo"), (named.Locale, named.TimeZone));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Paper_reports_bind_query_parameters_the_way_forms_do()
    {
        var query = PaperReportDefinition.Query(new QueryCollection(new Dictionary<string, StringValues> {
            ["id"] = "4", ["format"] = "html", [AppBlockContent.CultureParameter] = "fr", ["region"] = new(["NORTH", "WEST"]),
            ["not-a-name"] = "x", ["notes"] = new string('x', 4001),
        }));
        Assert.Equal(new[] { "id" }, query.Keys);
        var variables = new AppBindVariables("1", null, "pilot", null, DateTimeOffset.Now, query);
        var forwarding = PaperReportDefinition.Read(alias => alias == "dataSource" ? Guid.NewGuid().ToString() : null);
        static string Bound(IReadOnlyDictionary<string, string> parameters) => string.Join("&", parameters.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value));
        Assert.Equal("id=4", Bound(forwarding.BindParameters(variables)));
        var mapped = forwarding with { Parameters = new Dictionary<string, string> { ["order"] = ":PARAM.id", ["area"] = ":PARAM.region", ["kind"] = "summary" } };
        Assert.Equal("kind=summary&order=4", Bound(mapped.BindParameters(variables)));
    }

    [Fact]
    public async Task Unusable_service_settings_and_requests_the_client_refuses_deliver_html()
    {
        var factory = new HttpFactory(_ => throw new InvalidOperationException("No render call expected."));
        foreach (var delivery in new[] {
            Delivery(factory, "pressroom without a scheme"),
            Delivery(factory, "ftp://pressroom.example/"),
        })
        {
            var http = Http("reports.example");
            await (await delivery.DeliverAsync(Document(), http, default)).ExecuteAsync(http);
            Assert.StartsWith("text/html", http.Response.ContentType);
        }
        // The client checks the request itself and refuses an invalid locale before sending anything.
        var invalid = Http("reports.example");
        await (await Delivery(factory, true).DeliverAsync(Document() with { Options = new RenderOptions { Locale = "" } }, invalid, default)).ExecuteAsync(invalid);
        Assert.StartsWith("text/html", invalid.Response.ContentType);
        Assert.Equal(0, factory.Calls);

        var log = new RecordingLogger<DocumentOutputService>();
        var rejected = Delivery(new HttpFactory(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)), "https://pressroom.example/", log);
        var unauthorized = Http("reports.example");
        await (await rejected.DeliverAsync(Document(), unauthorized, default)).ExecuteAsync(unauthorized);
        Assert.StartsWith("text/html", unauthorized.Response.ContentType);
        Assert.Contains(log.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("Unauthorized"));
    }

    [Fact]
    public async Task Portable_html_embeds_nested_css_images_fonts_and_virtual_path_assets()
    {
        var root = Directory.CreateTempSubdirectory("paper-assets-");
        Directory.CreateDirectory(Path.Combine(root.FullName, "css"));
        Directory.CreateDirectory(Path.Combine(root.FullName, "media"));
        await File.WriteAllTextAsync(Path.Combine(root.FullName, "css", "print.css"), "@import url(base.css); body { background-image:url('../media/logo.png'); }");
        await File.WriteAllTextAsync(Path.Combine(root.FullName, "css", "base.css"), "@font-face {font-family:Example;src:url('../media/font.woff2')}");
        await File.WriteAllBytesAsync(Path.Combine(root.FullName, "media", "logo.png"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(root.FullName, "media", "font.woff2"), [4, 5, 6]);
        using var provider = new PhysicalFileProvider(root.FullName);
        var requests = new HttpFactory(_ => throw new InvalidOperationException("Local assets must not need HTTP."));
        var renderer = new SelfContainedHtml(new EnvironmentStub { WebRootFileProvider = provider }, requests, NullLogger<SelfContainedHtml>.Instance, Configuration());
        var html = await renderer.EmbedAsync("<html><head><link rel='stylesheet' href='/my-app/css/print.css'></head><body><img src='/my-app/media/logo.png' srcset='/my-app/media/logo.png 1x, /my-app/media/logo.png 2x'></body></html>",
            new Uri("https://example.invalid/my-app/"), "/my-app", null, default);
        Assert.DoesNotContain("@import", html);
        Assert.DoesNotContain("<link", html);
        Assert.Contains("data:image/png;base64,AQID", html);
        Assert.Contains("data:font/woff2;base64,BAUG", html);
        Assert.DoesNotContain("/my-app/media/", html);
        Assert.Equal(0, requests.Calls);
    }

    [Fact]
    public async Task Relative_assets_follow_the_page_url_and_explicit_html_base()
    {
        var root = Directory.CreateTempSubdirectory("paper-relative-");
        Directory.CreateDirectory(Path.Combine(root.FullName, "reports", "images"));
        await File.WriteAllBytesAsync(Path.Combine(root.FullName, "reports", "images", "logo.png"), [7, 8, 9]);
        using var provider = new PhysicalFileProvider(root.FullName);
        var factory = new HttpFactory(_ => throw new InvalidOperationException("Local assets must not need HTTP."));
        var renderer = new SelfContainedHtml(new EnvironmentStub { WebRootFileProvider = provider }, factory, NullLogger<SelfContainedHtml>.Instance, Configuration());
        foreach (var (html, uri) in new[] {
            ("<img src='images/logo.png'>", "https://example.invalid/my-app/reports/"),
            ("<html><head><base href='../reports/'></head><body><img src='images/logo.png'></body></html>", "https://example.invalid/my-app/other/"),
        })
        {
            var output = await renderer.EmbedAsync(html, new(uri), "/my-app", null, default);
            Assert.Contains("data:image/png;base64,BwgJ", output);
            Assert.DoesNotContain("<base", output);
        }
    }

    [Theory]
    [InlineData("localhost", false, false)]
    [InlineData("127.0.0.1", false, false)]
    [InlineData("[::1]", false, false)]
    [InlineData("reports.example", false, false)]
    [InlineData("reports.example", true, true)]
    public async Task Unconfigured_or_explicit_html_opens_inline_without_a_service(string host, bool configured, bool explicitHtml)
    {
        var factory = new HttpFactory(_ => throw new InvalidOperationException("No render call expected."));
        var http = Http(host, explicitHtml ? "?format=html" : "");
        var delivery = Delivery(factory, configured);
        var result = await delivery.DeliverAsync(Document(), http, default);
        await result.ExecuteAsync(http);
        Assert.StartsWith("text/html", http.Response.ContentType);
        Assert.Contains("orders.html", http.Response.Headers.ContentDisposition.ToString());
        Assert.StartsWith("inline;", http.Response.Headers.ContentDisposition.ToString());
        Assert.Contains("<h1>Orders</h1>", Encoding.UTF8.GetString(((MemoryStream)http.Response.Body).ToArray()));
        Assert.Equal(0, factory.Calls);
    }

    [Theory]
    [InlineData(503)]
    [InlineData(429)]
    [InlineData(0)]
    public async Task Service_failure_delivers_the_same_printable_html(int status)
    {
        var factory = new HttpFactory(_ => status == 0 ? throw new HttpRequestException("Offline") : new HttpResponseMessage((HttpStatusCode)status));
        var http = Http("reports.example");
        var result = await Delivery(factory, true).DeliverAsync(Document(), http, default);
        await result.ExecuteAsync(http);
        Assert.StartsWith("text/html", http.Response.ContentType);
        Assert.Equal(1, factory.Calls);
    }

    [Theory]
    [InlineData("reports.example")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    public async Task Configured_sites_can_open_the_pressroom_pdf_inline_without_an_api_key(string host)
    {
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.7\nexample");
        var factory = new HttpFactory(request => {
            Assert.False(request.Headers.Contains("X-Api-Key"));
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.False(body.RootElement.TryGetProperty("disposition", out _));
            Assert.True(!body.RootElement.TryGetProperty("printer", out var printer) || printer.ValueKind == JsonValueKind.Null);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            response.Content.Headers.ContentType = new("application/pdf");
            return response;
        });
        var http = Http(host);
        var result = await Delivery(factory, true).DeliverAsync(Document(), http, default);
        await result.ExecuteAsync(http);
        Assert.Equal("application/pdf", http.Response.ContentType);
        Assert.StartsWith("inline;", http.Response.Headers.ContentDisposition.ToString());
        Assert.Equal(bytes, ((MemoryStream)http.Response.Body).ToArray());
        Assert.Equal(1, factory.Calls);
    }

    private static PaperReportDocument Document() => new("<!doctype html><html><body><h1>Orders</h1></body></html>", "orders.pdf", new());

    [Fact]
    public async Task Backoffice_changes_apply_to_the_next_document_without_recreating_delivery()
    {
        using var site = new DocumentSettingsTests.SettingsSite();
        var factory = new HttpFactory(request => {
            Assert.Equal("https://saved.example/printing/v1/render", request.RequestUri!.ToString());
            Assert.Equal("new-key", request.Headers.GetValues("X-Api-Key").Single());
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.7")) };
            response.Content.Headers.ContentType = new("application/pdf");
            return response;
        });
        var delivery = new PaperReportDelivery(new DocumentOutputService(factory, NullLogger<DocumentOutputService>.Instance, site.Store));
        await site.Store.DeleteAsync(DocumentSettingsStore.ConfiguredId);
        var first = Http("localhost");
        await (await delivery.DeliverAsync(Document(), first, default)).ExecuteAsync(first);
        Assert.StartsWith("text/html", first.Response.ContentType);
        Assert.Equal(0, factory.Calls);
        await site.Store.SaveConfigurationAsync(new(null, "Printing", "https://saved.example/printing", ApiKey: "new-key"));
        var second = Http("localhost");
        await (await delivery.DeliverAsync(Document(), second, default)).ExecuteAsync(second);
        Assert.Equal("application/pdf", second.Response.ContentType);
        Assert.Equal(1, factory.Calls);
    }

    [Fact]
    public async Task A_cancelled_document_request_is_not_converted_into_a_service_failure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var logger = new RecordingLogger<DocumentOutputService>();
        var delivery = Delivery(new HttpFactory(_ => throw new OperationCanceledException(cancellation.Token)), "https://pressroom.example/", logger);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delivery.DeliverAsync(Document(), Http("localhost"), cancellation.Token));
        Assert.Empty(logger.Entries);
    }
    private static PaperReportDelivery Delivery(HttpFactory factory, bool configured) => Delivery(factory, configured ? "https://pressroom.example/" : null);
    /// <summary>Delivery through the document gateway, with <paramref name="url"/> as the configured Pressroom.</summary>
    private static PaperReportDelivery Delivery(HttpFactory factory, string? url, ILogger<DocumentOutputService>? log = null)
    {
        var store = new DocumentSettingsStore(Configuration(("AppBlocks:Documents:Pressroom:Url", url)),
            new DocumentSettingsTests.TestEnvironment { ContentRootPath = Directory.CreateTempSubdirectory("paper-delivery-").FullName },
            new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        return new(new DocumentOutputService(factory, log ?? NullLogger<DocumentOutputService>.Instance, store));
    }
    private static DefaultHttpContext Http(string host, string query = "")
    {
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        http.Request.Host = new(host);
        http.Request.QueryString = new(query);
        http.Response.Body = new MemoryStream();
        return http;
    }
    public class TemplateStub : System.Reflection.DispatchProxy
    {
        public int Id { get; set; }
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args) => method!.Name == "get_Id" ? Id : throw new NotImplementedException(method.Name);
    }
    private sealed class HttpFactory(Func<HttpRequestMessage, HttpResponseMessage> respond) : IHttpClientFactory
    {
        public int Calls { get; private set; }
        public HttpClient CreateClient(string name) => new(new Handler(request => { Calls++; return respond(request); })) { BaseAddress = new("https://pressroom.example/") };
        private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
        }
    }
    private sealed class EnvironmentStub : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Tests";
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    private static IConfiguration Configuration(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value))).Build();
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
