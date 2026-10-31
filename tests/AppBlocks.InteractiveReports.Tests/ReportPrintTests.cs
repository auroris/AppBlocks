using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using AppBlocks.DocumentOutput;
using AppBlocks.InteractiveReports.Reports;
using InteractiveReport.AspNetCore;
using InteractiveReport.Client.FileDownload;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AppBlocks.InteractiveReports.Tests;

public sealed class ReportPrintTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Html_download_uses_the_print_document_without_requiring_or_calling_Documents(bool install)
    {
        await using var site = await Site.Start(install);
        var state = JsonSerializer.SerializeToElement(site.Request("all")).GetProperty("document");
        // HTML is a read-only export: no destination selection or print antiforgery token required.
        var response = await site.Client.PostAsJsonAsync("/api/download/orders/html", state);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType.CharSet);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Contains(".html", response.Content.Headers.ContentDisposition.FileName);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var html = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("<style>", html);
        Assert.Contains("All matching rows", html);
        Assert.Contains("alice-two", html);
        Assert.Contains("alice-three", html);
        Assert.DoesNotContain("alice-one", html);
        Assert.DoesNotContain("bob-secret", html);
        Assert.Contains("&lt;safe&gt;", html);
        if (install)
        {
            await site.Options();
            var preview = await site.Preview();
            Assert.Equal(preview.GetProperty("html").GetString(), html);
        }
        var csv = await site.Client.PostAsJsonAsync("/api/download/orders/csv", state);
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        Assert.Equal(0, site.Gateway.Calls);
    }

    [Fact]
    public async Task Html_download_enforces_export_access_and_limits_without_returning_partial_files()
    {
        await using var site = await Site.Start(false);
        var state = JsonSerializer.SerializeToElement(site.Request("all")).GetProperty("document");
        Assert.Equal(HttpStatusCode.Forbidden, (await site.Client.PostAsJsonAsync("/api/download/locked/html", state)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await site.Client.PostAsJsonAsync("/api/download/capped/html", state)).StatusCode);
        site.Allow = false;
        Assert.Equal(HttpStatusCode.Forbidden, (await site.Client.PostAsJsonAsync("/api/download/orders/html", state)).StatusCode);
        Assert.Equal(0, site.Gateway.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_gateway_or_empty_configurations_offer_no_print_destinations(bool install)
    {
        await using var site = await Site.Start(install);
        site.Gateway.Destinations.Clear();
        var options = await site.Options();
        Assert.Empty(options.GetProperty("destinations").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, options.GetProperty("antiforgeryToken").ValueKind);
    }

    [Fact]
    public async Task Preview_requeries_authorized_rows_and_submits_the_exact_snapshot_only_once()
    {
        await using var site = await Site.Start();
        var options = await site.Options();
        Assert.Equal(2, options.GetProperty("destinations").GetArrayLength());
        Assert.DoesNotContain("apiKey", options.ToString());
        var preview = await site.Preview();
        var html = preview.GetProperty("html").GetString()!;
        Assert.Equal(2, preview.GetProperty("rows").GetInt32());
        Assert.DoesNotContain("bob-secret", html);
        Assert.DoesNotContain("alice-one", html);
        Assert.Contains("&lt;safe&gt;", html);
        Assert.True(html.IndexOf("alice-three", StringComparison.Ordinal) < html.IndexOf("alice-two", StringComparison.Ordinal));
        var submits = await Task.WhenAll(site.Submit(preview), site.Submit(preview));
        Assert.Single(submits, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(submits, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(1, site.Gateway.Calls);
        Assert.Equal(html, site.Gateway.Last!.Html);
        Assert.False(site.Gateway.Last.Options!.JavaScript);
        var receipt = await Read(submits.Single(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal("submitted", receipt.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Page_scope_keeps_paging_and_post_requires_antiforgery()
    {
        await using var site = await Site.Start();
        var missingToken = await site.Client.PostAsJsonAsync("/api/reports/orders/print/preview", site.Request("page"));
        Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);
        await site.Options();
        var preview = await site.Preview("page");
        Assert.Equal(1, preview.GetProperty("rows").GetInt32());
        Assert.Contains("alice-two", preview.GetProperty("html").GetString());
        Assert.DoesNotContain("alice-three", preview.GetProperty("html").GetString());
        site.Client.DefaultRequestHeaders.Remove("X-User");
        Assert.Equal(HttpStatusCode.Unauthorized, (await site.Client.GetAsync("/api/reports/orders/print/options")).StatusCode);
        Assert.Equal(0, site.Gateway.Calls);
    }

    [Fact]
    public async Task Changed_access_data_or_destination_prevents_submission()
    {
        await using var site = await Site.Start();
        await site.Options();
        var preview = await site.Preview();
        site.Allow = false;
        Assert.Equal(HttpStatusCode.Forbidden, (await site.Submit(preview)).StatusCode);
        site.Allow = true;
        site.Gateway.Destinations[0] = site.Gateway.Destinations[0] with { Printer = "Changed printer" };
        Assert.Equal(HttpStatusCode.Conflict, (await site.Submit(preview)).StatusCode);
        preview = await site.Preview();
        await site.Sql("UPDATE DATA SET NAME = 'changed after preview' WHERE ID = 2");
        Assert.Equal(HttpStatusCode.Conflict, (await site.Submit(preview)).StatusCode);
        Assert.Equal(0, site.Gateway.Calls);
    }

    [Fact]
    public async Task Tokens_cannot_be_used_by_another_user_and_truncation_does_not_print_partial_data()
    {
        await using var site = await Site.Start();
        await site.Options();
        var preview = await site.Preview();
        site.Client.DefaultRequestHeaders.Remove("X-User");
        site.Client.DefaultRequestHeaders.Add("X-User", "bob");
        await site.Options();
        Assert.Equal(HttpStatusCode.Conflict, (await site.Submit(preview)).StatusCode);
        site.Client.DefaultRequestHeaders.Remove("X-User");
        site.Client.DefaultRequestHeaders.Add("X-User", "alice");
        await site.Options();
        var capped = await site.Client.PostAsJsonAsync("/api/reports/capped/print/preview", site.Request("all"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, capped.StatusCode);
        Assert.Equal(0, site.Gateway.Calls);
    }

    [Fact]
    public async Task Uncertain_printer_failure_consumes_token_and_never_retries()
    {
        await using var site = await Site.Start();
        await site.Options();
        var preview = await site.Preview();
        site.Gateway.Fail = true;
        var response = await site.Submit(preview);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.True((await Read(response)).GetProperty("mayHavePrinted").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await site.Submit(preview)).StatusCode);
        Assert.Equal(1, site.Gateway.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Files_stream_and_dispose_their_owner_with_explicit_html_fallback(bool fallback)
    {
        await using var site = await Site.Start();
        await site.Options();
        site.Gateway.File = true; site.Gateway.Fallback = fallback;
        var preview = await site.Preview();
        var response = await site.Submit(preview);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("test file", await response.Content.ReadAsStringAsync());
        Assert.Equal(fallback ? "true" : "false", response.Headers.GetValues("X-AppBlocks-Html-Fallback").Single());
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.True(site.Gateway.Disposed);
    }

    private static async Task<JsonElement> Read(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private sealed class Gateway : IDocumentOutputService, IDisposable
    {
        public readonly List<DocumentDestination> Destinations = [new(Guid.NewGuid(), "Office", DocumentOutputKind.Printer, "Office printer"), new(Guid.NewGuid(), "PDF", DocumentOutputKind.Pdf, null)];
        public int Calls;
        public bool Fail, File, Fallback, Disposed;
        public DocumentRequest? Last;
        public IReadOnlyList<DocumentDestination> GetDestinations() => Destinations;
        public Task<DocumentOutputResult> SubmitAsync(Guid configurationId, DocumentRequest document, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls); Last = document;
            if (Fail) throw new DocumentPrintException("Private upstream detail", true);
            return Task.FromResult<DocumentOutputResult>(File
                ? new DocumentFileResult(new MemoryStream("test file"u8.ToArray()), Fallback ? "text/html" : "application/pdf", Fallback ? "report.html" : "report.pdf", Fallback, this)
                : new DocumentPrintResult("job-123", "Office printer", 2));
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class Site : IAsyncDisposable
    {
        public Gateway Gateway { get; } = new();
        public bool Allow = true;
        public HttpClient Client = null!;
        private WebApplication app = null!;
        private SqliteConnection database = null!;
        public static async Task<Site> Start(bool install = true)
        {
            var site = new Site();
            var cs = $"Data Source=print-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
            site.database = new SqliteConnection(cs); await site.database.OpenAsync();
            await site.Sql("CREATE TABLE DATA (ID INTEGER, NAME TEXT, OWNER TEXT); INSERT INTO DATA VALUES (1,'alice-one','alice'),(2,'alice-two','alice'),(3,'alice-three','alice'),(4,'bob-secret','bob');");
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            var config = new Dictionary<string, string?>();
            foreach (var name in new[] { "orders", "capped", "locked" })
            {
                config[$"InteractiveReport:Reports:{name}:Connection"] = "data";
                config[$"InteractiveReport:Reports:{name}:Dialect"] = "Sqlite";
                config[$"InteractiveReport:Reports:{name}:Sql"] = "SELECT ID, NAME, OWNER FROM DATA WHERE {{RowRestriction}}";
                config[$"InteractiveReport:Reports:{name}:Authorization:AllowAnonymous"] = "true";
            }
            config["InteractiveReport:Reports:capped:MaxRows"] = "1";
            config["InteractiveReport:Reports:capped:MaxPageSize"] = "1";
            config["InteractiveReport:Reports:capped:DefaultPageSize"] = "1";
            config["InteractiveReport:Reports:locked:Features:0"] = "search";
            builder.Configuration.AddInMemoryCollection(config);
            builder.Services.AddInteractiveReports(builder.Configuration).AddConnection("data", _ => new SqliteConnection(cs))
                .UseAuthorization((_, _) => ValueTask.FromResult(site.Allow))
                .UseRowRestrictions((request, _) => ValueTask.FromResult(RowRestriction.Where("OWNER = ?", request.UserId)));
            builder.Services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");
            builder.Services.AddSingleton<ReportPrintPreviews>();
            builder.Services.AddInteractiveReportFileDownload();
            if (install) builder.Services.AddSingleton<IDocumentOutputService>(site.Gateway);
            site.app = builder.Build();
            site.app.Use(async (http, next) => {
                if (http.Request.Headers.TryGetValue("X-User", out var user))
                    http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.ToString())], "Test"));
                await next();
            });
            site.app.MapReportPrinting("/api/reports");
            site.app.MapInteractiveReportFileDownload("/api/download");
            await site.app.StartAsync();
            site.Client = new HttpClient(new HttpClientHandler { CookieContainer = new() }) {
                BaseAddress = new Uri(site.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()),
            };
            site.Client.DefaultRequestHeaders.Add("X-User", "alice"); return site;
        }
        public async Task Sql(string sql) { await using var command = database.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }
        public async Task<JsonElement> Options()
        {
            var response = await Client.GetAsync("/api/reports/orders/print/options");
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            var result = await Read(response);
            Client.DefaultRequestHeaders.Remove("RequestVerificationToken");
            if (result.GetProperty("antiforgeryToken").GetString() is { } token) Client.DefaultRequestHeaders.Add("RequestVerificationToken", token);
            return result;
        }
        public object Request(string scope) => new {
            destinationId = Gateway.Destinations[0].Id, scope, pageSize = "A4", landscape = false,
            document = new { page = new { index = 2, size = 1 }, activeTable = "base", tables = new Dictionary<string, object> {
                ["base"] = new { from = "definition", composables = new object[] {
                    new { kind = "filter", filters = new[] { new { expr = "ID > 1" } } },
                    new { kind = "sort", sorts = new[] { new { col = "ID", dir = "desc" } } },
                    new { kind = "select", columns = new[] { "NAME", "ID" } },
                    new { kind = "labels", labels = new { NAME = "Name <safe>" } },
                } },
            } },
        };
        public async Task<JsonElement> Preview(string scope = "all")
        {
            var response = await Client.PostAsJsonAsync("/api/reports/orders/print/preview", Request(scope));
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync()); return await Read(response);
        }
        public Task<HttpResponseMessage> Submit(JsonElement preview) => Client.PostAsJsonAsync("/api/reports/orders/print/submit", new { token = preview.GetProperty("token").GetString() });
        public async ValueTask DisposeAsync() { Client.Dispose(); await app.StopAsync(); await app.DisposeAsync(); await database.DisposeAsync(); }
    }
}
