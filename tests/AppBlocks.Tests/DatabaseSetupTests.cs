using System.Collections.Concurrent;
using System.Data.Common;
using System.Text;
using AppBlocks.Attachments;
using AppBlocks.Connections;
using AppBlocks.Databases;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AppBlocks.Tests;

/// <summary>
/// The attachment store when its account cannot create its tables, and what the setup screen reads and prints. The SQL Server
/// tests sign in as a real low-privilege login (see <see cref="RestrictedSqlServer"/>). SQLite has no accounts, so a database
/// opened read-only stands in where that makes sense: it proves the store's handling of a refusal, not any permission model.
/// </summary>
public sealed class DatabaseSetupTests
{
    private static readonly AttachmentOwner Order = new("sample.orders", "4");

    [LocalDbFact]
    public async Task An_account_with_no_rights_gets_a_setup_problem_and_creates_nothing()
    {
        await using var database = await RestrictedSqlServer.CreateAsync();
        var site = Site(database.AccountConnection, connection => new SqlConnection(connection));

        var error = await Assert.ThrowsAsync<AttachmentStoreException>(() => site.Store().ListAsync(Order, default));
        Assert.Equal(AttachmentStoreProblem.NotSetUp, error.Problem);
        Assert.True(error.NeedsSetup);
        Assert.Equal(0, await database.TableCount());

        // Every later try is refused the same way, says so once in the log, and the screen reports it without touching anything.
        await Assert.ThrowsAsync<AttachmentStoreException>(() => site.Store().AddAsync(Order, Upload("a.txt"), null, null, null, "m", default));
        await Assert.ThrowsAsync<AttachmentStoreException>(() => site.Store().ReadAsync(Order, 1, default));
        var warning = Assert.Single(site.Logs.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("Database setup", warning.Message);
        Assert.DoesNotContain(database.Password, warning.Message);

        var status = await site.Setup().StatusAsync(default);
        Assert.Equal(DatabaseFeatureState.Action, status.State);
        Assert.Equal("sqlServer", status.Provider);
        Assert.Equal(["missing", "missing"], status.Tables.Select(table => table.State));
        Assert.Contains($"GRANT CREATE TABLE TO [{database.Account}]", status.GrantSql);
        Assert.Equal(0, await database.TableCount());
        SqlConnection.ClearAllPools();
    }

    [LocalDbFact]
    public async Task The_script_makes_every_store_operation_work_for_an_account_with_no_ddl_rights_while_it_runs()
    {
        await using var database = await RestrictedSqlServer.CreateAsync();
        var site = Site(database.AccountConnection, connection => new SqlConnection(connection));
        await Assert.ThrowsAsync<AttachmentStoreException>(() => site.Store().ListAsync(Order, default));

        // The same process, the same store memo: only the database changes.
        await database.Admin((await site.Setup().ScriptAsync(default))!);

        Assert.Equal(0, await database.AccountScalar<int>("SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE TABLE')"));
        await ExerciseStore(site.Store());
        Assert.Contains(site.Logs.Entries, entry => entry.Level == LogLevel.Information && entry.Message.Contains("work again"));
        Assert.Equal(DatabaseFeatureState.Ready, (await site.Setup().StatusAsync(default)).State);
        // The version table is the ensure's bookkeeping, nothing reads it, and this account could not have made one.
        Assert.Equal(0, await database.AdminScalar<int>("SELECT COUNT(*) FROM sys.tables WHERE name = 'APP_SCHEMA'"));
        SqlConnection.ClearAllPools();
    }

    [LocalDbFact]
    public async Task The_printed_grant_is_enough_for_the_store_to_create_its_own_tables_while_it_runs()
    {
        await using var database = await RestrictedSqlServer.CreateAsync();
        var site = Site(database.AccountConnection, connection => new SqlConnection(connection));
        await Assert.ThrowsAsync<AttachmentStoreException>(() => site.Store().ListAsync(Order, default));
        var before = await site.Setup().StatusAsync(default);

        await database.Admin(before.GrantSql!);

        Assert.Equal(DatabaseFeatureState.Automatic, (await site.Setup().StatusAsync(default)).State);
        await ExerciseStore(site.Store());
        Assert.Equal(1, await database.AdminScalar<int>("SELECT COUNT(*) FROM sys.tables WHERE name = 'APP_SCHEMA'"));
        SqlConnection.ClearAllPools();
    }

    [LocalDbFact]
    public async Task An_account_that_can_only_read_the_tables_lists_files_and_is_refused_writes_plainly()
    {
        await using var database = await RestrictedSqlServer.CreateAsync();
        var site = Site(database.AccountConnection, connection => new SqlConnection(connection));
        await database.Admin(ApplicationDatabase.Script(AttachmentStore.Schema, "sqlserver"));
        await database.Admin($"GRANT SELECT ON dbo.APP_ATTACHMENT TO [{database.Account}]; GRANT SELECT ON dbo.APP_ATTACHMENT_LINK TO [{database.Account}];");

        Assert.Empty(await site.Store().ListAsync(Order, default));
        var refused = await Assert.ThrowsAsync<AttachmentStoreException>(() => site.Store().AddAsync(Order, Upload("a.txt"), null, null, null, "m", default));
        Assert.Equal(AttachmentStoreProblem.WritesRefused, refused.Problem);

        // A successful read does not take back what the log said about writes.
        Assert.Empty(await site.Store().ListAsync(Order, default));
        await Assert.ThrowsAsync<AttachmentStoreException>(() => site.Store().AddAsync(Order, Upload("a.txt"), null, null, null, "m", default));
        Assert.Single(site.Logs.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("File uploads are off"));

        var status = await site.Setup().StatusAsync(default);
        Assert.Equal(DatabaseFeatureState.Action, status.State);
        Assert.Equal(["noAccess", "noAccess"], status.Tables.Select(table => table.State));
        SqlConnection.ClearAllPools();
    }

    [LocalDbFact]
    public async Task Status_reads_create_nothing_even_for_an_account_that_could()
    {
        await using var database = await RestrictedSqlServer.CreateAsync();
        // The database administrator's own connection: nothing but the read-only design stops it creating tables.
        var site = Site(database.AdminConnection, connection => new SqlConnection(connection));
        var status = await site.Setup().StatusAsync(default);
        Assert.Equal(DatabaseFeatureState.Automatic, status.State);
        Assert.NotNull(await site.Setup().ScriptAsync(default));
        Assert.Equal(0, await database.TableCount());
    }

    [Fact]
    public async Task Status_reads_create_no_sqlite_file_or_folder_contents_and_a_fresh_site_reads_as_created_on_first_use()
    {
        var root = Directory.CreateTempSubdirectory("setup-sqlite-").FullName;
        var file = Path.Combine(root, "nothing-yet.db");
        var site = Site($"Data Source={file};Pooling=False", connection => new SqliteConnection(connection));

        var status = await site.Setup().StatusAsync(default);
        Assert.Equal(DatabaseFeatureState.Automatic, status.State);
        Assert.Equal("sqlite", status.Provider);
        Assert.Contains("CREATE TABLE IF NOT EXISTS APP_ATTACHMENT", await site.Setup().ScriptAsync(default));
        Assert.Null(status.GrantSql);
        Assert.Empty(Directory.GetFileSystemEntries(root));

        // The store itself still creates it all on first use.
        Assert.Empty(await site.Store().ListAsync(Order, default));
        Assert.Equal(DatabaseFeatureState.Ready, (await site.Setup().StatusAsync(default)).State);
    }

    [Fact]
    public async Task Status_reads_leave_an_empty_sqlite_database_empty()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("setup-sqlite-").FullName, "empty.db");
        await SqliteRun(file, "CREATE TABLE OTHER (ID INTEGER); DROP TABLE OTHER;");
        var site = Site($"Data Source={file};Pooling=False", connection => new SqliteConnection(connection));
        await site.Setup().StatusAsync(default);
        await site.Setup().ScriptAsync(default);
        Assert.Equal(0L, await SqliteScalar(file, "SELECT COUNT(*) FROM sqlite_schema"));
    }

    [Fact]
    public async Task A_read_only_sqlite_database_stands_in_for_no_rights_and_the_script_takes_effect_while_running()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("setup-sqlite-").FullName, "readonly.db");
        await SqliteRun(file, "CREATE TABLE OTHER (ID INTEGER);");
        var site = Site($"Data Source={file};Pooling=False;Mode=ReadOnly", connection => new SqliteConnection(connection));

        var error = await Assert.ThrowsAsync<AttachmentStoreException>(() => site.Store().ListAsync(Order, default));
        Assert.Equal(AttachmentStoreProblem.NotSetUp, error.Problem);
        var status = await site.Setup().StatusAsync(default);
        Assert.Equal(DatabaseFeatureState.Action, status.State);

        // Someone with write access runs the printed script on the file; this site's connection stays read-only.
        await SqliteRun(file, (await site.Setup().ScriptAsync(default))!);
        Assert.Empty(await site.Store().ListAsync(Order, default));
        var refused = await Assert.ThrowsAsync<AttachmentStoreException>(() => site.Store().AddAsync(Order, Upload("a.txt"), null, null, null, "m", default));
        Assert.Equal(AttachmentStoreProblem.WritesRefused, refused.Problem);
        Assert.Equal(DatabaseFeatureState.Action, (await site.Setup().StatusAsync(default)).State);
        // The ensure could not record anything, and needed not.
        Assert.Equal(0L, await SqliteScalar(file, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'APP_SCHEMA'"));
    }

    [Fact]
    public async Task The_sqlite_script_creates_what_the_store_uses()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("setup-sqlite-").FullName, "scripted.db");
        await SqliteRun(file, ApplicationDatabase.Script(AttachmentStore.Schema, "sqlite"));
        await SqliteRun(file, "CREATE TABLE PROOF (ID INTEGER)");
        var site = Site($"Data Source={file};Pooling=False", connection => new SqliteConnection(connection));
        await ExerciseStore(site.Store());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("sqlserver")]
    public void The_script_is_the_installed_schema_file_itself(string dialect)
    {
        var script = ApplicationDatabase.Script(AttachmentStore.Schema, dialect);
        Assert.Equal(new StreamReader(typeof(AttachmentStore).Assembly.GetManifestResourceStream(
            typeof(AttachmentStore).Assembly.GetManifestResourceNames().Single(name => name.EndsWith($"{dialect}.001-attachments.sql", StringComparison.OrdinalIgnoreCase)))!).ReadToEnd(), script);
        foreach (var table in AttachmentStore.Tables) Assert.Contains(table, script);
    }

    [Fact]
    public async Task A_postgres_upload_store_gets_a_notice_and_never_a_connection_or_a_table()
    {
        const string secret = "Host=127.0.0.1;Port=1;Database=nothing;Username=someone;Password=never-shown";
        var site = Site(secret, connection => new NpgsqlConnection(connection));

        var error = await Assert.ThrowsAsync<AttachmentStoreException>(() => site.Store().ListAsync(Order, default));
        Assert.Equal(AttachmentStoreProblem.Unsupported, error.Problem);
        Assert.True(error.NeedsSetup);
        Assert.Contains("SQLite or SQL Server", Assert.Single(site.Logs.Entries, entry => entry.Level == LogLevel.Warning).Message);

        var status = await site.Setup().StatusAsync(default);
        Assert.Equal(DatabaseFeatureState.Unsupported, status.State);
        Assert.Equal("postgres", status.Provider);
        Assert.Contains("Attachments:Connection", status.Summary);
        Assert.False(status.HasScript);
        Assert.Null(status.GrantSql);
        Assert.Null(await site.Setup().ScriptAsync(default));
        Assert.DoesNotContain("never-shown", System.Text.Json.JsonSerializer.Serialize(status));
    }

    [Fact]
    public async Task An_oracle_upload_store_gets_the_same_notice_where_its_driver_is_present()
    {
        DbConnection oracle;
        try { oracle = OracleDriver.Create("Data Source=nowhere;User Id=someone;Password=never-shown"); }
        catch (Exception error) when (error is TypeLoadException or FileNotFoundException or FileLoadException) { Assert.Skip("The Oracle driver is not beside this test run."); throw; }
        await oracle.DisposeAsync();
        var site = Site("Data Source=nowhere;User Id=someone;Password=never-shown", OracleDriver.Create);
        Assert.Equal(AttachmentStoreProblem.Unsupported, (await Assert.ThrowsAsync<AttachmentStoreException>(() => site.Store().ListAsync(Order, default))).Problem);
        Assert.Equal(DatabaseFeatureState.Unsupported, (await site.Setup().StatusAsync(default)).State);
    }

    [Fact]
    public async Task A_connection_that_does_not_open_is_unreachable_rather_than_missing_tables()
    {
        var site = Site(@"Server=(localdb)\NoSuchInstanceForSetupTests;Integrated Security=true;Connect Timeout=1;TrustServerCertificate=true", connection => new SqlConnection(connection));
        var status = await site.Setup().StatusAsync(default);
        Assert.Equal(DatabaseFeatureState.Unreachable, status.State);
        Assert.Contains("Connections tab", status.Summary);
        Assert.DoesNotContain("NoSuchInstanceForSetupTests", System.Text.Json.JsonSerializer.Serialize(status));
        var error = await Assert.ThrowsAsync<AttachmentStoreException>(() => site.Store().ListAsync(Order, default));
        Assert.Equal(AttachmentStoreProblem.Unavailable, error.Problem);
        Assert.False(error.NeedsSetup);
    }

    [Fact]
    public async Task An_unregistered_connection_is_a_setup_problem_on_both_sides()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddTestConnections(_ => { });
        var provider = services.BuildServiceProvider();
        var settings = Options.Create(new AttachmentSettings { Connection = "NotThere" });
        var error = await Assert.ThrowsAsync<AttachmentStoreException>(() => new AttachmentStore(provider.GetRequiredService<IApplicationConnectionFactory>(), settings,
            provider.GetRequiredService<ILogger<AttachmentStore>>()).ListAsync(Order, default));
        Assert.Equal(AttachmentStoreProblem.NotRegistered, error.Problem);
        var status = await new AttachmentDatabaseSetup(provider.GetRequiredService<ConnectionCatalog>(), provider.GetRequiredService<IApplicationConnectionFactory>(), settings).StatusAsync(default);
        Assert.Equal(DatabaseFeatureState.Unreachable, status.State);
    }

    [Theory]
    [InlineData("sqlServer", "GO\n")]
    [InlineData("oracle", "/\n")]
    [InlineData("oracle11g", "/\n")]
    [InlineData("sqlite", ";\n")]
    [InlineData("postgres", ";\n")]
    public void Statements_end_as_each_database_type_tool_expects(string provider, string ending)
    {
        var script = DatabaseSetupGuide.Join(provider, ["ONE", "TWO\n"]);
        Assert.EndsWith(ending, script);
        Assert.Equal(2, script.Split(ending).Length - 1);
    }

    [Theory]
    [InlineData("sqlServer", "[app]")]
    [InlineData("postgres", "\"app\"")]
    [InlineData("oracle", "GRANT CREATE TABLE, CREATE SEQUENCE TO app")]
    [InlineData("oracle11g", "CREATE TRIGGER TO app")]
    public void The_grant_names_the_account_and_what_each_database_type_needs(string provider, string expected)
        => Assert.Contains(expected, DatabaseSetupGuide.Grant(provider, "app").Sql);

    [Fact]
    public void An_unknown_account_is_left_for_a_person_to_fill_in_and_an_owner_is_granted_nothing()
    {
        Assert.Contains(DatabaseSetupGuide.UnknownAccount, DatabaseSetupGuide.Grant("sqlServer", null).Sql);
        Assert.Contains(DatabaseSetupGuide.UnknownAccount, DatabaseSetupGuide.TableGrants("sqlServer", null, ["dbo.T"]));
        Assert.Equal("", DatabaseSetupGuide.TableGrants("sqlServer", "dbo", ["dbo.T"]));
        Assert.Equal("", DatabaseSetupGuide.TableGrants("sqlite", null, ["T"]));
        Assert.Contains("[odd]]name]", DatabaseSetupGuide.Grant("sqlServer", "odd]name").Sql);
    }

    /// <summary>Every operation of the store, so a table the script left out or got wrong fails here.</summary>
    private static async Task ExerciseStore(AttachmentStore store)
    {
        Assert.Empty(await store.ListAsync(Order, default));
        var added = await store.AddAsync(Order, Upload("first.txt"), "Invoice", "Label", "Notes", "member:1", default);
        Assert.Equal("first.txt", Assert.Single(await store.ListAsync(Order, default)).FileName);
        Assert.Equal("first.txt", Encoding.UTF8.GetString((await store.ReadAsync(Order, added.Id, default))!.Bytes));
        var replaced = await store.ReplaceAsync(Order, added.Id, Upload("second.txt"), null, null, null, "member:1", default);
        Assert.Equal([added.Id], replaced!.Revisions.Select(revision => revision.Id));
        Assert.True(await store.RemoveAsync(Order, replaced.Id, "member:1", default));
        Assert.Empty(await store.ListAsync(Order, default));
        Assert.Equal(new AttachmentPurge(1, 2, Encoding.UTF8.GetByteCount("first.txt") + Encoding.UTF8.GetByteCount("second.txt")), await store.PurgeAsync(DateTime.UtcNow.AddDays(1), default));
    }

    private static AttachmentUpload Upload(string name) => new(name, "text/plain", Encoding.UTF8.GetBytes(name));

    private static async Task SqliteRun(string file, string script)
    {
        await using var connection = new SqliteConnection($"Data Source={file};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = script;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> SqliteScalar(string file, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={file};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>One connection under a name of its own, so a static memo of the store is this test's alone.</summary>
    private static Harness Site(string connectionString, Func<string, DbConnection> open)
    {
        var name = "Files" + Guid.NewGuid().ToString("N");
        var logs = new Logs();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(logs));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddTestConnections(connections => connections.AddConnection(name, _ => open(connectionString)));
        var provider = services.BuildServiceProvider();
        var settings = Options.Create(new AttachmentSettings { Connection = name });
        return new(logs, () => new AttachmentStore(provider.GetRequiredService<IApplicationConnectionFactory>(), settings, provider.GetRequiredService<ILogger<AttachmentStore>>()),
            () => new AttachmentDatabaseSetup(provider.GetRequiredService<ConnectionCatalog>(), provider.GetRequiredService<IApplicationConnectionFactory>(), settings));
    }

    private sealed record Harness(Logs Logs, Func<AttachmentStore> Store, Func<AttachmentDatabaseSetup> Setup);

    private sealed class Logs : ILoggerProvider
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> entries = new();
        public IReadOnlyList<(LogLevel Level, string Message)> Entries => entries.ToArray();
        public ILogger CreateLogger(string categoryName) => new Writer(entries);
        public void Dispose() { }
        private sealed class Writer(ConcurrentQueue<(LogLevel, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}
