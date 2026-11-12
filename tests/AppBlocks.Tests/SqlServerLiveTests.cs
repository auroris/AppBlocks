using AppBlocks.Connections;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AppBlocks.Tests;

/// <summary>Runs the existing SQL Server integration scenarios against an opt-in, disposable database.</summary>
public sealed class SqlServerLiveTests(ITestOutputHelper output)
{
    [Fact(Explicit = true)]
    public Task Forms_support_generated_keys_triggers_concurrency_and_row_restrictions()
        => Run(SqlServerDataSourceTests.ExerciseEngine,
            ("TRIGGER", "ORDERS_AUDIT"), ("TABLE", "NOTES"), ("TABLE", "ORDERS"), ("TABLE", "ORDER_AUDIT"));

    [Fact(Explicit = true)]
    public Task Application_schema_runs_go_batches_and_is_idempotent()
        => Run(ApplicationDatabaseTests.ExerciseSqlServerSchema,
            ("VIEW", "ITEM_NAMES"), ("TABLE", "ITEMS"), ("TABLE", "APP_SCHEMA"));

    [Fact(Explicit = true)]
    public Task Attachments_support_uploads_renames_owner_isolation_removal_and_purge()
        => Run(connection => AttachmentStoreTests.Exercise(connection, text => new SqlConnection(text)),
            ("TABLE", "APP_ATTACHMENT_LINK"), ("TABLE", "APP_ATTACHMENT"), ("TABLE", "APP_SCHEMA"));

    private async Task Run(Func<string, Task> scenario, params (string Kind, string Name)[] objects)
    {
        var supplied = Environment.GetEnvironmentVariable("APPBLOCKS_SQLSERVER_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(supplied), "Set APPBLOCKS_SQLSERVER_CONNECTION to a disposable SQL Server database with db_owner access.");
        var builder = new SqlConnectionStringBuilder(supplied) { Pooling = false, ConnectTimeout = 10 };
        Assert.False(string.IsNullOrWhiteSpace(builder.InitialCatalog), "Specify the test database explicitly.");
        Assert.DoesNotContain(builder.InitialCatalog, new[] { "master", "model", "msdb", "tempdb" }, StringComparer.OrdinalIgnoreCase);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["ConnectionStrings:SqlServerLive"] = builder.ConnectionString,
            ["ConnectionStrings:SqlServerLive_ProviderName"] = "sqlServer",
        }).Build();
        var collection = new ServiceCollection();
        collection.AddSingleton<IConfiguration>(configuration);
        collection.AddTestConnections(_ => { });
        using var services = collection.BuildServiceProvider();
        var catalog = services.GetRequiredService<ConnectionCatalog>();
        await catalog.TestAsync(new("SqlServerLive", "sqlServer", null), TestContext.Current.CancellationToken);
        await using var database = catalog.Openable("SqlServerLive");
        await database.OpenAsync(TestContext.Current.CancellationToken);
        output.WriteLine($"SQL Server {database.ServerVersion}; database {database.Database}");

        // Check every name before running or cleaning anything. Existing application objects must stay untouched.
        foreach (var (_, name) in objects)
        {
            await using var check = database.CreateCommand();
            check.CommandText = "SELECT OBJECT_ID(@name)";
            var parameter = check.CreateParameter();
            parameter.ParameterName = "@name";
            parameter.Value = "dbo." + name;
            check.Parameters.Add(parameter);
            Assert.True(await check.ExecuteScalarAsync() is DBNull, $"dbo.{name} already exists; use an empty test database. No objects were changed.");
        }

        try { await scenario(builder.ConnectionString); }
        finally
        {
            var failures = new List<Exception>();
            foreach (var (kind, name) in objects)
            {
                try
                {
                    await using var drop = database.CreateCommand();
                    drop.CommandTimeout = 30;
                    drop.CommandText = $"DROP {kind} IF EXISTS [dbo].[{name}]";
                    await drop.ExecuteNonQueryAsync();
                }
                catch (Exception error) { failures.Add(error); }
            }
            if (failures.Count > 0) throw new AggregateException("Could not remove all SQL Server live-test objects.", failures);
        }
    }
}
