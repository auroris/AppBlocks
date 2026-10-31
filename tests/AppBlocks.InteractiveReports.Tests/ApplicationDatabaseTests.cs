using InteractiveReport.AspNetCore;
using InteractiveReport.Core.Execution;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AppBlocks.Databases;
using AppBlocks.DataSources;

namespace AppBlocks.InteractiveReports.Tests;

/// <summary>A registered application database, as the report engine's connection factory opens it.</summary>
public sealed class ApplicationDatabaseTests
{
    [Fact]
    public void A_registered_database_is_a_sqlite_file_in_the_instance_folder_unless_configured()
    {
        var schema = ApplicationSchema.FromDirectory(Folder(("sqlite/001-things.sql", "CREATE TABLE IF NOT EXISTS THINGS (ID INTEGER PRIMARY KEY, NAME TEXT);")));

        var dataRoot = Folder();
        using (var provider = Services(dataRoot, schema))
        using (var connection = provider.GetRequiredService<IReportConnectionFactory>().CreateConnection("TestDb"))
        {
            var file = Path.Combine(dataRoot, "App_Data", "TestDb.sqlite.db");
            Assert.IsType<SqliteConnection>(connection);
            Assert.False(File.Exists(file));
            connection.Open();
            Assert.True(File.Exists(file));
            Assert.Equal(1L, Scalar(file, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'THINGS'"));
        }

        var elsewhere = Path.Combine(Folder(), "configured.db");
        dataRoot = Folder();
        using (var provider = Services(dataRoot, schema, ("ConnectionStrings:TestDb", $"Data Source={elsewhere}"), ("ConnectionStrings:TestDb_ProviderName", "Microsoft.Data.Sqlite")))
        using (var connection = provider.GetRequiredService<IReportConnectionFactory>().CreateConnection("TestDb"))
        {
            connection.Open();
            Assert.Equal(1L, Scalar(elsewhere, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'THINGS'"));
            Assert.False(File.Exists(Path.Combine(dataRoot, "App_Data", "TestDb.sqlite.db")));
        }

        // A connection string without a provider name is SQL Server, as Umbraco reads its own.
        using (var provider = Services(Folder(), schema, ("ConnectionStrings:TestDb", @"Server=(localdb)\NoSuchInstance;Integrated Security=true")))
        using (var connection = provider.GetRequiredService<IReportConnectionFactory>().CreateConnection("TestDb"))
            Assert.IsType<SqlConnection>(connection);
    }

    private static ServiceProvider Services(string dataRoot, ApplicationSchema schema, params (string Key, string Value)[] settings)
    {
        var values = new Dictionary<string, string?> { ["AppBlocks:DataRoot"] = dataRoot };
        foreach (var (key, value) in settings) values[key] = value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<IConfiguration>(configuration);
        collection.AddInteractiveReports(configuration).AddAppBlocksConnections(collection, connections => connections.AddApplicationDatabase("TestDb", schema));
        collection.AddSingleton<DataSourceRuntime>();
        return collection.AddReportBlockSupport().BuildServiceProvider();
    }

    /// <summary>A new temporary folder holding the given files, by path relative to it.</summary>
    private static string Folder(params (string Path, string Content)[] files)
    {
        var root = Directory.CreateTempSubdirectory("app-db-test-").FullName;
        foreach (var (path, content) in files)
        {
            var target = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content);
        }
        return root;
    }

    private static object? Scalar(string file, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={file};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
