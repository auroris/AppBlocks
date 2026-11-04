using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using AppBlocks.Blocks;
using AppBlocks.Connections;
using AppBlocks.DataSources;
using AppBlocks.Forms;
using AppBlocks.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
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
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Core.Sync;
using Umbraco.Extensions;

namespace AppBlocks.InteractiveReports.Tests;

/// <summary>
/// A plain Umbraco site with the reports companion, whose AppBlocks database is a restricted SQL Server login, and a page
/// holding a report, and a form with an Attachments block in it. Business data sits in that same database, granted to the
/// login, so the report and the form really run through the account that has no rights over AppBlocks' own tables.
/// </summary>
internal sealed class SetupSite : IAsyncDisposable
{
    public static readonly Guid PageKey = AppBlockSchema.Key("setup-site:page");
    public static readonly Guid ReportKey = AppBlockSchema.Key("setup-site:report");
    public static readonly Guid FormKey = AppBlockSchema.Key("setup-site:form");
    public static readonly Guid FilesKey = AppBlockSchema.Key("setup-site:files");
    public const string TasksResource = "setup.tasks";
    public const string AdminEmail = "setup.admin@example.test";
    public const string EditorEmail = "setup.editor@example.test";
    public static string ReportName => AppBlockContent.ReportName(PageKey, ReportKey);

    private readonly WebApplication app;
    private readonly string password;

    private SetupSite(WebApplication app, Uri address, RecordingLoggerProvider logs, string password)
    {
        this.app = app;
        Address = address;
        Logs = logs;
        this.password = password;
    }

    public Uri Address { get; }
    public RecordingLoggerProvider Logs { get; }
    public IServiceProvider Services => app.Services;

    public static async Task<SetupSite> StartAsync(RestrictedSqlServer database)
    {
        await database.Admin($"""
            CREATE TABLE dbo.BUSINESS_TASKS (id int IDENTITY(1,1) NOT NULL PRIMARY KEY, title nvarchar(200) NOT NULL);
            INSERT INTO dbo.BUSINESS_TASKS (title) VALUES (N'First task'), (N'Second task');
            GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.BUSINESS_TASKS TO [{database.Account}];
            """);
        var root = Directory.CreateTempSubdirectory("appblocks-setup-site-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
        var password = "Setup-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)) + "!a";
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
            ApplicationName = typeof(SetupSite).Assembly.GetName().Name, ContentRootPath = root, EnvironmentName = "Development",
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var logs = new RecordingLoggerProvider();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["ConnectionStrings:umbracoDbDSN"] = $"Data Source={Path.Combine(root, "umbraco.db")};Pooling=False",
            ["ConnectionStrings:umbracoDbDSN_ProviderName"] = "Microsoft.Data.Sqlite",
            ["ConnectionStrings:AppBlocksDb"] = database.AccountConnection,
            ["ConnectionStrings:AppBlocksDb_ProviderName"] = "Microsoft.Data.SqlClient",
            ["AppBlocks:DataRoot"] = root,
            ["AppBlocks:ReportAdministratorGroups:0"] = "admin",
            ["Umbraco:CMS:Global:Id"] = Guid.NewGuid().ToString(), ["Umbraco:CMS:Global:UseHttps"] = "false",
            ["Umbraco:CMS:ModelsBuilder:ModelsMode"] = "Nothing",
            ["Umbraco:CMS:TypeFinder:AdditionalAssemblyExclusionEntries:0"] = "xunit.",
            // As a site with the Oracle driver installed sets it, so that Umbraco's startup scan does not load the driver's optional cloud assemblies; see docs/connections.md.
            ["Umbraco:CMS:TypeFinder:AdditionalAssemblyExclusionEntries:1"] = "Oracle.ManagedDataAccess",
            ["Umbraco:CMS:Unattended:InstallUnattended"] = "true", ["Umbraco:CMS:Unattended:UpgradeUnattended"] = "true",
            ["Umbraco:CMS:Unattended:UnattendedUserName"] = "Setup admin",
            ["Umbraco:CMS:Unattended:UnattendedUserEmail"] = AdminEmail,
            ["Umbraco:CMS:Unattended:UnattendedUserPassword"] = password,
        });
        builder.Services.Configure<InstallDefaultDataSettings>(Constants.Configuration.NamedOptions.InstallDefaultData.Languages,
            settings => { settings.InstallData = InstallDefaultDataOption.Values; settings.Values = new HashSet<string> { "en" }; });
        builder.CreateUmbracoBuilder().AddBackOffice().AddWebsite().AddComposers().Build();
        // This project is not a Web SDK one, so nothing generates the application parts Umbraco's API controllers rely on.
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(Umbraco.Cms.Api.Management.Controllers.ManagementApiControllerBase).Assembly)
            .AddApplicationPart(typeof(Umbraco.Cms.Api.Common.Attributes.MapToApiAttribute).Assembly);
        builder.Services.AddScoped<IFormResource, SetupTasks>();
        builder.Services.Replace(ServiceDescriptor.Singleton<IServerRoleAccessor, SingleServerRoleAccessor>());
        // Umbraco routes logging through Serilog, past any provider added to the builder.
        builder.Services.Replace(ServiceDescriptor.Singleton<ILoggerFactory>(new LoggerFactory([logs])));
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
        var site = new SetupSite(app, address, logs, password);
        await site.PublishPageAsync();
        await site.CreateEditorAsync();
        return site;
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }

    public HttpClient Anonymous() => new() { BaseAddress = Address };

    public Task<HttpClient> Administrator() => SignInAsync(AdminEmail);

    public Task<HttpClient> Editor() => SignInAsync(EditorEmail);

    /// <summary>The form's own address for a record, as the runtime asks for it.</summary>
    public static string FormUrl(string key) => $"/api/forms/{PageKey}/{FormKey}?taskId={key}";

    /// <summary>The Attachments block's address for the form's record.</summary>
    public static string FilesUrl(string key) => $"/api/attachments/{PageKey}/{FilesKey}?taskId={key}";

    private async Task PublishPageAsync()
    {
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var types = services.GetRequiredService<IContentTypeService>();
        for (var i = 0; i < 100 && types.Get(AppBlockSchema.FormsContainerAlias) is null; i++) await Task.Delay(100);
        var strings = services.GetRequiredService<IShortStringHelper>();
        var dataTypes = services.GetRequiredService<IDataTypeService>();
        var list = new DataType(services.GetRequiredService<DataEditorCollection>().First(e => e.Alias == "Umbraco.BlockList"), services.GetRequiredService<IConfigurationEditorJsonSerializer>()) {
            Key = AppBlockSchema.Key("setup-site:list"), Name = "Setup page blocks", EditorUiAlias = "Umb.PropertyEditorUi.BlockList",
        };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var configuration = new BlockListConfiguration { Blocks = new[] { AppBlockSchema.Key(AppBlockSchema.FormsContainerAlias), AppBlockSchema.Key("appReport") }
            .Select(key => new BlockListConfiguration.BlockConfiguration { ContentElementTypeKey = key }).ToArray() };
        list.SetConfigurationData(JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(configuration, options), options)!);
        var created = await dataTypes.CreateAsync(list, Constants.Security.SuperUserKey);
        Assert.True(created.Success, created.Status.ToString());
        var pageType = new ContentType(strings, -1) { Key = AppBlockSchema.Key("setup-site:type"), Alias = "setupPage", Name = "Setup page", AllowedAsRoot = true };
        pageType.AddPropertyType(new PropertyType(strings, list) { Alias = "content", Name = "Content" });
        var typed = await types.CreateAsync(pageType, Constants.Security.SuperUserKey);
        Assert.True(typed.Success, typed.Result.ToString());
        var contents = services.GetRequiredService<IContentService>();
#pragma warning disable CS0618
        var page = contents.Create("Setup page", -1, "setupPage");
        page.Key = PageKey;
        page.SetValue("content", Blocks().ToJsonString());
        Assert.True(contents.Save(page).Success);
        var published = contents.Publish(page, ["*"]);
        Assert.True(published.Success, string.Join("; ", published.EventMessages?.GetAll().Select(m => m.Message) ?? []));
#pragma warning restore CS0618
    }

    private static JsonObject Blocks()
    {
        var body = Set("Umbraco.BlockList");
        var source = new JsonObject { ["kind"] = "table", ["connection"] = ConnectionCatalog.ApplicationConnection, ["source"] = "BUSINESS_TASKS" };
        Add(body, "Umbraco.BlockList", ReportKey, AppBlockSchema.Key("appReport"), ("heading", JsonValue.Create("Tasks")), ("dataSource", JsonValue.Create(source.ToJsonString())), ("pageSize", JsonValue.Create(10)));
        var fields = Set("Umbraco.BlockGrid");
        var form = Add(fields, "Umbraco.BlockGrid", FormKey, AppBlockSchema.Key("appForm"), ("heading", JsonValue.Create("Edit task")), ("formInterface", Interface()));
        var title = Add(fields, "Umbraco.BlockGrid", AppBlockSchema.Key("setup-site:title"), AppBlockSchema.Key("appText"), ("field", JsonValue.Create("title")), ("label", JsonValue.Create("Title")));
        var files = Add(fields, "Umbraco.BlockGrid", FilesKey, AppBlockSchema.Key(AppBlockSchema.AttachmentsAlias), ("heading", JsonValue.Create("Files")));
        var layout = fields["layout"]!["Umbraco.BlockGrid"]!.AsArray();
        layout.Remove(title);
        layout.Remove(files);
        using var stream = new ManifestEmbeddedFileProvider(typeof(AppBlocksExtensions).Assembly).GetFileInfo("Schema/DataTypes/ApplicationApplicationBlocks.config").CreateReadStream();
        var grid = JsonNode.Parse(XElement.Load(stream).Element("Config")!.Value)!;
        var formBlock = grid["blocks"]!.AsArray().Single(b => b!["contentElementTypeKey"]!.GetValue<string>() == AppBlockSchema.Key("appForm").ToString())!;
        form["areas"]!.AsArray().Add(new JsonObject { ["key"] = formBlock["areas"]![0]!["key"]!.DeepClone(), ["items"] = new JsonArray(title, files) });
        Add(body, "Umbraco.BlockList", AppBlockSchema.Key("setup-site:container"), AppBlockSchema.Key(AppBlockSchema.FormsContainerAlias), ("forms", JsonValue.Create(fields.ToJsonString())));
        return body;
    }

    private static JsonObject Interface()
    {
        var blocks = Set("Umbraco.SingleBlock");
        Add(blocks, "Umbraco.SingleBlock", AppBlockSchema.Key("setup-site:interface"), AppBlockSchema.Key("appFormInterface"), ("resource", JsonValue.Create(TasksResource)), ("keyParameter", JsonValue.Create("taskId")));
        return blocks;
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

    private async Task CreateEditorAsync()
    {
        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserService>();
        var created = await users.CreateAsync(Constants.Security.SuperUserKey, new UserCreateModel {
            UserName = EditorEmail, Email = EditorEmail, Name = "Setup editor", Kind = UserKind.Default, UserGroupKeys = new HashSet<Guid> { Constants.Security.EditorGroupKey },
        }, approveUser: true);
        Assert.True(created.Success, created.Status.ToString());
        var manager = scope.ServiceProvider.GetRequiredService<IBackOfficeUserManager>();
        var identity = (await manager.FindByIdAsync(created.Result.CreatedUser!.Key.ToString()))!;
        var changed = await manager.ChangePasswordWithResetAsync(identity.Id, await manager.GeneratePasswordResetTokenAsync(identity), password);
        Assert.True(changed.Succeeded, string.Join("; ", changed.Errors.Select(error => error.Description)));
    }

    private async Task<HttpClient> SignInAsync(string username)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = Address };
        const string security = "/umbraco/management/api/v1/security/back-office";
        using var login = await client.PostAsJsonAsync(security + "/login", new { username, password });
        Assert.True(login.IsSuccessStatusCode, "Test sign-in failed: " + login.StatusCode);
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var redirect = new Uri(Address, "/umbraco/oauth_complete").ToString();
        using var authorize = await client.GetAsync(QueryHelpers.AddQueryString(security + "/authorize", new Dictionary<string, string?> {
            ["client_id"] = "umbraco-back-office", ["response_type"] = "code", ["redirect_uri"] = redirect,
            ["code_challenge"] = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), ["code_challenge_method"] = "S256", ["state"] = "setup-tests",
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

    /// <summary>The form's class: the built-in data source engine over the business table, with the restricted login's own rights.</summary>
    private sealed class SetupTasks(DataSourceRuntime runtime, IDataSourceUserResolver users, IEnumerable<IDataSourcePolicy> policies) : IFormResource
    {
        private readonly SqlDataSource records = new(new DataSourceDefinition {
            Key = AppBlockSchema.Key("setup-site:tasks"), Name = "Tasks", Alias = TasksResource,
            Access = DataSourceAccess.NotPublic,
            Connection = ConnectionCatalog.ApplicationConnection, Source = "BUSINESS_TASKS", KeyColumns = ["id"], AllowInsert = true, AllowUpdate = true,
        }, runtime, users, policies);

        public string Alias => records.Alias;
        public string ContractVersion => records.ContractVersion;
        public IReadOnlyList<FormField> Fields => records.Fields;
        public Task<FormResult> InitializeAsync(FormExecutionContext context, CancellationToken ct) => records.InitializeAsync(context, ct);
        public Task<FormResult> LoadAsync(string key, FormExecutionContext context, CancellationToken ct) => records.LoadAsync(key, context, ct);
        public Task<FormResult> CreateAsync(JsonElement input, FormExecutionContext context, CancellationToken ct) => records.CreateAsync(input, context, ct);
        public Task<FormResult> UpdateAsync(string key, JsonElement input, string? version, FormExecutionContext context, CancellationToken ct) => records.UpdateAsync(key, input, version, context, ct);
        public Task<FormResult> DeleteAsync(string key, string? version, FormExecutionContext context, CancellationToken ct) => records.DeleteAsync(key, version, context, ct);
    }
}

/// <summary>Keeps what the site logged, so a test can count how often it said something.</summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<(string Category, LogLevel Level, string Message)> entries = new();

    public IReadOnlyList<(string Category, LogLevel Level, string Message)> Entries => entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, entries);

    public void Dispose() { }

    private sealed class Logger(string category, ConcurrentQueue<(string, LogLevel, string)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => entries.Enqueue((category, logLevel, formatter(state, exception)));
    }
}
