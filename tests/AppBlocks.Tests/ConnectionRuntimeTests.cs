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
        await using (var connection = connections.CreateConnection("Business")) {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Tasks (id INTEGER PRIMARY KEY, title TEXT NOT NULL); INSERT INTO Tasks VALUES (1, 'Base forms work');";
            await command.ExecuteNonQueryAsync();
        }
        var runtime = new DataSourceRuntime(connections, NullLogger<DataSourceRuntime>.Instance, managed: catalog);
        var definition = new DataSourceDefinition { Key = Guid.NewGuid(), Name = "Tasks", Alias = "tasks", Connection = "Business", Source = "Tasks", KeyColumns = ["id"] };
        Assert.Empty(runtime.Validate(definition));
        Assert.Contains(runtime.Plan(definition).Plan!.Columns, c => c.Column == "title");
        await using var opened = await runtime.OpenAsync("Business", default);
        using var read = opened.CreateCommand();
        read.CommandText = "SELECT title FROM Tasks WHERE id = 1";
        Assert.Equal("Base forms work", await read.ExecuteScalarAsync());
        Assert.Throws<InvalidOperationException>(() => connections.CreateConnection("Missing"));
    }
}
