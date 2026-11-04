using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using AppBlocks.Attachments;
using AppBlocks.Authentication;
using AppBlocks.Blocks;
using AppBlocks.Connections;
using AppBlocks.DataSources;
using AppBlocks.Forms;
using AppBlocks.InteractiveReports.Reports;
using InteractiveReport.AspNetCore;
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
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Blocks;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Core.Sync;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Web.Common.Controllers;
using Umbraco.Extensions;

namespace AppBlocks.InteractiveReports.Tests;

/// <summary>
/// A plain Umbraco site with AppBlocks and its reports companion and nothing else: no host application and no uSync. Its pages
/// have no templates; <see cref="PublicPageController"/> answers Umbraco's own route with AppBlocks' real view components.
/// Every page sits under a home page, and one branch is behind Public Access set on its parent, so its page only inherits it.
/// </summary>
internal sealed class PublicSite : IAsyncDisposable
{
    public static readonly Guid ContactPage = Key("contact"), ContactForm = Key("contact-form");
    public static readonly Guid RefusingPage = Key("refusing"), RefusingForm = Key("refusing-form");
    public static readonly Guid EnginePage = Key("engine"), EngineForm = Key("engine-form");
    public static readonly Guid ReportPage = Key("reports"), PublicReport = Key("public-report"), PersonalReport = Key("personal-report");
    public static readonly Guid SizesPage = Key("sizes"), UnsizedReport = Key("unsized-report"), ZeroSizedReport = Key("zero-sized-report"), EmptySizedReport = Key("empty-sized-report");
    public static readonly Guid FilesPage = Key("files"), ClosedFiles = Key("closed-files"), OpenFiles = Key("open-files"), HiddenFiles = Key("hidden-files");
    public static readonly Guid DataPage = Key("data"), Rows = Key("rows"), Schedule = Key("schedule");
    public static readonly Guid OrderPage = Key("order"), OrderForm = Key("order-form"), OrderLines = Key("order-lines");
    public static readonly Guid MembersPage = Key("members"), ProtectedPage = Key("protected"), ProtectedForm = Key("protected-form"), ProtectedReport = Key("protected-report"), ProtectedFiles = Key("protected-files");
    public const string AdminEmail = "public.admin@example.test";

    private readonly WebApplication app;
    private readonly string password;

    private PublicSite(WebApplication app, Uri address, string password, RecordedForms forms)
    {
        this.app = app;
        Address = address;
        this.password = password;
        Forms = forms;
    }

    public Uri Address { get; }
    public RecordedForms Forms { get; }
    public Switches Switch => app.Services.GetRequiredService<Switches>();
    public RecordedBindings Bindings => app.Services.GetRequiredService<RecordedBindings>();

    private static Guid Key(string name) => AppBlockSchema.Key("public-site:" + name);

    /// <param name="outputCache">Gives the site what a site's own performance work might: ASP.NET Core's output cache on its pages, by a base policy.</param>
    public static async Task<PublicSite> StartAsync(bool outputCache = false, string? noStorePages = null)
    {
        var root = Directory.CreateTempSubdirectory("appblocks-public-site-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
        var password = "Public-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)) + "!a";
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
            ApplicationName = typeof(PublicSite).Assembly.GetName().Name, ContentRootPath = root, EnvironmentName = "Development",
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        if (noStorePages is not null) builder.Configuration["AppBlocks:NoStorePages"] = noStorePages;
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["ConnectionStrings:umbracoDbDSN"] = $"Data Source={Path.Combine(root, "umbraco.db")};Pooling=False",
            ["ConnectionStrings:umbracoDbDSN_ProviderName"] = "Microsoft.Data.Sqlite",
            ["AppBlocks:DataRoot"] = root,
            ["Umbraco:CMS:Global:Id"] = Guid.NewGuid().ToString(), ["Umbraco:CMS:Global:UseHttps"] = "false",
            ["Umbraco:CMS:ModelsBuilder:ModelsMode"] = "Nothing",
            ["Umbraco:CMS:TypeFinder:AdditionalAssemblyExclusionEntries:0"] = "xunit.",
            // As a site with the Oracle driver installed sets it, so that Umbraco's startup scan does not load the driver's optional cloud assemblies; see docs/connections.md.
            ["Umbraco:CMS:TypeFinder:AdditionalAssemblyExclusionEntries:1"] = "Oracle.ManagedDataAccess",
            ["Umbraco:CMS:Unattended:InstallUnattended"] = "true", ["Umbraco:CMS:Unattended:UpgradeUnattended"] = "true",
            ["Umbraco:CMS:Unattended:UnattendedUserName"] = "Public admin",
            ["Umbraco:CMS:Unattended:UnattendedUserEmail"] = AdminEmail,
            ["Umbraco:CMS:Unattended:UnattendedUserPassword"] = password,
            // Reports that are not blocks: an anonymous visitor gets nothing from any of them, however they are named or marked.
            ["InteractiveReport:Reports:adminonly:Connection"] = ConnectionCatalog.ApplicationConnection,
            ["InteractiveReport:Reports:adminonly:Sql"] = "SELECT id FROM PUBLIC_ROWS",
            ["InteractiveReport:Reports:adminonly:Authorization:Policy"] = ReportAdministratorRequirement.Policy,
            ["InteractiveReport:Reports:configured:Connection"] = ConnectionCatalog.ApplicationConnection,
            ["InteractiveReport:Reports:configured:Sql"] = "SELECT id FROM PUBLIC_ROWS",
            ["InteractiveReport:Reports:configured:Authorization:AllowAnonymous"] = "true",
        });
        builder.Services.Configure<InstallDefaultDataSettings>(Constants.Configuration.NamedOptions.InstallDefaultData.Languages,
            settings => { settings.InstallData = InstallDefaultDataOption.Values; settings.Values = new HashSet<string> { "en" }; });
        builder.CreateUmbracoBuilder().AddBackOffice().AddWebsite().AddComposers().Build();
        // This project is not a Web SDK one, so nothing generates the application parts MVC relies on.
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(Umbraco.Cms.Api.Management.Controllers.ManagementApiControllerBase).Assembly)
            .AddApplicationPart(typeof(Umbraco.Cms.Api.Common.Attributes.MapToApiAttribute).Assembly)
            .AddApplicationPart(typeof(RenderController).Assembly)
            .AddApplicationPart(typeof(PublicSite).Assembly);
        var forms = new RecordedForms();
        builder.Services.AddSingleton(forms);
        builder.Services.AddSingleton<RecordedBindings>();
        builder.Services.AddSingleton<IBindVariableProvider>(services => services.GetRequiredService<RecordedBindings>());
        builder.Services.AddSingleton<Switches>();
        builder.Services.AddScoped<IFormResource, ContactResource>();
        builder.Services.AddScoped<IFormResource, RefusingResource>();
        builder.Services.AddScoped<IFormResource, ClosedRecords>();
        builder.Services.AddScoped<IFormResource, OpenRecords>();
        builder.Services.AddScoped<IFormResource, HiddenRecords>();
        // The data source engine itself, as a form's class and as a tabular block's rows.
        builder.Services.AddScoped<IFormResource>(services => new SqlDataSource(new DataSourceDefinition {
            Key = Key("engine-source"), Name = "Rows", Alias = "public.engine", Connection = ConnectionCatalog.ApplicationConnection, Source = "PUBLIC_ROWS",
            Access = services.GetRequiredService<Switches>().PublicEngine ? DataSourceAccess.Public : DataSourceAccess.NotPublic,
            AllowAnonymousWrites = services.GetRequiredService<Switches>().AnonymousEngineWrites,
            KeyColumns = ["id"], AllowInsert = true, AllowUpdate = true, AllowDelete = true,
        }, services.GetRequiredService<DataSourceRuntime>(), services.GetRequiredService<IDataSourceUserResolver>(), services.GetServices<IDataSourcePolicy>()));
        // An order and its lines on the engine, the order with a backing class: a form and its tabular block that save together.
        builder.Services.AddSingleton<OrderRules>();
        builder.Services.AddSingleton<IDataSourceHandler>(services => services.GetRequiredService<OrderRules>());
        builder.Services.AddScoped<IFormResource>(services => new SqlDataSource(new DataSourceDefinition {
            Key = Key("orders-source"), Name = "Orders", Alias = "public.orders", Connection = ConnectionCatalog.ApplicationConnection, Source = "PUBLIC_ORDERS",
            Access = DataSourceAccess.NotPublic, KeyColumns = ["id"], AllowInsert = true, AllowUpdate = true, AllowDelete = true, Handler = OrderRules.HandlerAlias,
        }, services.GetRequiredService<DataSourceRuntime>(), services.GetRequiredService<IDataSourceUserResolver>(), services.GetServices<IDataSourcePolicy>(),
            null, services.GetRequiredService<DataSourceHandlers>()));
        builder.Services.AddScoped<IFormResource>(services => new SqlDataSource(new DataSourceDefinition {
            Key = Key("order-lines-source"), Name = "Order lines", Alias = "public.orders.lines", Connection = ConnectionCatalog.ApplicationConnection, Source = "PUBLIC_ORDER_LINES",
            Access = DataSourceAccess.NotPublic, KeyColumns = ["id"], AllowInsert = true, AllowUpdate = true, AllowDelete = true,
        }, services.GetRequiredService<DataSourceRuntime>(), services.GetRequiredService<IDataSourceUserResolver>(), services.GetServices<IDataSourcePolicy>()));
        builder.Services.AddSingleton<IDataSourcePolicy, SwitchedPolicy>();
        builder.Services.AddScoped<IProtectedPageAdmission, SwitchedAdmission>();
        builder.Services.Replace(ServiceDescriptor.Scoped<IReportAccessPolicy, SwitchedReportPolicy>());
        builder.Services.AddScoped<DefaultReportAccessPolicy>();
        builder.Services.Replace(ServiceDescriptor.Singleton<IServerRoleAccessor, SingleServerRoleAccessor>());
        if (outputCache) builder.Services.AddOutputCache(options => options.AddBasePolicy(policy => policy
            .With(context => !context.HttpContext.Request.Path.StartsWithSegments("/api") && !context.HttpContext.Request.Path.StartsWithSegments("/umbraco")).Cache()));
        var app = builder.Build();
        await app.BootUmbracoAsync();
        if (outputCache) app.UseOutputCache();
        MapTestLogin(app);
        app.UseUmbraco().WithMiddleware(u => { u.UseBackOffice(); u.UseWebsite(); })
            .WithEndpoints(u => { u.UseBackOfficeEndpoints(); u.UseWebsiteEndpoints(); });
        await app.StartAsync();
        var upgrade = app.Services.GetServices<IHostedService>().OfType<BackgroundService>()
            .Single(service => service.GetType().Name == "UnattendedUpgradeBackgroundService");
        if (upgrade.ExecuteTask is { } task) await task.WaitAsync(TimeSpan.FromSeconds(90));
        var runtime = app.Services.GetRequiredService<IRuntimeState>();
        Assert.True(runtime.Level == RuntimeLevel.Run, runtime.BootFailedException?.ToString() ?? runtime.Level.ToString());
        var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        var site = new PublicSite(app, address, password, forms);
        await site.PublishAsync();
        return site;
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }

    /// <summary>A real HTTP client with its own cookies and no account of any kind.</summary>
    public HttpClient Anonymous() => new() { BaseAddress = Address };

    public static string FormUrl(Guid page, Guid form) => $"/api/forms/{page}/{form}";
    public static string FilesUrl(Guid page, Guid block) => $"/api/attachments/{page}/{block}";
    public static string ReportName(Guid page, Guid block) => AppBlockContent.ReportName(page, block);

    private async Task PublishAsync()
    {
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var types = services.GetRequiredService<IContentTypeService>();
        for (var i = 0; i < 100 && types.Get(AppBlockSchema.FormsContainerAlias) is null; i++) await Task.Delay(100);
        await using (var connection = services.GetRequiredService<ConnectionCatalog>().Openable(ConnectionCatalog.ApplicationConnection))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE PUBLIC_ROWS (id INTEGER PRIMARY KEY, title TEXT NOT NULL, owner TEXT);
                INSERT INTO PUBLIC_ROWS (id, title, owner) VALUES (1, 'First public row', 'someone'), (2, 'Second public row', '');
                CREATE TABLE SCHEDULE_ROWS ("key" TEXT PRIMARY KEY, label TEXT, "group" TEXT);
                CREATE TABLE SCHEDULE_BARS ("key" TEXT PRIMARY KEY, "row" TEXT, "start" TEXT, "end" TEXT, label TEXT, kind TEXT, link TEXT);
                CREATE TABLE PUBLIC_ORDERS (id INTEGER PRIMARY KEY, customer TEXT NOT NULL, lines INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE PUBLIC_ORDER_LINES (id INTEGER PRIMARY KEY, order_id INTEGER NOT NULL REFERENCES PUBLIC_ORDERS (id), item TEXT NOT NULL);
                """;
            await command.ExecuteNonQueryAsync();
        }
        var strings = services.GetRequiredService<IShortStringHelper>();
        var dataTypes = services.GetRequiredService<IDataTypeService>();
        var list = new DataType(services.GetRequiredService<DataEditorCollection>().First(e => e.Alias == "Umbraco.BlockList"), services.GetRequiredService<IConfigurationEditorJsonSerializer>()) {
            Key = Key("list"), Name = "Public page blocks", EditorUiAlias = "Umb.PropertyEditorUi.BlockList",
        };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var configuration = new BlockListConfiguration { Blocks = new[] { AppBlockSchema.Key(AppBlockSchema.FormsContainerAlias), AppBlockSchema.Key("appReport") }
            .Select(key => new BlockListConfiguration.BlockConfiguration { ContentElementTypeKey = key }).ToArray() };
        list.SetConfigurationData(JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(configuration, options), options)!);
        var created = await dataTypes.CreateAsync(list, Constants.Security.SuperUserKey);
        Assert.True(created.Success, created.Status.ToString());
        var pageType = new ContentType(strings, -1) { Key = Key("type"), Alias = "publicPage", Name = "Public page", AllowedAsRoot = true };
        pageType.AddPropertyType(new PropertyType(strings, list) { Alias = "content", Name = "Content" });
        var typed = await types.CreateAsync(pageType, Constants.Security.SuperUserKey);
        Assert.True(typed.Success, typed.Result.ToString());
        var contents = services.GetRequiredService<IContentService>();
#pragma warning disable CS0618
        IContent Page(string name, Guid key, int parent, JsonObject? blocks = null)
        {
            var page = contents.Create(name, parent, "publicPage");
            page.Key = key;
            if (blocks is not null) page.SetValue("content", blocks.ToJsonString());
            Assert.True(contents.Save(page).Success);
            var published = contents.Publish(page, ["*"]);
            Assert.True(published.Success, name + ": " + string.Join("; ", published.EventMessages?.GetAll().Select(m => m.Message) ?? []));
            return page;
        }
        var sources = contents.Create("Data sources", -1, "dataSources");
        Assert.True(contents.Save(sources).Success);
        Assert.True(contents.Publish(sources, ["*"]).Success);
        foreach (var (alias, table) in new[] { ("public.schedule.rows", "SCHEDULE_ROWS"), ("public.schedule.bars", "SCHEDULE_BARS") })
        {
            var source = contents.Create(alias, sources.Id, "sqlDataSource");
            source.SetValue("alias", alias); source.SetValue("connection", ConnectionCatalog.ApplicationConnection);
            source.SetValue("source", table); source.SetValue("keyColumns", "key");
            source.SetValue("access", "Not public");
            Assert.True(contents.Save(source).Success);
            var published = contents.Publish(source, ["*"]);
            Assert.True(published.Success, alias + ": " + string.Join("; ", published.EventMessages?.GetAll().Select(m => m.Message) ?? []));
        }
        var home = Page("Home", Key("home"), -1);
        var signIn = Page("Sign in", Key("sign-in"), home.Id);
        Page("Contact", ContactPage, home.Id, Container(FormBlock(ContactForm, "public.contact", Field("name", "Your name", ("defaultValue", ":USER_NAME")), Field("message", "Message", ("defaultValue", ":PARAM.topic")))));
        Page("Refusing", RefusingPage, home.Id, Container(FormBlock(RefusingForm, "public.refuses", Field("name", "Your name"))));
        Page("Engine", EnginePage, home.Id, Container(FormBlock(EngineForm, "public.engine", Field("title", "Title"))));
        Page("Order", OrderPage, home.Id, Container(FormBlock(OrderForm, "public.orders", Field("customer", "Customer"),
            Block(OrderLines, AppBlockSchema.TabularAlias, ("heading", "Lines"), ("dataSource", "public.orders.lines"), ("linkField", "orderId"), ("fields", "item")))));
        Page("Reports", ReportPage, home.Id, Reports((PublicReport, Source("table", "PUBLIC_ROWS")),
            (PersonalReport, Source("sql", "SELECT id, title FROM PUBLIC_ROWS WHERE owner = :USER_KEY OR :USER_KEY IS NULL"))));
        Page("Sizes", SizesPage, home.Id, Reports((UnsizedReport, Source("table", "PUBLIC_ROWS"), null), (ZeroSizedReport, Source("table", "PUBLIC_ROWS"), JsonValue.Create(0)), (EmptySizedReport, Source("table", "PUBLIC_ROWS"), JsonValue.Create(""))));
        Page("Files", FilesPage, home.Id, Container(Attachments(ClosedFiles, "public.closed"), Attachments(OpenFiles, "public.open"), Attachments(HiddenFiles, "public.hidden")));
        Page("Data", DataPage, home.Id, Container(
            Block(Rows, AppBlockSchema.TabularAlias, ("dataSource", "public.engine"), ("linkField", "owner"), ("ownerResource", "public.closed"), ("ownerKey", "1")),
            Block(Schedule, AppBlockSchema.ScheduleAlias, ("rowsSource", "public.schedule.rows"), ("barsSource", "public.schedule.bars"), ("daysBefore", "1"), ("daysAfter", "1"), ("columns", "Days"))));
        var members = Page("Members", MembersPage, home.Id);
        var inside = Reports((ProtectedReport, Source("table", "PUBLIC_ROWS")));
        Merge(inside, Container(FormBlock(ProtectedForm, "public.contact", Field("name", "Your name")), Attachments(ProtectedFiles, "public.open")));
        Page("Inside", ProtectedPage, members.Id, inside);
        services.GetRequiredService<IPublicAccessService>().Save(new PublicAccessEntry(members, signIn, signIn, []));
#pragma warning restore CS0618
    }

    private static string Source(string kind, string source) => new JsonObject { ["kind"] = kind, ["connection"] = ConnectionCatalog.ApplicationConnection, ["source"] = source }.ToJsonString();

    private sealed record Item(Guid Key, string Alias, (string Alias, object Value)[] Values, Item[] Children);

    private static Item Block(Guid key, string alias, params (string Alias, object Value)[] values) => new(key, alias, values, []);
    private static Item Attachments(Guid key, string owner) => Block(key, AppBlockSchema.AttachmentsAlias, ("heading", "Files"), ("ownerResource", owner), ("ownerKey", "1"));
    private static Item Field(string field, string label, params (string Alias, object Value)[] values)
        => Block(AppBlockSchema.Key("public-site:field:" + field + ":" + label), "appText", [("field", field), ("label", label), .. values]);
    private static Item FormBlock(Guid key, string resource, params Item[] fields)
    {
        var binding = Set("Umbraco.SingleBlock");
        Add(binding, "Umbraco.SingleBlock", AppBlockSchema.Key("public-site:interface:" + key), AppBlockSchema.Key("appFormInterface"), ("resource", JsonValue.Create(resource)));
        return new(key, "appForm", [("heading", "Form"), ("formInterface", binding)], fields);
    }

    /// <summary>A forms container holding these blocks at its root, and a form's fields in the form's first area.</summary>
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
        Add(body, "Umbraco.BlockList", AppBlockSchema.Key("public-site:container:" + items[0].Key), AppBlockSchema.Key(AppBlockSchema.FormsContainerAlias), ("forms", JsonValue.Create(fields.ToJsonString())));
        return body;
    }

    private static (string, JsonNode?)[] Values(Item item) => item.Values.Select(v => (v.Alias, v.Value is JsonNode node ? node : (JsonNode?)JsonValue.Create((string)v.Value))).ToArray();

    private static JsonObject Reports(params (Guid Key, string Source)[] reports) => Reports(reports.Select(report => (report.Key, report.Source, (JsonNode?)JsonValue.Create(10))).ToArray());

    /// <param name="reports">Each with the page size it was saved with; null for one that never had a value, as the backoffice saves a block nobody filled in.</param>
    private static JsonObject Reports(params (Guid Key, string Source, JsonNode? PageSize)[] reports)
    {
        var body = Set("Umbraco.BlockList");
        foreach (var (key, source, pageSize) in reports)
            Add(body, "Umbraco.BlockList", key, AppBlockSchema.Key("appReport"), [("heading", JsonValue.Create("Rows")), ("dataSource", JsonValue.Create(source)), .. pageSize is null ? [] : new (string, JsonNode?)[] { ("pageSize", pageSize) }]);
        return body;
    }

    private static void Merge(JsonObject into, JsonObject from)
    {
        foreach (var name in new[] { "contentData", "expose" })
            foreach (var node in from[name]!.AsArray().ToArray()) { from[name]!.AsArray().Remove(node); into[name]!.AsArray().Add(node); }
        foreach (var node in from["layout"]!["Umbraco.BlockList"]!.AsArray().ToArray()) { from["layout"]!["Umbraco.BlockList"]!.AsArray().Remove(node); into["layout"]!["Umbraco.BlockList"]!.AsArray().Add(node); }
    }

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

    /// <summary>A real HTTP client carrying a real cookie for this account, as a browser that signed in earlier does, through the site's test-only route.</summary>
    public async Task<HttpClient> SignedIn(string scheme, string id, string name)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = Address };
        await SignInAsync(client, scheme, id, name);
        return client;
    }

    public async Task SignInAsync(HttpClient client, string scheme, string id, string name)
    {
        using var login = await client.GetAsync($"{TestLogin}/{id}?scheme={scheme}&name={Uri.EscapeDataString(name)}");
        login.EnsureSuccessStatusCode();
    }

    private const string TestLogin = "/__tests/login";

    private static void MapTestLogin(WebApplication app) => app.MapGet(TestLogin + "/{id}", async (string id, string scheme, string name, HttpContext http) =>
    {
        var authentication = scheme == "member" ? IdentityConstants.ApplicationScheme : Constants.Security.BackOfficeExposedAuthenticationType;
        await http.SignInAsync(authentication, new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, id), new(ClaimTypes.Name, name)], authentication)));
        // Umbraco marks the backoffice cookie Secure, and a cookie container never sends one of those over plain HTTP.
        http.Response.Headers.SetCookie = http.Response.Headers.SetCookie.Select(cookie => System.Text.RegularExpressions.Regex.Replace(cookie!, "; *secure(?=;|$)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToArray();
        return Results.Ok();
    });

    /// <summary>One of the site's singletons, such as what a registered class recorded.</summary>
    public T Service<T>() where T : notnull => app.Services.GetRequiredService<T>();

    /// <summary>Runs something against the site's services, in a scope of its own.</summary>
    public async Task WithServices(Func<IServiceProvider, Task> work)
    {
        using var scope = app.Services.CreateScope();
        await work(scope.ServiceProvider);
    }

    public async Task<HttpClient> Administrator(string email = AdminEmail)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = Address };
        const string security = "/umbraco/management/api/v1/security/back-office";
        using var login = await client.PostAsJsonAsync(security + "/login", new { username = email, password });
        Assert.True(login.IsSuccessStatusCode, "Test sign-in failed: " + login.StatusCode);
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var redirect = new Uri(Address, "/umbraco/oauth_complete").ToString();
        using var authorize = await client.GetAsync(QueryHelpers.AddQueryString(security + "/authorize", new Dictionary<string, string?> {
            ["client_id"] = "umbraco-back-office", ["response_type"] = "code", ["redirect_uri"] = redirect,
            ["code_challenge"] = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), ["code_challenge_method"] = "S256", ["state"] = "public-tests",
        }));
        Assert.Equal(System.Net.HttpStatusCode.Redirect, authorize.StatusCode);
        var code = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["code"].ToString();
        using var token = await client.PostAsync(security + "/token", new FormUrlEncodedContent(new Dictionary<string, string> {
            ["grant_type"] = "authorization_code", ["client_id"] = "umbraco-back-office", ["redirect_uri"] = redirect, ["code"] = code, ["code_verifier"] = verifier,
        }));
        Assert.True(token.IsSuccessStatusCode, "Test management token request failed: " + token.StatusCode);
        using var response = JsonDocument.Parse(await token.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", response.RootElement.GetProperty("access_token").GetString());
        return client;
    }

    public async Task SetUserPassword(Guid key) => await WithServices(async services => {
        var manager = services.GetRequiredService<IBackOfficeUserManager>();
        var identity = (await manager.FindByIdAsync(key.ToString()))!;
        Assert.True((await manager.ChangePasswordWithResetAsync(identity.Id,
            await manager.GeneratePasswordResetTokenAsync(identity), password)).Succeeded);
    });
}

/// <summary>What the contact class received, so a test can see it reached the class and what with.</summary>
/// <summary>Records every execution context the :APP. providers are asked for, so a test can see which page a block report bound.</summary>
internal sealed class RecordedBindings : IBindVariableProvider
{
    public ConcurrentQueue<FormExecutionContext> Contexts { get; } = new();
    public ValueTask<IReadOnlyDictionary<string, object?>?> ResolveAsync(FormExecutionContext context, CancellationToken ct)
    {
        Contexts.Enqueue(context);
        return ValueTask.FromResult<IReadOnlyDictionary<string, object?>?>(new Dictionary<string, object?> { ["page"] = context.PageKey.ToString("N") });
    }
}

internal sealed class RecordedForms
{
    public sealed record Submission(ClaimsPrincipal Member, string Values, IReadOnlyDictionary<string, string> FormData, string? SubmissionKey);
    public ConcurrentQueue<Submission> Created { get; } = new();
    public ConcurrentQueue<ClaimsPrincipal> Initialized { get; } = new();
}

/// <summary>What a test turns on to see a developer's rule refuse someone anonymous, or a host's rule admit someone Umbraco refuses.</summary>
internal sealed class Switches
{
    public DataSourceUser? LastDataAccount;
    public volatile bool ReportPolicyRefuses, DataPolicyRefuses, PublicEngine, AnonymousEngineWrites, AdmitBackOfficeAdministrators;
}

/// <summary>
/// What a host might register for a page Umbraco's Public Access refuses: while switched on, the current backoffice User
/// is admitted if they are in Umbraco's Administrators group. AppBlocks registers no rule of its own.
/// </summary>
internal sealed class SwitchedAdmission(Switches switches, ApplicationAccessPolicy access) : IProtectedPageAdmission
{
    public async Task<ClaimsPrincipal?> AdmitAsync(HttpContext context, IPublishedContent page, CancellationToken ct)
        => switches.AdmitBackOfficeAdministrators ? await access.BackOfficeAdministratorAsync(context) : null;
}

/// <summary>A create-only public form: no record to load, a captcha token checked from the form's other controls.</summary>
internal sealed class ContactResource(RecordedForms recorded) : IFormResource
{
    public string Alias => "public.contact";
    public string ContractVersion => "1";
    public IReadOnlyList<FormField> Fields { get; } = [new("name", "Your name", "text", Required: true, MaxLength: 100), new("message", "Message", "text", MaxLength: 4000)];
    private static FormRecord Fresh() => new(null, null, new Dictionary<string, object?> { ["name"] = null, ["message"] = null }, new(Create: true));

    public Task<FormResult> InitializeAsync(FormExecutionContext context, CancellationToken ct)
    {
        recorded.Initialized.Enqueue(context.Member);
        return Task.FromResult(FormResult.Ok(Fresh()));
    }
    public Task<FormResult> LoadAsync(string key, FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Missing());
    public Task<FormResult> CreateAsync(JsonElement input, FormExecutionContext context, CancellationToken ct)
    {
        recorded.Created.Enqueue(new(context.Member, input.GetRawText(), context.FormData, context.SubmissionKey));
        if (context.FormData.GetValueOrDefault("g-recaptcha-response") != "good-token") return Task.FromResult(FormResult.Invalid("", "Please confirm you are not a robot."));
        return Task.FromResult(FormResult.Ok(Fresh()));
    }
    public Task<FormResult> UpdateAsync(string key, JsonElement input, string version, FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Denied());
    public Task<FormResult> DeleteAsync(string key, string version, FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Denied());
}

/// <summary>A class that decides for itself that anonymous visitors may not use it, on every operation.</summary>
internal sealed class RefusingResource : IFormResource
{
    public string Alias => "public.refuses";
    public string ContractVersion => "1";
    public IReadOnlyList<FormField> Fields { get; } = [new("name", "Your name", "text")];
    private static Task<FormResult> Answer(FormExecutionContext context) => Task.FromResult(context.Member.Identity?.IsAuthenticated != true ? FormResult.Denied()
        : FormResult.Ok(new(null, null, new Dictionary<string, object?> { ["name"] = "Signed in" }, new(Create: true))));
    public Task<FormResult> InitializeAsync(FormExecutionContext context, CancellationToken ct) => Answer(context);
    public Task<FormResult> LoadAsync(string key, FormExecutionContext context, CancellationToken ct) => Answer(context);
    public Task<FormResult> CreateAsync(JsonElement input, FormExecutionContext context, CancellationToken ct) => Answer(context);
    public Task<FormResult> UpdateAsync(string key, JsonElement input, string version, FormExecutionContext context, CancellationToken ct) => Answer(context);
    public Task<FormResult> DeleteAsync(string key, string version, FormExecutionContext context, CancellationToken ct) => Answer(context);
}

/// <summary>A record anyone can load. Its class says nothing about files.</summary>
internal class ClosedRecords : IFormResource
{
    public virtual string Alias => "public.closed";
    public string ContractVersion => "1";
    public IReadOnlyList<FormField> Fields { get; } = [new("name", "Your name", "text")];
    public virtual Task<FormResult> LoadAsync(string key, FormExecutionContext context, CancellationToken ct)
        => Task.FromResult(FormResult.Ok(new(key, "1", new Dictionary<string, object?> { ["name"] = "A record" }, new())));
    public Task<FormResult> InitializeAsync(FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Denied());
    public Task<FormResult> CreateAsync(JsonElement input, FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Denied());
    public Task<FormResult> UpdateAsync(string key, JsonElement input, string version, FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Denied());
    public Task<FormResult> DeleteAsync(string key, string version, FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Denied());
}

/// <summary>The same record, whose class explicitly lets anyone change its files.</summary>
internal sealed class OpenRecords : ClosedRecords, IAttachmentOwner
{
    public override string Alias => "public.open";
    public ValueTask<bool> MayWriteAttachmentsAsync(FormRecord record, FormExecutionContext context, CancellationToken ct) => ValueTask.FromResult(true);
}

/// <summary>A record that does not load for someone anonymous, so neither do its files, whatever its write rule says.</summary>
internal sealed class HiddenRecords : ClosedRecords, IAttachmentOwner
{
    public override string Alias => "public.hidden";
    public override Task<FormResult> LoadAsync(string key, FormExecutionContext context, CancellationToken ct)
        => context.Member.Identity?.IsAuthenticated == true ? base.LoadAsync(key, context, ct) : Task.FromResult(FormResult.Denied());
    public ValueTask<bool> MayWriteAttachmentsAsync(FormRecord record, FormExecutionContext context, CancellationToken ct) => ValueTask.FromResult(true);
}

internal sealed class SwitchedReportPolicy(DefaultReportAccessPolicy inner, Switches switches) : IReportAccessPolicy
{
    public Task<bool> IsAdministratorAsync(ClaimsPrincipal user) => inner.IsAdministratorAsync(user);
    public ValueTask<bool> AuthorizeAsync(InteractiveReportAuthorizationRequest request, CancellationToken cancellationToken)
        => switches.ReportPolicyRefuses && request.User.Identity?.IsAuthenticated != true ? ValueTask.FromResult(false) : inner.AuthorizeAsync(request, cancellationToken);
}

/// <summary>
/// The order's backing class: it counts the lines an order will have from the batches saved with it, and refuses an order with a line
/// it does not sell, so a form and its rows are judged together, as an APEX page process judges a form and its grid.
/// </summary>
internal sealed class OrderRules : IDataSourceHandler
{
    public const string HandlerAlias = "public.orders.rules";
    public string Alias => HandlerAlias;
    public ConcurrentQueue<(DataSourceAction Action, int Changes)> Seen { get; } = new();

    public ValueTask<DataSourceOutcome> CreateAsync(DataSourceOperation operation, CancellationToken ct) => CountAsync(operation, 0, ct);

    public async ValueTask<DataSourceOutcome> UpdateAsync(DataSourceOperation operation, CancellationToken ct)
        => await CountAsync(operation, Convert.ToInt32(await operation.Sql.ScalarAsync("SELECT COUNT(*) FROM PUBLIC_ORDER_LINES WHERE order_id = :ARG.id", new { id = operation.Key }, ct)), ct);

    private ValueTask<DataSourceOutcome> CountAsync(DataSourceOperation operation, int stored, CancellationToken ct)
    {
        var changes = operation.Details.SelectMany(detail => detail.Changes).ToArray();
        Seen.Enqueue((operation.Action, changes.Length));
        if (changes.Any(change => change.Values.TryGetProperty("item", out var item) && item.GetString() == "Forbidden"))
            return ValueTask.FromResult(DataSourceOutcome.Invalid("", "Forbidden is not sold."));
        operation.Values!["lines"] = stored + changes.Count(change => change.Operation == RowOperation.Create) - changes.Count(change => change.Operation == RowOperation.Delete);
        return ValueTask.FromResult(DataSourceOutcome.Continue);
    }
}

internal sealed class SwitchedPolicy(Switches switches) : IDataSourcePolicy
{
    public ValueTask<bool> IsAllowedAsync(DataSourceRequest request, CancellationToken ct)
    {
        switches.LastDataAccount = request.User;
        return ValueTask.FromResult(!(switches.DataPolicyRefuses && request.User.IsAnonymous));
    }
}

/// <summary>Answers Umbraco's route to a page with AppBlocks' view components: the site's own layout would wrap this. Which one is the query's choice.</summary>
public sealed class PublicPageController(ILogger<RenderController> logger, ICompositeViewEngine engine, IUmbracoContextAccessor contexts) : RenderController(logger, engine, contexts)
{
    public override IActionResult Index() => Request.Query["component"].ToString() switch {
        "AppBlocks" => new ViewComponentResult { ViewComponentName = "AppBlocks", Arguments = new { page = CurrentPage } },
        "Native" => new ViewComponentResult { ViewComponentName = "AppBlocksBlock", Arguments = new { listItem = CurrentPage!.Value<BlockListModel>("content")![0], page = CurrentPage } },
        _ => new ViewComponentResult { ViewComponentName = "WebsiteBlocks", Arguments = new { page = CurrentPage } },
    };
}
