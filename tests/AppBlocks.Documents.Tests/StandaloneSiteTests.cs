using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AppBlocks.Connections;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Sync;
using Umbraco.Extensions;

namespace AppBlocks.Documents.Tests;

[Collection("Standalone Umbraco")]
public sealed class StandaloneSiteTests
{
    [Fact]
    public async Task Plain_Umbraco_installs_documents_and_serves_authorized_inline_html_and_admin_settings()
    {
        var root = Directory.CreateTempSubdirectory("documents-site-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
            ApplicationName = typeof(StandaloneSiteTests).Assembly.GetName().Name, ContentRootPath = root, EnvironmentName = "Development",
        });
        builder.WebHost.UseSetting(WebHostDefaults.StaticWebAssetsKey,
            Path.Combine(AppContext.BaseDirectory, typeof(StandaloneSiteTests).Assembly.GetName().Name + ".staticwebassets.runtime.json"))
            .UseStaticWebAssets().UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        const string password = "Documents!Test5278";
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["ConnectionStrings:umbracoDbDSN"] = $"Data Source={Path.Combine(root, "umbraco.db")};Pooling=False",
            ["ConnectionStrings:umbracoDbDSN_ProviderName"] = "Microsoft.Data.Sqlite",
            ["Umbraco:CMS:Global:Id"] = Guid.NewGuid().ToString(), ["Umbraco:CMS:Global:UseHttps"] = "false",
            ["Umbraco:CMS:ModelsBuilder:ModelsMode"] = "Nothing",
            ["Umbraco:CMS:TypeFinder:AdditionalAssemblyExclusionEntries:0"] = "xunit.",
            ["Umbraco:CMS:Unattended:InstallUnattended"] = "true", ["Umbraco:CMS:Unattended:UpgradeUnattended"] = "true",
            ["Umbraco:CMS:Unattended:UnattendedUserName"] = "Documents test",
            ["Umbraco:CMS:Unattended:UnattendedUserEmail"] = "documents@example.test",
            ["Umbraco:CMS:Unattended:UnattendedUserPassword"] = password,
            ["uSync:Settings:ImportAtStartup"] = "None", ["uSync:Settings:ExportOnSave"] = "None",
            ["Pressroom:Url"] = "https://configured.example/",
        });
        builder.Services.Configure<InstallDefaultDataSettings>(Constants.Configuration.NamedOptions.InstallDefaultData.Languages,
            settings => { settings.InstallData = InstallDefaultDataOption.Values; settings.Values = new HashSet<string> { "en" }; });
        // Use only standard Umbraco startup: the installed package's composer must do the rest.
        builder.CreateUmbracoBuilder().AddBackOffice().AddWebsite().AddComposers().Build();
        builder.Services.Replace(ServiceDescriptor.Singleton<IServerRoleAccessor, SingleServerRoleAccessor>());
        await using var app = builder.Build();
        var printSubmissions = 0;
        try
        {
            await app.BootUmbracoAsync();
            app.UseUmbraco().WithMiddleware(u => { u.UseBackOffice(); u.UseWebsite(); })
                .WithEndpoints(u => { u.UseBackOfficeEndpoints(); u.UseWebsiteEndpoints(); });
            app.MapGet("/test-pressroom/v1/printers", (HttpContext http) =>
                http.Request.Headers["X-Api-Key"] == "test-printer-key" ? Results.Json(new { printers = new[] { "office", "reception" } }) : Results.Unauthorized());
            app.MapPost("/test-pressroom/v1/render", (HttpContext http) => {
                printSubmissions++;
                return Results.Json(new { jobId = "test-job", printer = "office", status = "submitted", pageCount = 1 }, statusCode: 202);
            });
            await app.StartAsync();
            var upgrade = app.Services.GetServices<IHostedService>().OfType<BackgroundService>()
                .Single(service => service.GetType().Name == "UnattendedUpgradeBackgroundService");
            if (upgrade.ExecuteTask is { } task) await task.WaitAsync(TimeSpan.FromSeconds(90));
            var runtime = app.Services.GetRequiredService<IRuntimeState>();
            Assert.True(runtime.Level == RuntimeLevel.Run, runtime.BootFailedException?.ToString() ?? runtime.Level.ToString());
            var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var anonymous = new HttpClient { BaseAddress = address };
            using var admin = await Administrator(address, password);

            var settingsUrl = "/umbraco/management/api/v1/appblocks/documents/settings";
            var unauthorized = await anonymous.GetAsync(settingsUrl);
            Assert.False(unauthorized.IsSuccessStatusCode);
            var before = await admin.GetFromJsonAsync<PressroomConfigurationList>(settingsUrl);
            Assert.Single(before!.Configurations);
            using var saved = await admin.PostAsJsonAsync("/umbraco/management/api/v1/appblocks/documents/configurations",
                new SavePressroomConfiguration(null, "Another PDF", "https://another.example/", ApiKey: "secret-value"));
            var savedText = await saved.Content.ReadAsStringAsync();
            Assert.True(saved.IsSuccessStatusCode, savedText);
            Assert.DoesNotContain("secret-value", savedText);
            var current = await admin.GetFromJsonAsync<PressroomConfigurationList>(settingsUrl);
            Assert.Equal(2, current!.Configurations.Count);
            Assert.True(current.Configurations.Single(c => c.Name == "Another PDF").HasApiKey);
            foreach (var item in current.Configurations)
            {
                using var removed = await admin.DeleteAsync("/umbraco/management/api/v1/appblocks/documents/configurations/" + item.Id);
                Assert.True(removed.IsSuccessStatusCode);
            }
            Assert.Empty(app.Services.GetRequiredService<IDocumentOutputService>().GetDestinations());
            var pressroom = new Uri(address, "/test-pressroom/").ToString();
            using var discovery = await admin.PostAsJsonAsync("/umbraco/management/api/v1/appblocks/documents/printers",
                new DiscoverPressroomPrinters(null, pressroom, "test-printer-key"));
            Assert.True(discovery.IsSuccessStatusCode, await discovery.Content.ReadAsStringAsync());
            Assert.Equal(new[] { "office", "reception" }, (await discovery.Content.ReadFromJsonAsync<PressroomPrinterList>())!.Printers);
            using var invalidPrinter = await admin.PostAsJsonAsync("/umbraco/management/api/v1/appblocks/documents/configurations",
                new SavePressroomConfiguration(null, "Missing printer", pressroom, "printer:missing", "test-printer-key"));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidPrinter.StatusCode);
            using var savedPrinter = await admin.PostAsJsonAsync("/umbraco/management/api/v1/appblocks/documents/configurations",
                new SavePressroomConfiguration(null, "Office printer", pressroom, "printer:office", "test-printer-key"));
            Assert.True(savedPrinter.IsSuccessStatusCode, await savedPrinter.Content.ReadAsStringAsync());
            Assert.Equal(0, printSubmissions); // Discovery and configuration never print a document.
            var gateway = app.Services.GetRequiredService<IDocumentOutputService>();
            var destination = Assert.Single(gateway.GetDestinations());
            using var submitted = await gateway.SubmitAsync(destination.Id, new("<html>Test document</html>"));
            Assert.Equal("test-job", Assert.IsType<DocumentPrintResult>(submitted).JobId);
            Assert.Equal(1, printSubmissions);
            Assert.Contains("Documents", await admin.GetStringAsync("/umbraco/management/api/v1/appblocks/dashboard-pages"));
            Assert.Contains("Pressroom URL", await admin.GetStringAsync("/App_Plugins/AppBlocks.Documents/settings.js"));
            Assert.Contains("@page", await admin.GetStringAsync("/App_Plugins/AppBlocks.Documents/print.css"));

            Guid key;
            using (var scope = app.Services.CreateScope())
            {
                var services = scope.ServiceProvider;
                var types = services.GetRequiredService<IContentTypeService>();
                Assert.NotNull(types.Get("paperReport"));
                Assert.Null(types.Get("appReport")); // The Interactive Reports companion is not installed here.
                var connections = services.GetRequiredService<ConnectionCatalog>();
                await using var connection = connections.Openable("UmbracoDb");
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE DocumentTestRows (id INTEGER PRIMARY KEY, title TEXT); INSERT INTO DocumentTestRows VALUES (1, 'A standalone document');";
                await command.ExecuteNonQueryAsync();
                var templates = services.GetRequiredService<ITemplateService>();
                var view = await System.IO.File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Views", "DocumentTest.cshtml"));
                var created = await templates.CreateAsync("Document test", "DocumentTest", view, Constants.Security.SuperUserKey);
                Assert.True(created.Success, created.Result.ToString());
                var template = await templates.GetAsync("DocumentTest");
                var contents = services.GetRequiredService<IContentService>();
#pragma warning disable CS0618
                var sources = contents.Create("Data sources", -1, "dataSources");
                Save(sources);
                var source = contents.Create("Document rows", sources.Id, "sqlDataSource");
                source.SetValue("alias", "document.rows"); source.SetValue("connection", "UmbracoDb");
                source.SetValue("source", "DocumentTestRows"); source.SetValue("keyColumns", "id"); Save(source);
                var document = contents.Create("Document test", -1, "paperReport");
                document.TemplateId = template!.Id;
                document.SetValue("dataSource", source.Key.ToString()); Save(document);
                key = document.Key;
                void Save(IContent content)
                {
                    Assert.True(contents.Save(content).Success);
                    var published = contents.Publish(content, ["*"]);
                    Assert.True(published.Success, string.Join("; ", published.EventMessages?.GetAll().Select(m => m.Message) ?? []));
                }
#pragma warning restore CS0618
            }
            using var denied = await anonymous.GetAsync("/api/paper-reports/" + key);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            using var documentResponse = await admin.GetAsync("/api/paper-reports/" + key);
            var html = await documentResponse.Content.ReadAsStringAsync();
            Assert.True(documentResponse.IsSuccessStatusCode, html);
            Assert.Equal("text/html", documentResponse.Content.Headers.ContentType?.MediaType);
            Assert.Equal("inline", documentResponse.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Contains("<h1>A standalone document</h1>", html);
            Assert.Contains("@page", html);
            Assert.DoesNotContain("<link", html);
            Assert.True(documentResponse.Headers.CacheControl?.NoStore);
            Assert.Equal(1, printSubmissions); // Loading the template document still did not print.
        }
        finally { await app.StopAsync(); }
    }

    private static async Task<HttpClient> Administrator(Uri address, string password)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = address };
        const string security = "/umbraco/management/api/v1/security/back-office";
        using var login = await client.PostAsJsonAsync(security + "/login", new { username = "documents@example.test", password });
        Assert.True(login.IsSuccessStatusCode, "Test administrator sign-in failed: " + login.StatusCode);
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var redirect = new Uri(address, "/umbraco/oauth_complete").ToString();
        using var authorize = await client.GetAsync(QueryHelpers.AddQueryString(security + "/authorize", new Dictionary<string, string?> {
            ["client_id"] = "umbraco-back-office", ["response_type"] = "code", ["redirect_uri"] = redirect,
            ["code_challenge"] = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), ["code_challenge_method"] = "S256", ["state"] = "documents-tests",
        }));
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        var code = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["code"].ToString();
        using var token = await client.PostAsync(security + "/token", new FormUrlEncodedContent(new Dictionary<string, string> {
            ["grant_type"] = "authorization_code", ["client_id"] = "umbraco-back-office", ["redirect_uri"] = redirect, ["code"] = code, ["code_verifier"] = verifier,
        }));
        Assert.True(token.IsSuccessStatusCode, "Test management token request failed: " + token.StatusCode);
        using var response = JsonDocument.Parse(await token.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", response.RootElement.GetProperty("access_token").GetString());
        return client;
    }
}

[CollectionDefinition("Standalone Umbraco", DisableParallelization = true)]
public sealed class StandaloneUmbracoCollection;
