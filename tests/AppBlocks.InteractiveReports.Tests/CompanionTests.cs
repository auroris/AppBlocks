using System.Text.Json;
using System.Xml.Linq;
using AppBlocks.Connections;
using AppBlocks.Setup;
using InteractiveReport.AspNetCore;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Security;
using UmbracoAppHost.Blocks;
using UmbracoAppHost.Attachments;
using UmbracoAppHost.DataSources;

namespace AppBlocks.InteractiveReports.Tests;

public sealed class CompanionTests
{
    [Theory]
    [InlineData("oracle11g", ReportDialect.Oracle11g)]
    [InlineData("oracle", ReportDialect.Oracle)]
    public async Task Managed_Oracle_connections_compile_with_the_selected_report_dialect(string token, ReportDialect dialect)
    {
        var root = Directory.CreateTempSubdirectory("appblocks-oracle-reports-").FullName;
        try
        {
            var configuration = new ConfigurationBuilder().Build();
            var catalog = new ConnectionCatalog(configuration, new Environment(root), new EphemeralDataProtectionProvider());
            await catalog.SaveAsync(new("Business", token, "User Id=app;Password=test-secret;Data Source=localhost:1521/FREEPDB1"), default);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddInteractiveReports(configuration);
            using var provider = services.BuildServiceProvider();
            var compiler = provider.GetRequiredService<ReportDefinitionCompiler>();
            var original = new ReportDefinition { Connection = "Business", Sql = "SELECT ID, TITLE FROM ORDERS" };
            var report = compiler.CompileManaged("orders", original, catalog);
            Assert.Equal(dialect, report.Dialect);
            Assert.Null(report.DataSource);
            Assert.Null(report.Provider);
            Assert.DoesNotContain("test-secret", JsonSerializer.Serialize(report));
            Assert.Equal("Business", original.Connection);
            Assert.Null(original.Dialect);
            using var connection = provider.GetRequiredService<InteractiveReport.Core.Execution.IReportConnectionFactory>().CreateConnection(report.Connection);
            Assert.IsType<Oracle.ManagedDataAccess.Client.OracleConnection>(connection);

            await catalog.SaveAsync(new("Business", token == "oracle11g" ? "oracle" : "oracle11g", null), default);
            var changed = compiler.CompileManaged("orders", original, catalog);
            Assert.Equal(dialect == ReportDialect.Oracle11g ? ReportDialect.Oracle : ReportDialect.Oracle11g, changed.Dialect);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class Environment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "AppBlocks.InteractiveReports.Tests";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public void Companion_owns_report_schema_views_and_editors_and_references_AppBlocks()
    {
        var assembly = typeof(InteractiveReportsExtensions).Assembly;
        Assert.Contains(assembly.GetReferencedAssemblies(), a => a.Name == "AppBlocks");
        var files = new ManifestEmbeddedFileProvider(assembly);
        using var stream = files.GetFileInfo("Schema/ContentTypes/appreport.config").CreateReadStream();
        var report = XElement.Load(stream);
        Assert.Equal(AppBlockSchema.Key("appReport"), Guid.Parse(report.Attribute("Key")!.Value));
        Assert.Contains(report.Descendants("GenericProperty"), p => p.Element("Alias")!.Value == "dataSource");
        Assert.DoesNotContain(report.Descendants("GenericProperty"), p => p.Element("Alias")!.Value is "reportName" or "sql");
        Assert.True(files.GetFileInfo("Views/Partials/blockgrid/Components/appReport.cshtml").Exists);
        Assert.True(files.GetFileInfo("Views/Partials/blocklist/Components/appReport.cshtml").Exists);
        Assert.True(files.GetFileInfo("Views/Partials/ApplicationBlocks/InteractiveReport.cshtml").Exists);
        Assert.True(files.GetFileInfo("Schema/DataTypes/ApplicationReportStartingView.config").Exists);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Registration_shares_application_storage_and_respects_explicit_store_overrides(bool separateStores)
    {
        var root = Directory.CreateTempSubdirectory("appblocks-companion-").FullName;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["ConnectionStrings:umbracoDbDSN"] = "Data Source=" + Path.Combine(root, "cms.db"),
            ["ConnectionStrings:umbracoDbDSN_ProviderName"] = "Microsoft.Data.Sqlite",
            ["Attachments:Connection"] = separateStores ? "AttachmentsDb" : null,
            ["InteractiveReport:SavedReports:DataSource"] = separateStores ? "CustomReports" : null,
            ["ConnectionStrings:CustomReports"] = "Data Source=" + Path.Combine(root, "custom-reports.db"),
            ["ConnectionStrings:CustomReports_ProviderName"] = "Microsoft.Data.Sqlite",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization();
        services.AddHttpContextAccessor();
        services.AddSingleton<IConfiguration>(configuration);
        var environment = new Environment(root);
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddSingleton<IPublicAccessChecker>(_ => null!); // Only the registration is needed here.
        var assemblies = new DefaultUmbracoAssemblyProvider(typeof(CompanionTests).Assembly, NullLoggerFactory.Instance);
        var finder = new TypeFinder(NullLogger<TypeFinder>.Instance, assemblies, null, null);
        var loader = new TypeLoader(finder, NullLogger<TypeLoader>.Instance, [typeof(UmbracoBuilder).Assembly]);
        var builder = new UmbracoBuilder(services, configuration, loader);
        builder.AddAppBlocks(connections => connections.AddConnection("Before", _ => new SqliteConnection("Data Source=:memory:")));
        Assert.DoesNotContain(services, d => d.ServiceType.Namespace?.StartsWith("InteractiveReport") == true);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IAppBlockPlugin));
        builder.AddAppBlocksInteractiveReports(reports => reports.AddConnection("Legacy", _ => new SqliteConnection("Data Source=:memory:")));
        var count = services.Count;
        builder.AddAppBlocksInteractiveReports();
        builder.AddAppBlocks(connections => connections.AddConnection("After", _ => new SqliteConnection("Data Source=:memory:")));
        Assert.Equal(count, services.Count);
        Assert.Equal(2, services.Count(d => d.ServiceType == typeof(AppBlocksSchema)));
        Assert.Single(services, d => d.ServiceType == typeof(IAppBlockPlugin));
        // This storage fixture has no running Umbraco host or virtual-path URL rewriting.
        services.Remove(services.Single(d => d.ImplementationType == typeof(UmbracoAppHost.Reports.ReportLinksUnderVirtualPath)));
        using var provider = services.BuildServiceProvider();
        var compiler = provider.GetRequiredService<ReportDefinitionCompiler>();
        var application = provider.GetRequiredService<IApplicationConnectionFactory>();
        foreach (var name in new[] { "Before", "After", "Legacy", "UmbracoDb", "AppBlocksDb", "AttachmentsDb" }) {
            Assert.Equal(DatabaseDialect.Sqlite, application.Dialect(name));
            using var connection = application.CreateConnection(name);
            Assert.IsType<SqliteConnection>(connection);
            Assert.Equal(ReportDialect.Sqlite, compiler.Compile(name, new ReportDefinition { Connection = name, Sql = "SELECT 1 AS id" }).Dialect);
        }
        Assert.NotNull(provider.GetRequiredService<DataSourceRuntime>());

        // Hold the CMS write lock throughout application writes: separate files must not compete for it.
        await using var cms = application.CreateConnection("UmbracoDb");
        await cms.OpenAsync();
        await using var cmsTransaction = await cms.BeginTransactionAsync();
        using var cmsCommand = cms.CreateCommand();
        cmsCommand.Transaction = cmsTransaction;
        cmsCommand.CommandText = "CREATE TABLE CmsOnly (id INTEGER PRIMARY KEY)";
        await cmsCommand.ExecuteNonQueryAsync();
        await using (var data = application.CreateConnection("AppBlocksDb"))
        {
            await data.OpenAsync();
            Assert.Equal(Path.Combine(root, "AppBlocks.sqlite.db"), data.DataSource);
            using var command = data.CreateCommand();
            command.CommandText = "CREATE TABLE Tasks (id INTEGER PRIMARY KEY, title TEXT); INSERT INTO Tasks VALUES (1, 'Application data')";
            await command.ExecuteNonQueryAsync();
        }
        var saved = provider.GetRequiredService<ISavedReportStore>();
        await saved.Create(new SavedReport { ReportName = "tasks", Title = "My tasks", Owner = "test", StateJson = "{}" });
        Assert.Equal("My tasks", Assert.Single(await saved.ListAll()).Title);
        using var scope = provider.CreateScope();
        var uploads = scope.ServiceProvider.GetRequiredService<AttachmentStore>();
        var owner = new AttachmentOwner("tasks", "1");
        var file = await uploads.AddAsync(owner, new("note.txt", "text/plain", [1, 2, 3]), null, null, null, "test", default);
        Assert.Equal(new byte[] { 1, 2, 3 }, (await uploads.ReadAsync(owner, file.Id, default))!.Bytes);
        Assert.Equal(separateStores ? "AttachmentsDb" : "AppBlocksDb", provider.GetRequiredService<IOptions<AttachmentSettings>>().Value.ConnectionName);
        var reportOptions = provider.GetRequiredService<IOptions<InteractiveReportOptions>>().Value;
        Assert.Equal(separateStores ? "CustomReports" : null, reportOptions.SavedReports.DataSource);
        Assert.Equal(separateStores ? null : "AppBlocksDb", reportOptions.SavedReports.Connection);
        await using (var data = application.CreateConnection("AppBlocksDb"))
        {
            await data.OpenAsync();
            using var command = data.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name IN ('Tasks', 'IR_SAVED_REPORTS', 'APP_ATTACHMENT', 'APP_ATTACHMENT_LINK')";
            Assert.Equal(separateStores ? 1L : 4L, await command.ExecuteScalarAsync());
        }
        Assert.Equal(separateStores, File.Exists(Path.Combine(root, "Attachments.sqlite.db")));
        Assert.Equal(separateStores, File.Exists(Path.Combine(root, "custom-reports.db")));
        cmsCommand.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%'";
        Assert.Equal(1L, await cmsCommand.ExecuteScalarAsync());
    }

    [Fact]
    public void A_plugin_participates_in_block_validation_without_changing_the_core_validator()
    {
        var plugin = new TestBlock();
        var key = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new {
            contentData = new[] { new { key, contentTypeKey = AppBlockSchema.Key(plugin.Alias), values = Array.Empty<object>() } },
            layout = new Dictionary<string, object> { ["Umbraco.BlockList"] = new[] { new { contentKey = key } } },
        });
        var validator = new AppBlocksValidator(null!, plugins: [plugin]);
        Assert.Contains("Companion validation reached", validator.ValidateEmbedded(json));
    }

    private sealed class TestBlock : IAppBlockPlugin
    {
        public string Alias => "testCompanion";
        public string ViewPath => "test.cshtml";
        public bool RequiresUser => true;
        public void Validate(JsonElement block, List<string> errors) => errors.Add("Companion validation reached");
    }
}
