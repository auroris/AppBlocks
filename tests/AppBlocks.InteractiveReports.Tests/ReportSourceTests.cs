using System.Security.Claims;
using System.Text.Json;
using AppBlocks.Connections;
using AppBlocks.InteractiveReports.Reports;
using InteractiveReport.AspNetCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AppBlocks.InteractiveReports.Blocks;
using AppBlocks.InteractiveReports.DataSources;
using AppBlocks.DataSources;
using AppBlocks.Forms;

namespace AppBlocks.InteractiveReports.Tests;

public sealed class ReportSourceTests
{
    [Theory]
    [InlineData("table", "\"Order.Items\"")]
    [InlineData("sql", "SELECT id, title FROM \"Order.Items\" WHERE owner = :USER_KEY AND title = :PARAM.title")]
    public async Task Block_sources_compile_and_query_managed_connections_without_content_nodes(string kind, string sql)
    {
        using var site = new Fixture();
        var errors = new List<string>();
        var key = Guid.NewGuid();
        var report = site.Catalog.ReadBlockReport(key, property => property switch {
            "dataSource" => Selection(kind, sql), "heading" => "Orders", "pageSize" => "15", _ => null,
        }, errors);
        Assert.Empty(errors);
        Assert.NotNull(report?.DataSource);
        Assert.Equal(key, report.DataSource.Key);
        Assert.Equal("Business", report.Definition.Connection);
        Assert.Equal("Orders", report.Definition.Title);
        Assert.Equal(15, report.Definition.DefaultPageSize);
        Assert.False(report.DataSource.AllowsWrites);
        Assert.Contains("{{RowRestriction}}", report.Definition.Sql);
        var parameters = new Dictionary<string, string> { ["title"] = "O'Brien" };
        var result = await site.Queries.QueryAsync(report.DataSource, new(site.User, Guid.NewGuid(), key, "1", Parameters: parameters));
        Assert.Equal(kind == "table" ? 2 : 1, result.Rows.Count);
        if (kind == "sql")
        {
            Assert.Equal("O'Brien", result.Rows[0]["title"]);
            parameters["title"] = "' OR 1=1 --";
            Assert.Empty((await site.Queries.QueryAsync(report.DataSource, new(site.User, Guid.NewGuid(), key, "1", Parameters: parameters))).Rows);
        }
    }

    [Theory]
    [InlineData("{\"kind\":\"sql\",\"connection\":\"Missing\",\"source\":\"SELECT 1\"}")]
    // The report engine can compile this one, but the picker does not offer it.
    [InlineData("{\"kind\":\"sql\",\"connection\":\"ReportOnly\",\"source\":\"SELECT 1\"}")]
    [InlineData("{\"kind\":\"sql\",\"connection\":\"Business\",\"source\":\"DELETE FROM Orders\"}")]
    [InlineData("{\"kind\":\"sql\",\"connection\":\"Business\",\"source\":\"SELECT 1; DELETE FROM Orders\"}")]
    [InlineData("{\"kind\":\"table\",\"connection\":\"Business\",\"source\":\"SELECT 1\"}")]
    [InlineData("{\"kind\":\"sql\",\"connection\":\"Business\",\"source\":\"SELECT :FIELD.id\"}")]
    [InlineData("{\"kind\":\"sql\",\"connection\":\"Business\",\"source\":\"SELECT 1\",\"connectionString\":\"secret\"}")]
    [InlineData("{\"kind\":\"sql\",\"kind\":\"table\",\"connection\":\"Business\",\"source\":\"Orders\"}")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("a-published-source-alias")]
    public void Invalid_sources_are_rejected(string selection)
    {
        using var site = new Fixture();
        var errors = new List<string>();
        Assert.Null(site.Catalog.ReadBlockReport(Guid.NewGuid(), property => property == "dataSource" ? selection : null, errors));
        Assert.NotEmpty(errors);
    }

    [Fact]
    public async Task A_code_registered_connection_passes_the_validation_a_picker_choice_gets()
    {
        using var site = new Fixture();
        var listed = Assert.Single(site.Connections.List(), c => c.Name == "Registered");
        Assert.True(listed.ReadOnly);
        Assert.Equal("sqlite", listed.Provider);
        var table = Assert.Single(await new ConnectionTables(site.Connections).ListAsync("Registered", default));
        Assert.Equal("Registered.Items", table.Name);

        // The picker's own value for that table, then what publishing, rendering and a preview all read.
        var key = Guid.NewGuid();
        var errors = new List<string>();
        var selection = JsonSerializer.Serialize(new { kind = "table", connection = "Registered", source = table.Value });
        var report = site.Catalog.ReadBlockReport(key, property => property == "dataSource" ? selection : null, errors);
        Assert.Empty(errors);
        Assert.Equal("Registered", report!.Definition.Connection);
        Assert.Null(report.Definition.DataSource);
        Assert.DoesNotContain("registered.db", JsonSerializer.Serialize(report.Definition));
        var row = Assert.Single((await site.Queries.QueryAsync(report.DataSource!, new(site.User, Guid.NewGuid(), key, "1"))).Rows);
        Assert.Equal("From code", row["title"]);

        var previews = new ReportSourcePreviews();
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = site.User } };
        var store = new AppReportDefinitionStore(null!, null!, site.Compiler, site.Catalog, new DataSourceReportBindings(site.Runtime, new Users(), http), site.Connections, previews: previews, http: http);
        var preview = await store.Find(previews.Add(report, site.User));
        Assert.Equal("Registered", preview!.Connection);
        Assert.Equal(InteractiveReport.Core.Model.ReportDialect.Sqlite, preview.Dialect);
        Assert.Null(preview.DataSource);
        using var engine = site.Services.GetRequiredService<InteractiveReport.Core.Execution.IReportConnectionFactory>().CreateConnection("Registered");
        Assert.IsType<SqliteConnection>(engine);
    }

    [Fact]
    public async Task Previews_are_private_expire_and_keep_request_bindings_and_access_checks()
    {
        using var site = new Fixture();
        var clock = new Clock();
        var previews = new ReportSourcePreviews(clock);
        var errors = new List<string>();
        var report = site.Catalog.ReadBlockReport(Guid.NewGuid(), property => property == "dataSource"
            ? Selection("sql", "SELECT title FROM \"Order.Items\" WHERE owner = :USER_KEY") : null, errors)!;
        Assert.Empty(errors);
        var name = previews.Add(report, site.User);
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = site.User } };
        var bindings = new DataSourceReportBindings(site.Runtime, new Users(), http);
        var store = new AppReportDefinitionStore(null!, null!, site.Compiler, site.Catalog, bindings, site.Connections, previews: previews, http: http);
        var authorization = await store.FindAuthorization(name);
        Assert.NotNull(authorization);
        var definition = await store.Find(name);
        Assert.NotNull(definition);
        Assert.Equal(ReportAdministratorRequirement.Policy, definition.Authorization?.Policy);
        Assert.Equal(name, definition.SourceName);
        Assert.Equal("reader", bindings.Get(Assert.Single(definition.ContextParams!).Value.Claim!));
        Assert.Null(report.Definition.SourceName); // Binding never mutates the shared preview.

        var access = new DataSourceReportAccess(site.Catalog, site.Queries, http, previews: previews);
        var request = new InteractiveReportRowRestrictionRequest { User = site.User, UserId = "reader", ReportName = name, RequestServices = site.Services };
        Assert.Same(RowRestriction.Unrestricted, await access.RestrictAsync(request, default));
        var refused = new DataSourceReportAccess(site.Catalog, new DataSourceQueries(site.Runtime, new Users(), [new Deny()]), http, previews: previews);
        Assert.Same(RowRestriction.Deny, await refused.RestrictAsync(request, default));
        http.HttpContext.User = Principal("other");
        Assert.Null(await store.FindAuthorization(name));
        Assert.Null(await store.Find(name));
        Assert.Same(RowRestriction.Deny, await access.RestrictAsync(request with { User = http.HttpContext.User }, default));
        http.HttpContext.User = site.User;
        clock.Now = clock.Now.AddMinutes(11);
        Assert.Null(await store.Find(name));
        Assert.Same(RowRestriction.Deny, await access.RestrictAsync(request, default));
    }

    private static string Selection(string kind, string source) => JsonSerializer.Serialize(new { kind, connection = "Business", source });
    private static ClaimsPrincipal Principal(string name) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, name)], "test"));
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Users : IDataSourceUserResolver
    {
        public Task<DataSourceUser?> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct) => Task.FromResult<DataSourceUser?>(
            principal.Identity?.IsAuthenticated == true ? new(principal.FindFirstValue(ClaimTypes.NameIdentifier)!, null, "Reader", null, [], false) : null);
    }
    private sealed class Deny : IDataSourcePolicy
    {
        public ValueTask<bool> IsAllowedAsync(DataSourceRequest request, CancellationToken ct) => ValueTask.FromResult(false);
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string root = Directory.CreateTempSubdirectory("appblocks-report-source-").FullName;
        public ServiceProvider Services { get; }
        public ConnectionCatalog Connections { get; }
        public ReportDefinitionCompiler Compiler { get; }
        public DataSourceRuntime Runtime { get; }
        public DataSourceQueries Queries { get; }
        public PublishedReportCatalog Catalog { get; }
        public ClaimsPrincipal User { get; } = Principal("reader");
        public Fixture()
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
                ["ConnectionStrings:Business"] = "Data Source=" + Path.Combine(root, "business.db") + ";Pooling=False",
                ["ConnectionStrings:Business_ProviderName"] = "sqlite",
            }).Build();
            var registered = new AppBlocksBuilder().AddConnection("Registered", _ => new SqliteConnection("Data Source=" + Path.Combine(root, "registered.db") + ";Pooling=False"));
            var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<IConfiguration>(configuration);
            services.AddInteractiveReports(configuration).AddConnectionsFrom(registered)
                .AddConnection("ReportOnly", _ => new SqliteConnection("Data Source=:memory:"));
            Services = services.BuildServiceProvider();
            Connections = new(configuration, new Environment(root), new EphemeralDataProtectionProvider(), registered, Services);
            using (var connection = Connections.Openable("Business"))
            {
                connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE \"Order.Items\" (id INTEGER PRIMARY KEY, title TEXT NOT NULL, owner TEXT NOT NULL); INSERT INTO \"Order.Items\" VALUES (1, 'O''Brien', 'reader'), (2, 'Other', 'other');";
                command.ExecuteNonQuery();
            }
            using (var connection = Connections.Openable("Registered"))
            {
                connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE \"Registered.Items\" (id INTEGER PRIMARY KEY, title TEXT NOT NULL); INSERT INTO \"Registered.Items\" VALUES (1, 'From code');";
                command.ExecuteNonQuery();
            }
            Compiler = Services.GetRequiredService<ReportDefinitionCompiler>();
            Runtime = new(new ApplicationConnections(Services, registered, Connections), NullLogger<DataSourceRuntime>.Instance, managed: Connections);
            Queries = new(Runtime, new Users(), []);
            Catalog = new(null!, null!, null!, Services.GetRequiredService<IOptionsMonitor<InteractiveReportOptions>>(), configuration,
                Runtime, Compiler, null!, NullLogger<PublishedReportCatalog>.Instance, Connections);
        }
        public void Dispose() { Services.Dispose(); Directory.Delete(root, recursive: true); }
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
}
