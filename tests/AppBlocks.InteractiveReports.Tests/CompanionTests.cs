using System.Text.Json;
using System.Xml.Linq;
using AppBlocks.Connections;
using AppBlocks.Setup;
using InteractiveReport.AspNetCore;
using InteractiveReport.Core.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Security;
using UmbracoAppHost.Blocks;
using UmbracoAppHost.DataSources;

namespace AppBlocks.InteractiveReports.Tests;

public sealed class CompanionTests
{
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

    [Fact]
    public void Registration_is_idempotent_and_connections_work_in_both_directions()
    {
        var root = Directory.CreateTempSubdirectory("appblocks-companion-").FullName;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["ConnectionStrings:umbracoDbDSN"] = "Data Source=" + Path.Combine(root, "cms.db"),
            ["ConnectionStrings:umbracoDbDSN_ProviderName"] = "Microsoft.Data.Sqlite",
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
        using var provider = services.BuildServiceProvider();
        var compiler = provider.GetRequiredService<ReportDefinitionCompiler>();
        var application = provider.GetRequiredService<IApplicationConnectionFactory>();
        foreach (var name in new[] { "Before", "After", "Legacy", "UmbracoDb" }) {
            Assert.Equal(DatabaseDialect.Sqlite, application.Dialect(name));
            using var connection = application.CreateConnection(name);
            Assert.IsType<SqliteConnection>(connection);
            Assert.Equal(ReportDialect.Sqlite, compiler.Compile(name, new ReportDefinition { Connection = name, Sql = "SELECT 1 AS id" }).Dialect);
        }
        Assert.NotNull(provider.GetRequiredService<DataSourceRuntime>());
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
