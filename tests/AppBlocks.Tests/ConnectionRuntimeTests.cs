using AppBlocks.Connections;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using UmbracoAppHost.DataSources;

namespace AppBlocks.Tests;

public sealed class ConnectionRuntimeTests
{
    private const string OracleConnectionString = "User Id=app;Password=oracle-test-secret;Data Source=localhost:1521/FREEPDB1";

    [Theory]
    [InlineData("oracle11g", "oracle11g", DatabaseDialect.Oracle11g)]
    [InlineData(" ORACLE11G ", "oracle11g", DatabaseDialect.Oracle11g)]
    [InlineData("oracle", "oracle", DatabaseDialect.Oracle)]
    [InlineData("Oracle.ManagedDataAccess.Client", "oracle", DatabaseDialect.Oracle)]
    [InlineData("Oracle.ManagedDataAccess.Core", "oracle", DatabaseDialect.Oracle)]
    public void Configured_Oracle_connections_load_the_bundled_driver_and_keep_the_selected_dialect(
        string provider, string canonical, DatabaseDialect dialect)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["ConnectionStrings:Business"] = OracleConnectionString,
            ["ConnectionStrings:Business_ProviderName"] = provider,
        }).Build();
        var catalog = new ConnectionCatalog(config, new Environment(Path.GetTempPath()), new EphemeralDataProtectionProvider());
        using var services = new ServiceCollection().BuildServiceProvider();
        var connections = new ApplicationConnections(services, new AppBlocksBuilder(), catalog);
        Assert.Equal(canonical, Assert.Single(catalog.List(), c => c.Name == "Business").Provider);
        Assert.Equal(dialect, connections.Dialect("Business"));
        using var connection = connections.CreateConnection("Business");
        Assert.IsType<Oracle.ManagedDataAccess.Client.OracleConnection>(connection);
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
        Assert.Equal(OracleConnectionString, connection.ConnectionString);
    }

    [Theory]
    [InlineData("oracle11g", DatabaseDialect.Oracle11g, "oracle", DatabaseDialect.Oracle)]
    [InlineData("oracle", DatabaseDialect.Oracle, "oracle11g", DatabaseDialect.Oracle11g)]
    public async Task Managed_Oracle_versions_round_trip_and_can_change_without_reentering_credentials(
        string first, DatabaseDialect firstDialect, string second, DatabaseDialect secondDialect)
    {
        var root = Directory.CreateTempSubdirectory("appblocks-oracle-").FullName;
        try
        {
            var config = new ConfigurationBuilder().Build();
            var protection = new EphemeralDataProtectionProvider();
            var catalog = new ConnectionCatalog(config, new Environment(root), protection);
            using var services = new ServiceCollection().BuildServiceProvider();
            var connections = new ApplicationConnections(services, new AppBlocksBuilder(), catalog);
            await catalog.SaveAsync(new("Business", first, OracleConnectionString), default);
            Assert.Equal(firstDialect, connections.Dialect("Business"));
            Assert.DoesNotContain("oracle-test-secret", File.ReadAllText(Path.Combine(root, "App_Data", "AppBlocks", "connections.json")));
            var revision = catalog.Revision("Business");
            await catalog.SaveAsync(new("Business", second, null), default);
            Assert.NotEqual(revision, catalog.Revision("Business"));
            Assert.Equal(secondDialect, connections.Dialect("Business"));
            var reopened = new ConnectionCatalog(config, new Environment(root), protection);
            Assert.Equal(second, Assert.Single(reopened.List(), c => c.Name == "Business").Provider);
            using var connection = reopened.Openable("Business");
            Assert.IsType<Oracle.ManagedDataAccess.Client.OracleConnection>(connection);
            Assert.Equal(OracleConnectionString, connection.ConnectionString);
            await Assert.ThrowsAsync<ArgumentException>(() => catalog.SaveAsync(new("Business", "sqlServer", null), default));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Detected_Oracle_version_is_cached_only_for_the_current_managed_connection_revision()
    {
        var root = Directory.CreateTempSubdirectory("appblocks-oracle-").FullName;
        try
        {
            var config = new ConfigurationBuilder().Build();
            var catalog = new ConnectionCatalog(config, new Environment(root), new EphemeralDataProtectionProvider());
            using var services = new ServiceCollection().BuildServiceProvider();
            var connections = new ApplicationConnections(services, new AppBlocksBuilder(), catalog);
            await catalog.SaveAsync(new("Business", "oracle", OracleConnectionString), default);
            connections.SetDetectedDialect("Business", DatabaseDialect.Oracle11g);
            Assert.Equal(DatabaseDialect.Oracle11g, connections.Dialect("business"));
            await catalog.SaveAsync(new("Business", "oracle", OracleConnectionString.Replace("localhost", "new-server")), default);
            Assert.Equal(DatabaseDialect.Oracle, connections.Dialect("Business"));
            connections.SetDetectedDialect("Business", DatabaseDialect.Oracle11g);
            await catalog.SaveAsync(new("Business", "sqlite", "Data Source=:memory:"), default);
            Assert.Equal(DatabaseDialect.Sqlite, connections.Dialect("Business"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Configuration_changes_invalidate_detection_and_plans_even_when_only_the_Oracle_version_changes()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["ConnectionStrings:Business"] = OracleConnectionString,
            ["ConnectionStrings:Business_ProviderName"] = "oracle11g",
        }).Build();
        var catalog = new ConnectionCatalog(config, new Environment(Path.GetTempPath()), new EphemeralDataProtectionProvider());
        using var services = new ServiceCollection().BuildServiceProvider();
        var connections = new ApplicationConnections(services, new AppBlocksBuilder(), catalog);
        var revision = catalog.Revision("Business");
        connections.SetDetectedDialect("Business", DatabaseDialect.Oracle11g);
        config["ConnectionStrings:Business_ProviderName"] = "oracle";
        Assert.NotEqual(revision, catalog.Revision("Business"));
        Assert.Equal(DatabaseDialect.Oracle, connections.Dialect("Business"));
        connections.SetDetectedDialect("Business", DatabaseDialect.Oracle11g);
        config["ConnectionStrings:Business"] = OracleConnectionString.Replace("localhost", "new-server");
        Assert.Equal(DatabaseDialect.Oracle, connections.Dialect("Business"));
    }

    private sealed class Environment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "AppBlocks.Tests";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public async Task Forms_can_inspect_and_query_a_database_without_the_reports_companion()
    {
        var root = Directory.CreateTempSubdirectory("appblocks-core-").FullName;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["ConnectionStrings:umbracoDbDSN"] = "Data Source=" + Path.Combine(root, "cms.db"),
            ["ConnectionStrings:umbracoDbDSN_ProviderName"] = "Microsoft.Data.Sqlite",
        }).Build();
        var catalog = new ConnectionCatalog(config, new Environment(root), new EphemeralDataProtectionProvider());
        using var services = new ServiceCollection().BuildServiceProvider();
        var builder = new AppBlocksBuilder().AddConnection("Business", _ => new SqliteConnection("Data Source=" + Path.Combine(root, "business.db")));
        var connections = new ApplicationConnections(services, builder, catalog);
        Assert.Equal(DatabaseDialect.Sqlite, connections.Dialect("UmbracoDb"));
        Assert.Equal(DatabaseDialect.Sqlite, connections.Dialect("Business"));
        await using (var connection = connections.CreateConnection(ConnectionCatalog.ApplicationConnection)) {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Tasks (id INTEGER PRIMARY KEY, title TEXT NOT NULL); INSERT INTO Tasks VALUES (1, 'Base forms work');";
            await command.ExecuteNonQueryAsync();
        }
        var runtime = new DataSourceRuntime(connections, NullLogger<DataSourceRuntime>.Instance, managed: catalog);
        var errors = new List<string>();
        var values = new Dictionary<string, object?> { ["alias"] = "tasks", ["source"] = "Tasks", ["keyColumns"] = "id" };
        var definition = DataSourceDefinition.Read(Guid.NewGuid(), "Tasks", key => values.GetValueOrDefault(key), _ => [], errors)!;
        Assert.Empty(errors);
        Assert.Equal(ConnectionCatalog.ApplicationConnection, definition.Connection);
        Assert.Empty(runtime.Validate(definition));
        Assert.Contains(runtime.Plan(definition).Plan!.Columns, c => c.Column == "title");
        await using var opened = await runtime.OpenAsync(definition.Connection, default);
        using var read = opened.CreateCommand();
        read.CommandText = "SELECT title FROM Tasks WHERE id = 1";
        Assert.Equal("Base forms work", await read.ExecuteScalarAsync());
        Assert.Equal(Path.Combine(root, ConnectionCatalog.ApplicationDatabaseFile), opened.DataSource);
        read.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", await read.ExecuteScalarAsync());
        read.CommandText = "PRAGMA foreign_keys";
        Assert.Equal(1L, await read.ExecuteScalarAsync());
        Assert.False(File.Exists(Path.Combine(root, "cms.db")));
        Assert.Throws<InvalidOperationException>(() => connections.CreateConnection("Missing"));
    }

    [Theory]
    [InlineData("absolute")]
    [InlineData("relative")]
    [InlineData("uri")]
    [InlineData("data-directory")]
    public void Default_files_are_beside_the_resolved_Umbraco_SQLite_file(string kind)
    {
        var root = Directory.CreateTempSubdirectory("appblocks-path-").FullName;
        var cms = Path.Combine(root, "database folder", "cms.db");
        var source = kind switch
        {
            "relative" => Path.GetRelativePath(System.Environment.CurrentDirectory, cms),
            "uri" => new Uri(cms).AbsoluteUri,
            "data-directory" => "|DataDirectory|/instance/cms.db",
            _ => cms,
        };
        if (kind == "data-directory") cms = Path.GetFullPath(Path.Combine(
            AppDomain.CurrentDomain.GetData("DataDirectory") as string ?? AppContext.BaseDirectory, "instance", "cms.db"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["ConnectionStrings:umbracoDbDSN"] = new SqliteConnectionStringBuilder { DataSource = source }.ToString(),
            ["ConnectionStrings:umbracoDbDSN_ProviderName"] = "Microsoft.Data.Sqlite",
            ["AppBlocks:DataRoot"] = Path.Combine(root, "different-data-root"),
        }).Build();
        var catalog = new ConnectionCatalog(config, new Environment(root), new EphemeralDataProtectionProvider());
        foreach (var (name, file) in new[] { (ConnectionCatalog.ApplicationConnection, ConnectionCatalog.ApplicationDatabaseFile),
            (ConnectionCatalog.AttachmentsConnection, ConnectionCatalog.AttachmentsDatabaseFile) })
        {
            var material = catalog.Find(name)!;
            Assert.Equal(Path.Combine(Path.GetDirectoryName(cms)!, file), new SqliteConnectionStringBuilder(material.Value).DataSource);
        }
    }

    [Theory]
    [InlineData("sqlite", "Data Source=:memory:")]
    [InlineData("sqlServer", "Server=localhost;Database=cms")]
    public void Non_file_CMS_databases_use_the_instance_data_folder_and_allow_explicit_overrides(string provider, string cms)
    {
        var root = Directory.CreateTempSubdirectory("appblocks-path-").FullName;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["ConnectionStrings:umbracoDbDSN"] = cms,
            ["ConnectionStrings:umbracoDbDSN_ProviderName"] = provider,
            ["AppBlocks:DataRoot"] = Path.Combine(root, "instance"),
        }).Build();
        var catalog = new ConnectionCatalog(config, new Environment(root), new EphemeralDataProtectionProvider());
        Assert.Equal(Path.Combine(root, "instance", "App_Data", ConnectionCatalog.ApplicationDatabaseFile),
            new SqliteConnectionStringBuilder(catalog.Find(ConnectionCatalog.ApplicationConnection)!.Value).DataSource);
        config["ConnectionStrings:AppBlocksDb"] = "Server=custom;Database=application";
        config["ConnectionStrings:AppBlocksDb_ProviderName"] = "Microsoft.Data.SqlClient";
        using var custom = catalog.Openable(ConnectionCatalog.ApplicationConnection);
        Assert.IsType<Microsoft.Data.SqlClient.SqlConnection>(custom);
        Assert.Contains("Database=application", custom.ConnectionString);
    }
}
