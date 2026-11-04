using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AppBlocks.Blocks;
using AppBlocks.Connections;
using AppBlocks.DataSources;
using AppBlocks.Forms;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Core.Sync;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Web.Common.Controllers;
using Umbraco.Extensions;

namespace AppBlocks.Tests;

/// <summary>
/// A plain Umbraco site with AppBlocks and nothing else: no host application, no uSync, no reports companion. Its orders live in the
/// application database, a C# class owns them and a data source reads and writes their lines. The pages have no templates;
/// <see cref="AppBlockPageController"/> answers Umbraco's own route with AppBlocks' real view component.
/// </summary>
internal sealed class BlockSite : IAsyncDisposable
{
    public static readonly Guid FormPageKey = AppBlockSchema.Key("block-site:orders-form");
    public static readonly Guid WebsitePageKey = AppBlockSchema.Key("block-site:website");
    private const string AdminEmail = "block.admin@example.test";
    private const string LoginRoute = "/__tests/login";

    private readonly WebApplication app;
    private readonly string password;
    private readonly string? ownedRoot;

    private BlockSite(WebApplication app, Uri address, string password, string? ownedRoot)
    {
        this.app = app;
        Address = address;
        this.password = password;
        this.ownedRoot = ownedRoot;
    }

    public Uri Address { get; }
    public string PathBase => "";
    public IServiceProvider Services => app.Services;

    /// <summary>The Orders form page's content, whose form's first area is its first layout item's first area.</summary>
    public JsonNode FormContent { get; } = BlockPages.OrdersForm();

    /// <summary>The Attachments block <see cref="FormContent"/> places in its form.</summary>
    public Guid AttachmentsBlock => Guid.Parse(BlockPages.Blocks(FormContent, AppBlockSchema.AttachmentsAlias).Single()["key"]!.GetValue<string>());

    /// <summary>The site with its orders and pages in place.</summary>
    public static async Task<BlockSite> StartAsync()
    {
        var site = await BootAsync();
        try { await site.PublishAsync(); }
        catch { await site.DisposeAsync(); throw; }
        return site;
    }

    /// <summary>The site up with nothing added to it, on a folder of its own or on one a previous site left, whatever its install did.</summary>
    public static async Task<BlockSite> BootAsync(string? root = null, Action<WebApplicationBuilder>? configure = null)
    {
        var owned = root is null;
        root ??= Directory.CreateTempSubdirectory("appblocks-block-site-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
        // Derived from the folder, so a later site on it can still sign in as this one's administrator.
        var password = "Block-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..24] + "!a";
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
            ApplicationName = typeof(BlockSite).Assembly.GetName().Name, ContentRootPath = root, EnvironmentName = "Development",
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["ConnectionStrings:umbracoDbDSN"] = $"Data Source={Path.Combine(root, "umbraco.db")};Pooling=False",
            ["ConnectionStrings:umbracoDbDSN_ProviderName"] = "Microsoft.Data.Sqlite",
            ["AppBlocks:DataRoot"] = root,
            ["Umbraco:CMS:Global:Id"] = Guid.NewGuid().ToString(), ["Umbraco:CMS:Global:UseHttps"] = "false",
            ["Umbraco:CMS:ModelsBuilder:ModelsMode"] = "Nothing",
            // As a site whose accounts have usernames of their own.
            ["Umbraco:CMS:Security:UsernameIsEmail"] = "false",
            ["Umbraco:CMS:TypeFinder:AdditionalAssemblyExclusionEntries:0"] = "xunit.",
            // As a site with the Oracle driver installed sets it, so that Umbraco's startup scan does not load the driver's optional cloud assemblies; see docs/connections.md.
            ["Umbraco:CMS:TypeFinder:AdditionalAssemblyExclusionEntries:1"] = "Oracle.ManagedDataAccess",
            ["Umbraco:CMS:Unattended:InstallUnattended"] = "true", ["Umbraco:CMS:Unattended:UpgradeUnattended"] = "true",
            ["Umbraco:CMS:Unattended:UnattendedUserName"] = "Block admin",
            ["Umbraco:CMS:Unattended:UnattendedUserEmail"] = AdminEmail,
            ["Umbraco:CMS:Unattended:UnattendedUserPassword"] = password,
        });
        builder.Services.Configure<InstallDefaultDataSettings>(Constants.Configuration.NamedOptions.InstallDefaultData.Languages,
            settings => { settings.InstallData = InstallDefaultDataOption.Values; settings.Values = new HashSet<string> { "en" }; });
        builder.CreateUmbracoBuilder().AddBackOffice().AddWebsite().AddComposers().Build();
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(Umbraco.Cms.Api.Management.Controllers.ManagementApiControllerBase).Assembly)
            .AddApplicationPart(typeof(Umbraco.Cms.Api.Common.Attributes.MapToApiAttribute).Assembly)
            .AddApplicationPart(typeof(RenderController).Assembly)
            .AddApplicationPart(typeof(BlockSite).Assembly);
        builder.Services.AddScoped<IFormResource, OrdersResource>();
        builder.Services.Replace(ServiceDescriptor.Singleton<IServerRoleAccessor, SingleServerRoleAccessor>());
        configure?.Invoke(builder);
        var app = builder.Build();
        await app.BootUmbracoAsync();
        app.UseUmbraco().WithMiddleware(u => { u.UseBackOffice(); u.UseWebsite(); })
            .WithEndpoints(u => { u.UseBackOfficeEndpoints(); u.UseWebsiteEndpoints(); });
        MapTestLogin(app);
        await app.StartAsync();
        var upgrade = app.Services.GetServices<IHostedService>().OfType<BackgroundService>()
            .Single(service => service.GetType().Name == "UnattendedUpgradeBackgroundService");
        if (upgrade.ExecuteTask is { } task) await task.WaitAsync(TimeSpan.FromSeconds(90));
        var runtime = app.Services.GetRequiredService<IRuntimeState>();
        Assert.True(runtime.Level == RuntimeLevel.Run, runtime.BootFailedException?.ToString() ?? runtime.Level.ToString());
        var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        return new BlockSite(app, address, password, owned ? root : null);
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (ownedRoot is not null) try { Directory.Delete(ownedRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public IServiceScope CreateScope() => app.Services.CreateScope();

    /// <summary>A browser stand-in with its own cookies that does not follow redirects.</summary>
    public HttpClient CreateClient(CookieContainer? cookies = null) => new(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = cookies ?? new() }) { BaseAddress = Address, Timeout = TimeSpan.FromSeconds(30) };

    public AttachmentsClient Attachments(HttpClient client, Guid page, Guid block) => new(client, PathBase, page, block);
    public TabularClient Tabular(HttpClient client, Guid page, Guid block) => new(client, PathBase, page, block);
    public Task<HttpResponseMessage> GetPageAsync(HttpClient client, string path) => client.GetAsync(PathBase + path);

    /// <summary>An open connection to the application database the orders live in.</summary>
    public DbConnection OpenDatabase()
    {
        var connection = app.Services.GetRequiredService<ConnectionCatalog>().Openable(ConnectionCatalog.ApplicationConnection);
        connection.Open();
        return connection;
    }

    public async Task WaitForPublishedAsync(Guid key)
    {
        using var reference = app.Services.GetRequiredService<IUmbracoContextFactory>().EnsureUmbracoContext();
        for (var i = 0; i < 100 && reference.UmbracoContext.Content?.GetById(false, key) is null; i++) await Task.Delay(100);
        Assert.NotNull(reference.UmbracoContext.Content?.GetById(false, key));
    }

    /// <summary>Tables, their rows, the data source over the lines, and the two pages. Separate from starting, for a site whose schema is what a test is about.</summary>
    public async Task PublishAsync()
    {
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var types = services.GetRequiredService<IContentTypeService>();
        for (var i = 0; i < 100 && types.Get(DataSourceSchema.SqlAlias) is null; i++) await Task.Delay(100);
        await using (var connection = OpenDatabase())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE ORDERS (ORDER_ID INTEGER PRIMARY KEY, CUSTOMER TEXT NOT NULL);
                CREATE TABLE ORDER_LINES (
                    LINE_ID INTEGER PRIMARY KEY, ORDER_ID INTEGER NOT NULL REFERENCES ORDERS (ORDER_ID), ITEM TEXT NOT NULL, QUANTITY INTEGER NOT NULL DEFAULT 1,
                    UNIT_PRICE NUMERIC NOT NULL DEFAULT 0, NOTES TEXT NULL, VERSION INTEGER NOT NULL DEFAULT 1,
                    CREATED_BY TEXT NULL, CREATED_AT TEXT NULL, UPDATED_BY TEXT NULL, UPDATED_AT TEXT NULL);
                INSERT INTO ORDERS (ORDER_ID, CUSTOMER) VALUES (4, 'Acme Corp'), (5, 'Globex');
                INSERT INTO ORDER_LINES (ORDER_ID, ITEM, QUANTITY, UNIT_PRICE) VALUES (4, 'Bearing', 2, 12.50), (4, 'Sprocket', 1, 3.25), (5, 'Chain', 4, 9.00), (5, 'Oil', 1, 2.00);
                """;
            await command.ExecuteNonQueryAsync();
        }
        var pageType = new ContentType(services.GetRequiredService<IShortStringHelper>(), -1) { Key = AppBlockSchema.Key("block-site:type"), Alias = AppBlockSchema.PageAlias, Name = "Block page", AllowedAsRoot = true };
        var grid = await services.GetRequiredService<IDataTypeService>().GetAsync(Guid.Parse("97b36051-4abe-e750-6667-9cb7ee6a9a99"));
        pageType.AddPropertyType(new PropertyType(services.GetRequiredService<IShortStringHelper>(), grid!) { Alias = AppBlockSchema.BodyAlias, Name = "Blocks" });
        var typed = await types.CreateAsync(pageType, Constants.Security.SuperUserKey);
        Assert.True(typed.Success, typed.Result.ToString());
        var contents = services.GetRequiredService<IContentService>();
#pragma warning disable CS0618
        void Publish(IContent content)
        {
            Assert.True(contents.Save(content).Success);
            var published = contents.Publish(content, ["*"]);
            Assert.True(published.Success, content.Name + ": " + string.Join("; ", published.EventMessages?.GetAll().Select(m => m.Message) ?? []));
        }
        var sources = contents.Create("Data sources", -1, DataSourceSchema.RootAlias);
        Publish(sources);
        var lines = contents.Create("Orders lines", sources.Id, DataSourceSchema.SqlAlias);
        lines.SetValue("access", "Public");
        lines.SetValue("alias", "orders.lines"); lines.SetValue("connection", ConnectionCatalog.ApplicationConnection); lines.SetValue("source", "ORDER_LINES"); lines.SetValue("keyColumns", "LINE_ID");
        lines.SetValue("allowInsert", 1); lines.SetValue("allowUpdate", 1); lines.SetValue("allowDelete", 1); lines.SetValue("concurrency", "VERSION");
        lines.SetValue("auditColumns", "createdBy = CREATED_BY\ncreatedAt = CREATED_AT\nupdatedBy = UPDATED_BY\nupdatedAt = UPDATED_AT");
        lines.SetValue("insertDefaults", "QUANTITY = 1\nUNIT_PRICE = 0");
        lines.SetValue("mapping", """{ "item": { "maxLength": 200 }, "quantity": { "min": 1, "max": 9999 }, "unitPrice": { "min": 0, "max": 999999.99 }, "notes": { "maxLength": 1000 }, "createdAt": { "kind": "datetime" }, "updatedAt": { "kind": "datetime" } }""");
        Publish(lines);
        IContent Page(string name, Guid key, int parent, JsonNode? blocks)
        {
            var page = contents.Create(name, parent, AppBlockSchema.PageAlias);
            page.Key = key;
            if (blocks is not null) page.SetValue(AppBlockSchema.BodyAlias, blocks.ToJsonString());
            Publish(page);
            return page;
        }
        var home = Page("Home", AppBlockSchema.Key("block-site:home"), -1, null);
        Page("Orders form", FormPageKey, home.Id, FormContent);
        Page("Website", WebsitePageKey, home.Id, BlockPages.OrdersForm());
#pragma warning restore CS0618
    }

    public IMember CreateMember(string username, string email, string name)
    {
        using var scope = CreateScope();
        var members = scope.ServiceProvider.GetRequiredService<IMemberService>();
#pragma warning disable CS0618
        var member = members.CreateMemberWithIdentity(username, email, name, "Member", isApproved: true);
        members.Save(member);
#pragma warning restore CS0618
        return member;
    }

    /// <summary>A real HTTP client with a real member cookie, from the site's test-only route.</summary>
    public async Task<HttpClient> SignInMemberAsync(IMember member)
    {
        var client = CreateClient();
        using var login = await client.GetAsync($"{LoginRoute}/{member.Id}?name={Uri.EscapeDataString(member.Name ?? member.Username)}");
        login.EnsureSuccessStatusCode();
        return client;
    }

    private static void MapTestLogin(WebApplication app) => app.MapGet(LoginRoute + "/{id}", async (string id, string name, HttpContext http) =>
    {
        await http.SignInAsync(IdentityConstants.ApplicationScheme, new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, id), new(ClaimTypes.Name, name)], IdentityConstants.ApplicationScheme)));
        return Results.Ok();
    });

    public async Task<IUser> CreateUserAsync(string username, string email, string name)
    {
        using var scope = CreateScope();
        var created = await scope.ServiceProvider.GetRequiredService<IUserService>().CreateAsync(Constants.Security.SuperUserKey, new UserCreateModel {
            UserName = username, Email = email, Name = name, Kind = UserKind.Default, UserGroupKeys = new HashSet<Guid> { Constants.Security.EditorGroupKey },
        }, approveUser: true);
        Assert.True(created.Success, created.Status.ToString());
        return created.Result.CreatedUser!;
    }

    public async Task SetPasswordAsync(IUser user, string newPassword)
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IBackOfficeUserManager>();
        var identity = (await manager.FindByIdAsync(user.Key.ToString()))!;
        var changed = await manager.ChangePasswordWithResetAsync(identity.Id, await manager.GeneratePasswordResetTokenAsync(identity), newPassword);
        Assert.True(changed.Succeeded, string.Join("; ", changed.Errors.Select(error => error.Description)));
    }

    public Task<HttpClient> SignInAsAdministratorAsync(CookieContainer? cookies = null) => SignInManagementApiAsync(AdminEmail, password, cookies);

    /// <summary>Signs in as the backoffice does: the password login, then the PKCE code flow of the backoffice's client for the management API's token.</summary>
    /// <param name="cookies">Receives the backoffice cookies the token relies on, for a caller that hands them on.</param>
    public async Task<HttpClient> SignInManagementApiAsync(string username, string userPassword, CookieContainer? cookies = null)
    {
        var client = CreateClient(cookies);
        const string security = "/umbraco/management/api/v1/security/back-office";
        using var login = await client.PostAsJsonAsync(security + "/login", new { username, password = userPassword });
        Assert.True(login.IsSuccessStatusCode, "Test sign-in failed: " + login.StatusCode);
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var redirect = new Uri(Address, "/umbraco/oauth_complete").ToString();
        using var authorize = await client.GetAsync(QueryHelpers.AddQueryString(security + "/authorize", new Dictionary<string, string?> {
            ["client_id"] = "umbraco-back-office", ["response_type"] = "code", ["redirect_uri"] = redirect,
            ["code_challenge"] = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), ["code_challenge_method"] = "S256", ["state"] = "block-site",
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

/// <summary>
/// The orders, readable and writable by a signed-in Member: what a site's own class would be, with only what these tests look at. It
/// keeps the customer, and a customer of "Refused" is refused, as a class's own rule would.
/// </summary>
internal sealed class OrdersResource(ConnectionCatalog connections) : IFormResource
{
    private static FormChoice[] Choices(params string[] values) => values.Select(v => new FormChoice(v, v)).ToArray();

    public string Alias => SampleOrdersResource.ResourceAlias;
    public string ContractVersion => "1";
    public IReadOnlyList<FormField> Fields { get; } = [
        new("customer", "Customer", "text", Required: true, MaxLength: 200),
        new("region", "Region", "text", Required: true, Choices: Choices("NORTH", "SOUTH", "EAST", "WEST")),
        new("status", "Status", "text", Required: true, Choices: Choices("NEW", "PENDING", "SHIPPED", "CANCELLED")),
        new("amount", "Amount", "number", Required: true, Minimum: 0, Maximum: 999999999),
        new("orderDate", "Order date", "date", Required: true),
        new("notes", "Notes", "text", MaxLength: 4000),
    ];

    public async Task<FormResult> LoadAsync(string key, FormExecutionContext context, CancellationToken ct)
    {
        if (context.Member.Identity?.AuthenticationType != IdentityConstants.ApplicationScheme) return FormResult.Denied();
        if (!long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return FormResult.Missing();
        await using var connection = connections.Openable(ConnectionCatalog.ApplicationConnection);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT CUSTOMER FROM ORDERS WHERE ORDER_ID = {id}";
        return await command.ExecuteScalarAsync(ct) is string customer
            ? FormResult.Ok(new(key, "1", new Dictionary<string, object?> { ["customer"] = customer }, new(Update: true))) : FormResult.Missing();
    }

    public Task<FormResult> InitializeAsync(FormExecutionContext context, CancellationToken ct) => Task.FromResult(
        context.Member.Identity?.AuthenticationType != IdentityConstants.ApplicationScheme ? FormResult.Denied()
        // A test asks for a resource that cannot start a record right now.
        : context.Parameters?.GetValueOrDefault("unavailable") == "1" ? new FormResult(503, Errors: new Dictionary<string, string[]> { [""] = ["Orders are offline."] })
        : FormResult.Ok(new(null, null, new Dictionary<string, object?> { ["customer"] = "" }, new(Create: true))));
    public async Task<FormResult> CreateAsync(JsonElement input, FormExecutionContext context, CancellationToken ct)
    {
        if (context.Member.Identity?.AuthenticationType != IdentityConstants.ApplicationScheme) return FormResult.Denied();
        if (Customer(input) is not { } customer) return FormResult.Invalid("customer", "Enter a customer.");
        await using var connection = connections.Openable(ConnectionCatalog.ApplicationConnection);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO ORDERS (CUSTOMER) VALUES ($customer) RETURNING ORDER_ID";
        var parameter = command.CreateParameter(); parameter.ParameterName = "$customer"; parameter.Value = customer; command.Parameters.Add(parameter);
        var id = Convert.ToString(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture)!;
        return await LoadAsync(id, context, ct);
    }

    public async Task<FormResult> UpdateAsync(string key, JsonElement input, string version, FormExecutionContext context, CancellationToken ct)
    {
        if (context.Member.Identity?.AuthenticationType != IdentityConstants.ApplicationScheme) return FormResult.Denied();
        if (!long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return FormResult.Missing();
        if (Customer(input) is not { } customer) return FormResult.Invalid("customer", "Enter a customer.");
        await using var connection = connections.Openable(ConnectionCatalog.ApplicationConnection);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE ORDERS SET CUSTOMER = $customer WHERE ORDER_ID = {id}";
        var parameter = command.CreateParameter(); parameter.ParameterName = "$customer"; parameter.Value = customer; command.Parameters.Add(parameter);
        return await command.ExecuteNonQueryAsync(ct) == 1 ? await LoadAsync(key, context, ct) : FormResult.Missing();
    }

    private static string? Customer(JsonElement input)
        => input.TryGetProperty("customer", out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } customer && customer != "Refused" ? customer : null;
    public Task<FormResult> DeleteAsync(string key, string version, FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Denied());
}

/// <summary>Answers Umbraco's route to a block page with AppBlocks' view component, which a site's own template would wrap.</summary>
public sealed class AppBlockPageController(ILogger<RenderController> logger, ICompositeViewEngine engine, IUmbracoContextAccessor contexts) : RenderController(logger, engine, contexts)
{
    public override IActionResult Index() => new ViewComponentResult { ViewComponentName = "WebsiteBlocks", Arguments = new { page = CurrentPage } };
}

/// <summary>What the clients below throw for a call they will not make, as the host's test clients do.</summary>
internal sealed class TestSiteException(string message) : Exception(message);

internal static class Http
{
    private static readonly JsonElement Empty = Parse("{}");

    public static string WithQuery(string path, string? query)
        => string.IsNullOrEmpty(query) ? path : path + (path.Contains('?') ? "&" : "?") + query.TrimStart('?', '&');

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(text)) return Empty;
        try { return Parse(text); }
        catch (JsonException)
        {
            throw new TestSiteException($"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri} returned {(int)response.StatusCode} {response.StatusCode} with a body that is not JSON: {text}");
        }
    }

    public static string? Text(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) ? value.ValueKind switch {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.GetRawText(),
        } : null;

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

/// <summary>The file an upload or a replacement sends: its name, its content type and its bytes.</summary>
internal sealed record AttachmentFile(string FileName, string ContentType, byte[] Content);

/// <summary>Drives <c>/api/attachments</c> as the Attachments block's runtime does: the list's antiforgery token goes on every write, and the page's parameters travel as a query string.</summary>
internal sealed class AttachmentsClient(HttpClient client, string pathBase, Guid pageKey, Guid blockKey)
{
    private readonly string api = $"{pathBase}/api/attachments/{pageKey}/{blockKey}";
    private string? antiforgeryToken;

    public async Task<AttachmentsResponse> ListAsync(string? query = null)
    {
        var listed = await SendAsync(new HttpRequestMessage(HttpMethod.Get, Http.WithQuery(api, query)));
        if (listed.IsSuccess) antiforgeryToken = Http.Text(listed.Body, "antiforgeryToken");
        return listed;
    }

    public Task<AttachmentsResponse> UploadAsync(AttachmentFile file, string? purpose = null, string? label = null, string? notes = null, string? query = null)
        => WriteAsync(HttpMethod.Post, api, query, file, purpose, label, notes);

    public Task<AttachmentsResponse> ReplaceAsync(long id, AttachmentFile? file = null, string? purpose = null, string? label = null, string? notes = null, string? query = null)
        => WriteAsync(HttpMethod.Put, Item(id), query, file, purpose, label, notes);

    public Task<HttpResponseMessage> DownloadAsync(long id, string? query = null) => client.GetAsync(Http.WithQuery(Item(id), query));

    public async Task<HttpResponseMessage> RemoveAsync(long id, string? query = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, Http.WithQuery(Item(id), query));
        request.Headers.Add("RequestVerificationToken", Token());
        return await client.SendAsync(request);
    }

    private string Item(long id) => $"{api}/{id}";

    private string Token() => antiforgeryToken
        ?? throw new TestSiteException($"List the attachments at {api} before writing to them: a write carries the antiforgery token the list returns.");

    private Task<AttachmentsResponse> WriteAsync(HttpMethod method, string path, string? query, AttachmentFile? file, string? purpose, string? label, string? notes)
    {
        var body = new MultipartFormDataContent();
        if (file is not null)
        {
            var bytes = new ByteArrayContent(file.Content);
            bytes.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType);
            body.Add(bytes, "file", file.FileName);
        }
        foreach (var (name, value) in new[] { ("purpose", purpose), ("label", label), ("notes", notes) })
            if (value is not null) body.Add(new StringContent(value), name);
        var request = new HttpRequestMessage(method, Http.WithQuery(path, query)) { Content = body };
        request.Headers.Add("RequestVerificationToken", Token());
        return SendAsync(request);
    }

    private async Task<AttachmentsResponse> SendAsync(HttpRequestMessage request)
    {
        using (request)
        {
            var response = await client.SendAsync(request);
            return new AttachmentsResponse(response, await Http.ReadJsonAsync(response));
        }
    }
}

internal sealed record AttachmentsResponse(HttpResponseMessage Response, JsonElement Body)
{
    public bool IsSuccess => Response.IsSuccessStatusCode;
    public IReadOnlyList<JsonElement> Items
        => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray().ToArray() : [];
    public bool CanWrite => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("canWrite", out var value) && value.ValueKind == JsonValueKind.True;
    public long? Id => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("id", out var id) && id.TryGetInt64(out var value) ? value : null;
    public string? Problem => Http.Text(Body, "detail") ?? Http.Text(Body, "title");
}

/// <summary>One change of a batch: <c>create</c> with values, <c>update</c> with the key, the version and the changed values, or <c>delete</c> with the key and the version.</summary>
internal sealed record TabularChange(string Operation, string? Key = null, string? Version = null, object? Values = null);

/// <summary>Drives <c>/api/tabular</c> as the Tabular form block's runtime does: the list's antiforgery token goes on every save, and the page's parameters travel as a query string.</summary>
internal sealed class TabularClient(HttpClient client, string pathBase, Guid pageKey, Guid blockKey)
{
    private readonly string api = $"{pathBase}/api/tabular/{pageKey}/{blockKey}";
    private string? antiforgeryToken;

    public async Task<TabularResponse> ListAsync(string? query = null)
    {
        var listed = await SendAsync(new HttpRequestMessage(HttpMethod.Get, Http.WithQuery(api, query)));
        if (listed.IsSuccess) antiforgeryToken = Http.Text(listed.Body, "antiforgeryToken");
        return listed;
    }

    public Task<TabularResponse> SaveAsync(IEnumerable<TabularChange> changes, string? query = null, string? submissionKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Http.WithQuery(api, query))
        {
            Content = JsonContent.Create(new {
                submissionKey = submissionKey ?? Guid.NewGuid().ToString(),
                changes = changes.Select(change => new { operation = change.Operation, key = change.Key, version = change.Version, values = change.Values }).ToArray(),
            }),
        };
        request.Headers.Add("RequestVerificationToken", antiforgeryToken
            ?? throw new TestSiteException($"List the rows at {api} before saving them: a save carries the antiforgery token the list returns."));
        return SendAsync(request);
    }

    private async Task<TabularResponse> SendAsync(HttpRequestMessage request)
    {
        using (request)
        {
            var response = await client.SendAsync(request);
            return new TabularResponse(response, await Http.ReadJsonAsync(response));
        }
    }
}

internal sealed record TabularResponse(HttpResponseMessage Response, JsonElement Body)
{
    public bool IsSuccess => Response.IsSuccessStatusCode;
    public IReadOnlyList<JsonElement> Rows
        => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array ? rows.EnumerateArray().ToArray() : [];
    public IReadOnlyList<JsonElement> Fields
        => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array ? fields.EnumerateArray().ToArray() : [];
    public bool CanAdd => Flag("canAdd");
    public bool CanUpdate => Flag("canUpdate");
    public bool CanDelete => Flag("canDelete");
    public int? Index => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("index", out var index) && index.TryGetInt32(out var value) ? value : null;
    public IReadOnlyDictionary<string, string[]> Errors
        => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object
            ? errors.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(e => e.GetString() ?? "").ToArray())
            : new Dictionary<string, string[]>();
    public string? Problem => Http.Text(Body, "detail") ?? Http.Text(Body, "title");
    private bool Flag(string name) => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
