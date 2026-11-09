using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using AppBlocks.AntiSpam;
using AppBlocks.Blocks;
using AppBlocks.Forms;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Core.Sync;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Web.Common.Controllers;

namespace AppBlocks.Tests;

/// <summary>
/// A plain Umbraco site with AppBlocks and nothing else, whose forms carry Anti-spam blocks. The class behind them records
/// every call. The verification services are stand-ins: <see cref="StandInService"/> answers by its token, and the real
/// adapters reach <see cref="RecordingVerifier"/> rather than the network, unless <paramref name="realKeys"/> says otherwise.
/// </summary>
internal sealed class AntiSpamSite : IAsyncDisposable
{
    public const string StandInSecret = "standin-secret-from-configuration-7Hq2";
    /// <summary>Real enough for publishing to recognize, and typed into a draft by the test, so it is not among the secrets searched for.</summary>
    public const string PastedSecret = "recaptcha-three-secret-pasted-by-mistake";
    public const string SavedSecret = "turnstile-secret-saved-on-the-screen-9Xk4";
    public const string AdminEmail = "antispam.admin@example.test";

    public static readonly Guid StandInPage = Key("standin"), StandInForm = Key("standin-form");
    public static readonly Guid HoneypotPage = Key("honeypot"), HoneypotForm = Key("honeypot-form"), HoneypotBlock = Key("honeypot-block");
    public static readonly Guid PlainPage = Key("plain"), PlainForm = Key("plain-form");
    public static readonly Guid TurnstilePage = Key("turnstile"), TurnstileForm = Key("turnstile-form");
    public static readonly Guid MissingPage = Key("missing"), MissingForm = Key("missing-form");
    public static readonly Guid WidgetsPage = Key("widgets"), TurnstileOne = Key("w-turnstile-1"), TurnstileTwo = Key("w-turnstile-2"), RecaptchaTwo = Key("w-recaptcha-2"), RecaptchaThree = Key("w-recaptcha-3"), HCaptchaOne = Key("w-hcaptcha"), ViewOnlyForm = Key("w-viewonly");

    private readonly WebApplication app;
    private readonly string password;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<HttpClient, CookieContainer> jars = [];

    private AntiSpamSite(WebApplication app, Uri address, string password, string root)
    {
        (this.app, Address, this.password, Root) = (app, address, password, root);
    }

    public Uri Address { get; }
    public string Root { get; }
    public ContactCalls Calls => app.Services.GetRequiredService<ContactCalls>();
    public RecordingVerifier Verifier => app.Services.GetRequiredService<RecordingVerifier>();
    public StandInService StandIn => app.Services.GetServices<IAntiSpamService>().OfType<StandInService>().Single();
    public CapturedLog Log => app.Services.GetRequiredService<CapturedLog>();

    /// <summary>Every answer any client of this site was given, to search for what must never leave it.</summary>
    public ConcurrentQueue<string> Answers { get; } = new();

    /// <summary>What publishing said about the page that uses a service with no secret key, and about the pages it refuses.</summary>
    public List<string> PublishMessages { get; } = [];
    public List<string> Refused { get; } = [];

    private static Guid Key(string name) => AppBlockSchema.Key("antispam-site:" + name);
    public static string FormUrl(Guid page, Guid form) => $"/api/forms/{page}/{form}";
    public static string HoneypotField => new AntiSpamBlock(HoneypotBlock, AntiSpamBlock.Honeypot, null, 0, null, null).HoneypotField;

    public static async Task<AntiSpamSite> StartAsync(bool realKeys = false)
    {
        var root = Directory.CreateTempSubdirectory("appblocks-antispam-site-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
        var password = "Spam-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)) + "!a";
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
            ApplicationName = typeof(AntiSpamSite).Assembly.GetName().Name, ContentRootPath = root, EnvironmentName = "Development",
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var settings = new Dictionary<string, string?> {
            ["ConnectionStrings:umbracoDbDSN"] = $"Data Source={Path.Combine(root, "umbraco.db")};Pooling=False",
            ["ConnectionStrings:umbracoDbDSN_ProviderName"] = "Microsoft.Data.Sqlite",
            ["AppBlocks:DataRoot"] = root,
            ["AppBlocks:AntiSpam:standin:SecretKey"] = StandInSecret,
            ["AppBlocks:AntiSpam:recaptchaV3:SecretKey"] = PastedSecret,
            ["Umbraco:CMS:Global:Id"] = Guid.NewGuid().ToString(), ["Umbraco:CMS:Global:UseHttps"] = "false",
            ["Umbraco:CMS:ModelsBuilder:ModelsMode"] = "Nothing",
            ["Umbraco:CMS:TypeFinder:AdditionalAssemblyExclusionEntries:0"] = "xunit.",
            // As a site with the Oracle driver installed sets it, so that Umbraco's startup scan does not load the driver's optional cloud assemblies; see docs/connections.md.
            ["Umbraco:CMS:TypeFinder:AdditionalAssemblyExclusionEntries:1"] = "Oracle.ManagedDataAccess",
            ["Umbraco:CMS:Unattended:InstallUnattended"] = "true", ["Umbraco:CMS:Unattended:UpgradeUnattended"] = "true",
            ["Umbraco:CMS:Unattended:UnattendedUserName"] = "Anti-spam admin",
            ["Umbraco:CMS:Unattended:UnattendedUserEmail"] = AdminEmail,
            ["Umbraco:CMS:Unattended:UnattendedUserPassword"] = password,
        };
        if (realKeys) foreach (var (service, (_, secret, _)) in AntiSpamRealKeys.Passing) settings[$"AppBlocks:AntiSpam:{service}:SecretKey"] = secret;
        // The published test keys answer with a host name of their own, never this one.
        if (realKeys) settings["AppBlocks:AntiSpam:VerifyHostname"] = "false";
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.Configure<InstallDefaultDataSettings>(Constants.Configuration.NamedOptions.InstallDefaultData.Languages,
            options => { options.InstallData = InstallDefaultDataOption.Values; options.Values = new HashSet<string> { "en" }; });
        builder.CreateUmbracoBuilder().AddBackOffice().AddWebsite().AddComposers().Build();
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(Umbraco.Cms.Api.Management.Controllers.ManagementApiControllerBase).Assembly)
            .AddApplicationPart(typeof(Umbraco.Cms.Api.Common.Attributes.MapToApiAttribute).Assembly)
            .AddApplicationPart(typeof(RenderController).Assembly)
            .AddApplicationPart(typeof(AntiSpamSite).Assembly);
        var log = new CapturedLog();
        builder.Services.AddSingleton(log);
        // Umbraco's own logger factory ignores added providers, so this one replaces it, and sees every level of every category.
        builder.Services.RemoveAll<ILoggerFactory>();
        builder.Services.AddSingleton(LoggerFactory.Create(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(log)));
        builder.Services.AddSingleton<ContactCalls>();
        builder.Services.AddScoped<IFormResource, RecordingContact>();
        builder.Services.AddSingleton<IAntiSpamService, StandInService>();
        var verifier = new RecordingVerifier();
        builder.Services.AddSingleton(verifier);
        if (!realKeys) builder.Services.AddHttpClient(SiteVerifyService.ClientName).ConfigurePrimaryHttpMessageHandler(() => verifier);
        builder.Services.Replace(ServiceDescriptor.Singleton<IServerRoleAccessor, SingleServerRoleAccessor>());
        var app = builder.Build();
        await app.BootUmbracoAsync();
        app.UseUmbraco().WithMiddleware(u => { u.UseBackOffice(); u.UseWebsite(); })
            .WithEndpoints(u => { u.UseBackOfficeEndpoints(); u.UseWebsiteEndpoints(); });
        await app.StartAsync();
        var upgrade = app.Services.GetServices<IHostedService>().OfType<BackgroundService>()
            .Single(service => service.GetType().Name == "UnattendedUpgradeBackgroundService");
        if (upgrade.ExecuteTask is { } task) await task.WaitAsync(TimeSpan.FromSeconds(90));
        var runtime = app.Services.GetRequiredService<IRuntimeState>();
        Assert.True(runtime.Level == RuntimeLevel.Run, runtime.BootFailedException?.ToString() ?? runtime.Level.ToString());
        var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        var site = new AntiSpamSite(app, address, password, root);
        await site.PublishAsync(realKeys);
        return site;
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task PublishAsync(bool realKeys)
    {
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var types = services.GetRequiredService<IContentTypeService>();
        for (var i = 0; i < 100 && (types.Get(AppBlockSchema.FormsContainerAlias) is null || types.Get(AppBlockSchema.AntiSpamAlias) is null); i++) await Task.Delay(100);
        var strings = services.GetRequiredService<IShortStringHelper>();
        var dataTypes = services.GetRequiredService<IDataTypeService>();
        var list = new DataType(services.GetRequiredService<DataEditorCollection>().First(e => e.Alias == "Umbraco.BlockList"), services.GetRequiredService<IConfigurationEditorJsonSerializer>()) {
            Key = Key("list"), Name = "Anti-spam page blocks", EditorUiAlias = "Umb.PropertyEditorUi.BlockList",
        };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var configuration = new BlockListConfiguration { Blocks = [new BlockListConfiguration.BlockConfiguration { ContentElementTypeKey = AppBlockSchema.Key(AppBlockSchema.FormsContainerAlias) }] };
        list.SetConfigurationData(JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(configuration, options), options)!);
        var created = await dataTypes.CreateAsync(list, Constants.Security.SuperUserKey);
        Assert.True(created.Success, created.Status.ToString());
        var pageType = new ContentType(strings, -1) { Key = Key("type"), Alias = "antiSpamPage", Name = "Anti-spam page", AllowedAsRoot = true };
        pageType.AddPropertyType(new PropertyType(strings, list) { Alias = "content", Name = "Content" });
        var typed = await types.CreateAsync(pageType, Constants.Security.SuperUserKey);
        Assert.True(typed.Success, typed.Result.ToString());
        var contents = services.GetRequiredService<IContentService>();
#pragma warning disable CS0618
        IContent Page(string name, Guid key, int parent, JsonObject? blocks = null)
        {
            var page = contents.Create(name, parent, "antiSpamPage");
            page.Key = key;
            if (blocks is not null) page.SetValue("content", blocks.ToJsonString());
            Assert.True(contents.Save(page).Success);
            var published = contents.Publish(page, ["*"]);
            Assert.True(published.Success, name + ": " + string.Join("; ", published.EventMessages?.GetAll().Select(m => m.Message) ?? []));
            PublishMessages.AddRange(published.EventMessages?.GetAll().Select(m => m.Message) ?? []);
            return page;
        }
        void Refuse(string name, JsonObject blocks)
        {
            var page = contents.Create(name, -1, "antiSpamPage");
            page.SetValue("content", blocks.ToJsonString());
            Assert.True(contents.Save(page).Success);
            var published = contents.Publish(page, ["*"]);
            Assert.False(published.Success, name + " should have been refused.");
            Refused.AddRange(published.EventMessages?.GetAll().Select(m => m.Message) ?? []);
        }
        var home = Page("Home", Key("home"), -1);
        Page("Standin", StandInPage, home.Id, Container(FormBlock(StandInForm, Field("name", "Your name"), Field("message", "Message"), Spam(Key("standin-block"), "standin", ("siteKey", "standin-site-key"), ("theme", "dark")))));
        Page("Honeypot", HoneypotPage, home.Id, Container(FormBlock(HoneypotForm, Field("name", "Your name"), Spam(HoneypotBlock, AntiSpamBlock.Honeypot))));
        Page("Plain", PlainPage, home.Id, Container(FormBlock(PlainForm, Field("name", "Your name"), Field("message", "Message"))));
        Page("Turnstile", TurnstilePage, home.Id, Container(FormBlock(TurnstileForm, Field("name", "Your name"), Spam(Key("turnstile-block"), "turnstile", ("siteKey", "0xTurnstileSiteKey")))));
        Page("Missing", MissingPage, home.Id, Container(FormBlock(MissingForm, Field("name", "Your name"), Spam(Key("missing-block"), "hcaptcha", ("siteKey", "missing-secret-site-key")))));
        Page("Widgets", WidgetsPage, home.Id, Container(
            FormBlock(TurnstileOne, Field("name", "Your name"), Spam(Key("w1"), "turnstile", ("siteKey", "0xTurnstileOne"), ("size", "compact"))),
            FormBlock(TurnstileTwo, Field("name", "Your name"), Spam(Key("w2"), "turnstile", ("siteKey", "0xTurnstileTwo"))),
            FormBlock(RecaptchaTwo, Field("name", "Your name"), Spam(Key("w3"), "recaptchaV2", ("siteKey", "recaptcha-two-key"), ("theme", "dark"))),
            FormBlock(HCaptchaOne, Field("name", "Your name"), Spam(Key("w5"), "hcaptcha", ("siteKey", "hcaptcha-key")))));
        Page("Threes", Key("threes"), home.Id, Container(FormBlock(RecaptchaThree, Field("name", "Your name"), Spam(Key("w4"), "recaptchaV3", ("siteKey", "recaptcha-three-key"), ("scoreThreshold", "0.7")))));
        Page("Reading", Key("reading"), home.Id, Container(FormBlock(ViewOnlyForm, true, Field("name", "Your name"), Spam(Key("w6"), "turnstile", ("siteKey", "0xViewOnly")))));
        if (realKeys)
            foreach (var (service, (siteKey, _, _)) in AntiSpamRealKeys.Passing)
                Page("Real " + service, Key("real-" + service), home.Id, Container(FormBlock(Key("real-form-" + service), Field("name", "Your name"), Spam(Key("real-block-" + service), service, ("siteKey", siteKey)))));
        else
        {
            Refuse("Outside a form", Container(Spam(Key("outside"), AntiSpamBlock.Honeypot)));
            Refuse("Two checks", Container(FormBlock(Key("two"), Field("name", "Your name"), Spam(Key("two-1"), AntiSpamBlock.Honeypot), Spam(Key("two-2"), AntiSpamBlock.Honeypot))));
            Refuse("Unknown service", Container(FormBlock(Key("unknown"), Field("name", "Your name"), Spam(Key("unknown-1"), "nobody", ("siteKey", "x")))));
            Refuse("No site key", Container(FormBlock(Key("nokey"), Field("name", "Your name"), Spam(Key("nokey-1"), "turnstile"))));
            Refuse("Secret in a block", Container(FormBlock(Key("leak"), Field("name", "Your name"), Spam(Key("leak-1"), "standin", ("siteKey", PastedSecret)))));
            Refuse("Bad threshold", Container(FormBlock(Key("bad"), Field("name", "Your name"), Spam(Key("bad-1"), "recaptchaV3", ("siteKey", "x"), ("scoreThreshold", "7")))));
        }
#pragma warning restore CS0618
    }

    private sealed record Item(Guid Key, string Alias, (string Alias, object Value)[] Values, Item[] Children);

    private static Item Field(string field, string label) => new(AppBlockSchema.Key("antispam-site:field:" + Guid.NewGuid()), "appText", [("field", field), ("label", label)], []);
    private static Item Spam(Guid key, string service, params (string Alias, object Value)[] values) => new(key, AppBlockSchema.AntiSpamAlias, [("service", service), .. values], []);
    private static Item FormBlock(Guid key, params Item[] fields) => FormBlock(key, false, fields);
    private static Item FormBlock(Guid key, bool viewOnly, params Item[] fields)
    {
        var binding = Set("Umbraco.SingleBlock");
        Add(binding, "Umbraco.SingleBlock", AppBlockSchema.Key("antispam-site:interface:" + key), AppBlockSchema.Key("appFormInterface"), ("resource", JsonValue.Create(RecordingContact.ResourceAlias)), ("viewOnly", JsonValue.Create(viewOnly)));
        return new(key, "appForm", [("heading", "Form"), ("formInterface", binding)], fields);
    }

    /// <summary>A forms container holding these blocks at its root, and a form's blocks in the form's first area.</summary>
    private static JsonObject Container(params Item[] items)
    {
        using var stream = new ManifestEmbeddedFileProvider(typeof(AppBlocksExtensions).Assembly).GetFileInfo("Schema/DataTypes/ApplicationApplicationBlocks.config").CreateReadStream();
        var grid = JsonNode.Parse(XElement.Load(stream).Element("Config")!.Value)!;
        var formBlock = grid["blocks"]!.AsArray().Single(b => b!["contentElementTypeKey"]!.GetValue<string>() == AppBlockSchema.Key("appForm").ToString())!;
        var area = formBlock["areas"]![0]!["key"]!.GetValue<string>();
        var fields = Set("Umbraco.BlockGrid");
        var top = fields["layout"]!["Umbraco.BlockGrid"]!.AsArray();
        foreach (var item in items)
        {
            var layout = Add(fields, "Umbraco.BlockGrid", item.Key, AppBlockSchema.Key(item.Alias), Values(item));
            if (item.Children.Length == 0) continue;
            var children = new JsonArray();
            foreach (var child in item.Children)
            {
                var childLayout = Add(fields, "Umbraco.BlockGrid", child.Key, AppBlockSchema.Key(child.Alias), Values(child));
                top.Remove(childLayout);
                children.Add(childLayout);
            }
            layout["areas"]!.AsArray().Add(new JsonObject { ["key"] = area, ["items"] = children });
        }
        var body = Set("Umbraco.BlockList");
        Add(body, "Umbraco.BlockList", AppBlockSchema.Key("antispam-site:container:" + items[0].Key), AppBlockSchema.Key(AppBlockSchema.FormsContainerAlias), ("forms", JsonValue.Create(fields.ToJsonString())));
        return body;
    }

    private static (string, JsonNode?)[] Values(Item item) => item.Values.Select(v => (v.Alias, v.Value is JsonNode node ? node : (JsonNode?)JsonValue.Create((string)v.Value))).ToArray();
    private static JsonObject Set(string editor) => new() { ["layout"] = new JsonObject { [editor] = new JsonArray() }, ["contentData"] = new JsonArray(), ["settingsData"] = new JsonArray(), ["expose"] = new JsonArray() };

    private static JsonObject Add(JsonObject set, string editor, Guid key, Guid type, params (string Alias, JsonNode? Value)[] values)
    {
        set["contentData"]!.AsArray().Add(new JsonObject { ["key"] = key.ToString(), ["contentTypeKey"] = type.ToString(),
            ["values"] = new JsonArray(values.Select(v => (JsonNode)new JsonObject { ["alias"] = v.Alias, ["value"] = v.Value, ["culture"] = null, ["segment"] = null }).ToArray()) });
        set["expose"]!.AsArray().Add(new JsonObject { ["contentKey"] = key.ToString(), ["culture"] = null, ["segment"] = null });
        var layout = new JsonObject { ["contentKey"] = key.ToString(), ["settingsKey"] = null, ["columnSpan"] = 12, ["rowSpan"] = 1, ["areas"] = new JsonArray() };
        set["layout"]![editor]!.AsArray().Add(layout);
        return layout;
    }

    /// <summary>A real HTTP client with its own cookies and no account of any kind, whose answers are kept in <see cref="Answers"/>.</summary>
    public HttpClient Anonymous(params (string Name, string Value)[] cookies)
    {
        var inner = new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer() };
        foreach (var (name, value) in cookies) inner.CookieContainer.Add(Address, new Cookie(name, value));
        var client = new HttpClient(new RecordingAnswers(inner, Answers)) { BaseAddress = Address };
        jars.Add(client, inner.CookieContainer);
        return client;
    }

    public void SetCookie(HttpClient client, string name, string value)
    {
        Assert.True(jars.TryGetValue(client, out var jar));
        jar.Add(Address, new Cookie(name, value));
    }

    /// <summary>The backoffice administrator, with its sign-in cookie for the site's pages and a token for its management API.</summary>
    public async Task<HttpClient> Administrator(params (string Name, string Value)[] cookies)
    {
        var client = Anonymous(cookies);
        const string security = "/umbraco/management/api/v1/security/back-office";
        using var login = await client.PostAsJsonAsync(security + "/login", new { username = AdminEmail, password });
        Assert.True(login.IsSuccessStatusCode, "Test sign-in failed: " + login.StatusCode);
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var redirect = new Uri(Address, "/umbraco/oauth_complete").ToString();
        using var authorize = await client.GetAsync(QueryHelpers.AddQueryString(security + "/authorize", new Dictionary<string, string?> {
            ["client_id"] = "umbraco-back-office", ["response_type"] = "code", ["redirect_uri"] = redirect,
            ["code_challenge"] = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), ["code_challenge_method"] = "S256", ["state"] = "antispam-tests",
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

    /// <summary>What Umbraco's preview service puts in its cookie when the administrator enters preview.</summary>
    public async Task<string> PreviewTokenAsync()
    {
        using var scope = app.Services.CreateScope();
        var administrator = scope.ServiceProvider.GetRequiredService<IUserService>().GetByEmail(AdminEmail)!;
        var accessor = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>();
        var previous = accessor.HttpContext;
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Scheme = Address.Scheme;
        context.Request.Host = new Microsoft.AspNetCore.Http.HostString(Address.Authority);
        try
        {
            accessor.HttpContext = context;
            Assert.True(await scope.ServiceProvider.GetRequiredService<IPreviewService>().TryEnterPreviewAsync(administrator));
            var cookies = context.Response.Headers.SetCookie.Select(value => Microsoft.Net.Http.Headers.SetCookieHeaderValue.Parse(value!));
            return Assert.Single(cookies, cookie => cookie.Name == Constants.Web.PreviewCookieName).Value.ToString();
        }
        finally { accessor.HttpContext = previous; }
    }

    private sealed class RecordingAnswers(HttpMessageHandler inner, ConcurrentQueue<string> answers) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            await response.Content.LoadIntoBufferAsync(ct);
            answers.Enqueue(response.Headers + "\n" + await response.Content.ReadAsStringAsync(ct));
            return response;
        }
    }
}

/// <summary>What the test sees of every call the class gets.</summary>
internal sealed class ContactCalls
{
    public sealed record Call(string Operation, string? Values, IReadOnlyDictionary<string, string> FormData);
    public ConcurrentQueue<Call> All { get; } = new();
    public int Count(string operation) => All.Count(call => call.Operation == operation);
}

/// <summary>A class with no spam code of its own: anyone may create, and there is always a record 1 to update.</summary>
internal sealed class RecordingContact(ContactCalls calls) : IFormResource
{
    public const string ResourceAlias = "antispam.contact";
    public string Alias => ResourceAlias;
    public string ContractVersion => "1";
    public IReadOnlyList<FormField> Fields { get; } = [new("name", "Your name", "text", MaxLength: 100), new("message", "Message", "text")];
    private static FormRecord Record(string? key) => new(key, key is null ? null : "1", new Dictionary<string, object?> { ["name"] = null, ["message"] = null }, new(Create: true, Update: true, Delete: true));
    private FormResult Called(string operation, string? values, FormExecutionContext context, string? key)
    {
        calls.All.Enqueue(new(operation, values, context.FormData));
        return FormResult.Ok(Record(key));
    }
    public Task<FormResult> InitializeAsync(FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Ok(Record(null)));
    public Task<FormResult> LoadAsync(string key, FormExecutionContext context, CancellationToken ct) => Task.FromResult(Called("load", null, context, key));
    public Task<FormResult> CreateAsync(JsonElement input, FormExecutionContext context, CancellationToken ct) => Task.FromResult(Called("create", input.GetRawText(), context, null));
    public Task<FormResult> UpdateAsync(string key, JsonElement input, string? checksum, FormExecutionContext context, CancellationToken ct) => Task.FromResult(Called("update", input.GetRawText(), context, key));
    public Task<FormResult> DeleteAsync(string key, string? checksum, FormExecutionContext context, CancellationToken ct)
    {
        Called("delete", null, context, key);
        return Task.FromResult(new FormResult(204));
    }
}

/// <summary>A site's own adapter, as a site would register it, whose answer is whatever its token says. It never touches the network.</summary>
internal sealed class StandInService : IAntiSpamService
{
    public const string Field = "standin-token";
    public ConcurrentQueue<AntiSpamVerification> Asked { get; } = new();
    public string Alias => "standin";
    public string Name => "Stand-in service";
    public string TokenField => Field;
    public AntiSpamWidget Widget(AntiSpamOptions options) => new("https://standin.example.test/api.js?render=explicit", "standin",
        options.Theme is { } theme ? new Dictionary<string, string> { ["theme"] = theme } : null);
    public async Task<AntiSpamAnswer> VerifyAsync(AntiSpamVerification request, CancellationToken ct)
    {
        Asked.Enqueue(request);
        switch (request.Token)
        {
            case "good": return AntiSpamAnswer.Passed;
            case "outage": return AntiSpamAnswer.Unavailable($"The stand-in is down. It was given {request.SecretKey} and {request.Token}.");
            case "throw": throw new InvalidOperationException("It blew up holding " + request.SecretKey);
            case "hang": await Task.Delay(TimeSpan.FromMinutes(1), ct); return AntiSpamAnswer.Passed;
            default: return AntiSpamAnswer.Failed;
        }
    }
}

/// <summary>Stands in for the outside world behind the real adapters: it answers with <see cref="Reply"/> and keeps what it was sent.</summary>
internal sealed class RecordingVerifier : HttpMessageHandler
{
    public sealed record Sent(HttpMethod Method, Uri Url, string Body);
    public ConcurrentQueue<Sent> Requests { get; } = new();
    public Func<HttpResponseMessage> Reply { get; set; } = () => new(HttpStatusCode.OK) { Content = new StringContent("""{"success":true}""") };
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Enqueue(new(request.Method, request.RequestUri!, await request.Content!.ReadAsStringAsync(ct)));
        return Reply();
    }
    protected override void Dispose(bool disposing) { /* The site's one instance outlives each client the factory builds around it. */ }
}

/// <summary>Everything the site logged, to check what was said once and what was never said at all.</summary>
internal sealed class CapturedLog : ILoggerProvider
{
    public ConcurrentQueue<(LogLevel Level, string Category, string Message)> Entries { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Writer(this, categoryName);
    public void Dispose() { }
    private sealed class Writer(CapturedLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => log.Entries.Enqueue((logLevel, category, formatter(state, exception) + exception));
    }
}

/// <summary>Answers Umbraco's route to a page with AppBlocks' own view component.</summary>
public sealed class AntiSpamPageController(ILogger<RenderController> logger, ICompositeViewEngine engine, IUmbracoContextAccessor contexts) : RenderController(logger, engine, contexts)
{
    public override IActionResult Index() => new ViewComponentResult { ViewComponentName = "WebsiteBlocks", Arguments = new { page = CurrentPage } };
}

/// <summary>The keys Cloudflare, Google and hCaptcha publish for testing, which always give the same answer. Nothing here is a real secret.</summary>
internal static class AntiSpamRealKeys
{
    public static readonly IReadOnlyDictionary<string, (string SiteKey, string SecretKey, string Token)> Passing = new Dictionary<string, (string, string, string)> {
        ["turnstile"] = ("1x00000000000000000000AA", "1x0000000000000000000000000000000AA", "XXXX.DUMMY.TOKEN.XXXX"),
        ["recaptchaV2"] = ("6LeIxAcTAAAAAJcZVRqyHh71UMIEGNQ_MXjiZKhI", "6LeIxAcTAAAAAGG-vFI1TnRWxMZNFuojJ4WifJWe", "any-token-at-all"),
        ["hcaptcha"] = ("10000000-ffff-ffff-ffff-000000000001", "0x0000000000000000000000000000000000000000", "10000000-aaaa-bbbb-cccc-000000000001"),
    };
}
