using AppBlocks.Connections;
using AppBlocks.Databases;
using AppBlocks.InteractiveReports.Connections;
using InteractiveReport.AspNetCore;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

namespace AppBlocks.InteractiveReports.Tests;

/// <summary>The report engine opens an application database through its own factory; the schema comes with it.</summary>
public sealed class ApplicationDatabaseReportTests : IDisposable
{
    private const string Name = "InnDb";
    private readonly string root = Directory.CreateTempSubdirectory("appblocks-app-db-report-").FullName;
    private readonly List<ServiceProvider> providers = [];
    private int steps;

    private string OtherFile => Path.Combine(root, "other.db");
    private string OtherConnection => $"Data Source={OtherFile};Pooling=False";

    public void Dispose()
    {
        foreach (var provider in providers) provider.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void The_report_factory_ensures_a_configured_application_database()
    {
        var site = Site(configured: true);
        using var connection = site.Reports.CreateConnection(Name);
        connection.Open();
        Assert.True(HasTables(OtherFile));
        Assert.Equal(1, steps);
    }

    [Fact]
    public async Task The_report_factory_ensures_a_saved_connection_that_overrides_the_registration()
    {
        var site = Site(configured: false);
        await site.Unregistered.SaveAsync(new(Name, "sqlite", OtherConnection), default);
        using var connection = site.Reports.CreateConnection(Name);
        connection.Open();
        Assert.True(HasTables(OtherFile));
        Assert.False(File.Exists(Path.Combine(root, "App_Data", Name + ".sqlite.db")));
        Assert.Equal(1, steps);
    }

    [Fact]
    public void Both_factories_share_one_ensure_per_process()
    {
        var site = Site(configured: true);
        using (var report = site.Reports.CreateConnection(Name)) report.Open();
        using (var form = site.Connections.CreateConnection(Name)) form.Open();
        Assert.Equal(1, steps);
    }

    /// <summary>A report over a configured connection normally gets a content-addressed name, which no factory for its own name would see.</summary>
    [Fact]
    public void A_report_compiled_over_a_configured_application_database_ensures_it_when_the_engine_opens_it()
    {
        var site = Site(configured: true);
        var compiled = site.Compiler.CompileManaged("things", new ReportDefinition { Connection = Name, Sql = "SELECT ID FROM THINGS" }, site.Catalog);
        Assert.Equal(ReportDialect.Sqlite, compiled.Dialect);
        Assert.Null(compiled.DataSource);
        using var connection = site.Reports.CreateConnection(compiled.Connection);
        connection.Open();
        Assert.True(HasTables(OtherFile));
        Assert.Equal(1, steps);
    }

    [Fact]
    public void A_report_over_any_other_configured_connection_keeps_its_content_addressed_name()
    {
        var site = Site(configured: true, ("ConnectionStrings:Business", OtherConnection), ("ConnectionStrings:Business_ProviderName", "Microsoft.Data.Sqlite"));
        var compiled = site.Compiler.CompileManaged("business", new ReportDefinition { Connection = "Business", Sql = "SELECT 1" }, site.Catalog);
        Assert.StartsWith("__ir:", compiled.Connection);
    }

    private Fixture Site(bool configured, params (string Key, string Value)[] settings)
    {
        var folder = Path.Combine(root, "schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "sqlite"));
        File.WriteAllText(Path.Combine(folder, "sqlite", "001-things.sql"), "CREATE TABLE IF NOT EXISTS THINGS (ID INTEGER PRIMARY KEY);");
        var schema = ApplicationSchema.FromDirectory(folder).Then((_, _) => steps++);
        var values = new Dictionary<string, string?> { ["AppBlocks:DataRoot"] = root };
        if (configured)
        {
            values[$"ConnectionStrings:{Name}"] = OtherConnection;
            values[$"ConnectionStrings:{Name}_ProviderName"] = "Microsoft.Data.Sqlite";
        }
        foreach (var (key, value) in settings) values[key] = value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var environment = new Environment(root);
        var protection = new EphemeralDataProtectionProvider();
        var builder = new AppBlocksBuilder().AddApplicationDatabase(Name, schema);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddInteractiveReports(configuration).AddConnectionsFrom(builder);
        var provider = services.BuildServiceProvider();
        providers.Add(provider);
        var catalog = new ConnectionCatalog(configuration, environment, protection, builder, provider);
        return new(catalog, new(configuration, environment, protection), provider.GetRequiredService<ReportDefinitionCompiler>(),
            new ManagedConnectionFactory(provider.GetRequiredService<IReportConnectionFactory>(), catalog), new ApplicationConnections(provider, builder, catalog));
    }

    private static bool HasTables(string file)
    {
        using var connection = new SqliteConnection($"Data Source={file};Pooling=False;Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('THINGS', 'APP_SCHEMA')";
        return (long)command.ExecuteScalar()! > 0;
    }

    private sealed record Fixture(ConnectionCatalog Catalog, ConnectionCatalog Unregistered, ReportDefinitionCompiler Compiler,
        ManagedConnectionFactory Reports, ApplicationConnections Connections);

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
