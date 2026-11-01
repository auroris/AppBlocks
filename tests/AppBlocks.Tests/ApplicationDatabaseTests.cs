using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AppBlocks.Databases;
using AppBlocks.DataSources;
using AppBlocks.Forms;

namespace AppBlocks.Tests;

/// <summary>Application databases: schema folders, the version table, once per process, and the engine over them.</summary>
public sealed class ApplicationDatabaseTests
{
    [Fact]
    public async Task Sqlite_scripts_run_statement_by_statement_in_file_order_once_per_process()
    {
        var root = Folder(("sqlite/001-tables.sql", """
            CREATE TABLE IF NOT EXISTS ITEMS (ID INTEGER PRIMARY KEY, NAME TEXT NOT NULL);
            ALTER TABLE ITEMS ADD COLUMN NOTE TEXT NULL; -- a semicolon; inside a comment
            CREATE TABLE IF NOT EXISTS ITEM_LOG (ID INTEGER PRIMARY KEY, ENTRY TEXT NOT NULL);
            /* A trigger body holds semicolons of its own; */
            CREATE TRIGGER IF NOT EXISTS ITEMS_ADDED AFTER INSERT ON ITEMS
            BEGIN
                INSERT INTO ITEM_LOG (ENTRY) VALUES ('added; ' || NEW.NAME);
            END;
            """), ("sqlite/002-seed.sql", "INSERT OR IGNORE INTO ITEMS (ID, NAME, NOTE) VALUES (1, 'First; with a semicolon', 'seeded');"),
            ("sqlite/notes.txt", "Not a script."), ("sqlserver/001-tables.sql", "THIS IS NOT SQLITE"));
        var steps = new List<string>();
        var schema = ApplicationSchema.FromDirectory(root).Then((_, dialect) => steps.Add(dialect));
        var file = Path.Combine(root, "items.db");
        Assert.Equal(Path.GetFullPath(root), schema.Id);

        using (var first = Open(file)) ApplicationDatabase.Ensure(first, schema);
        using (var second = Open(file)) await ApplicationDatabase.EnsureAsync(second, schema);
        Assert.Equal(["sqlite"], steps); // The second ensure in this process was skipped.
        Assert.Equal("seeded", Scalar(file, "SELECT NOTE FROM ITEMS WHERE ID = 1"));
        Assert.Equal(1L, Scalar(file, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name = 'ITEMS_ADDED'"));
        Assert.Equal("added; First; with a semicolon", Scalar(file, "SELECT ENTRY FROM ITEM_LOG"));
        Assert.Equal("001-tables.sql|002-seed.sql", Scalar(file, "SELECT group_concat(SCRIPT, '|') FROM (SELECT SCRIPT FROM APP_SCHEMA ORDER BY SCRIPT)"));
        Assert.Equal(2L, Scalar(file, "SELECT COUNT(*) FROM APP_SCHEMA WHERE length(HASH) = 64 AND APPLIED_AT IS NOT NULL"));

        // A new process runs every script again: the column the ALTER adds is there, which counts as applied.
        var copy = Folder(("sqlite/001-tables.sql", File.ReadAllText(Path.Combine(root, "sqlite", "001-tables.sql"))),
            ("sqlite/002-seed.sql", File.ReadAllText(Path.Combine(root, "sqlite", "002-seed.sql"))));
        using (var again = Open(file)) await ApplicationDatabase.EnsureAsync(again, ApplicationSchema.FromDirectory(copy));
        Assert.Equal(1L, Scalar(file, "SELECT COUNT(*) FROM ITEM_LOG"));
        Assert.Equal(2L, Scalar(file, "SELECT COUNT(*) FROM APP_SCHEMA"));
    }

    [Fact]
    public void A_failing_script_names_itself_leaves_nothing_behind_and_runs_again_once_fixed()
    {
        var root = Folder(("sqlite/001-good.sql", "CREATE TABLE IF NOT EXISTS GOOD (ID INTEGER PRIMARY KEY);"),
            ("sqlite/002-broken.sql", "CREATE TABLE IF NOT EXISTS HALF (ID INTEGER PRIMARY KEY);\nCREATE TABEL NOPE (ID INTEGER);"));
        var steps = 0;
        var schema = ApplicationSchema.FromDirectory(root).Then((_, _) => steps++);
        var file = Path.Combine(root, "broken.db");

        using (var connection = Open(file))
        {
            var error = Assert.Throws<ApplicationSchemaException>(() => ApplicationDatabase.Ensure(connection, schema));
            Assert.Contains("002-broken.sql", error.Message);
            Assert.IsType<SqliteException>(error.InnerException);
        }
        Assert.Equal(0, steps);
        Assert.Equal("001-good.sql", Scalar(file, "SELECT group_concat(SCRIPT) FROM APP_SCHEMA"));
        Assert.Equal(0L, Scalar(file, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'HALF'"));

        File.WriteAllText(Path.Combine(root, "sqlite", "002-broken.sql"), "CREATE TABLE IF NOT EXISTS HALF (ID INTEGER PRIMARY KEY);\nCREATE TABLE IF NOT EXISTS NOPE (ID INTEGER);");
        using (var connection = Open(file)) ApplicationDatabase.Ensure(connection, schema);
        Assert.Equal(1, steps);
        Assert.Equal(2L, Scalar(file, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('HALF', 'NOPE')"));
        Assert.Equal(2L, Scalar(file, "SELECT COUNT(*) FROM APP_SCHEMA"));

        // A duplicate column counts as applied only for ALTER TABLE ... ADD; anywhere else it fails the script.
        var duplicate = Folder(("sqlite/001-twice.sql", "CREATE TABLE TWICE (A TEXT, A TEXT);"));
        using (var connection = Open(file))
            Assert.Contains("001-twice.sql", Assert.Throws<ApplicationSchemaException>(() => ApplicationDatabase.Ensure(connection, ApplicationSchema.FromDirectory(duplicate))).Message);
    }

    [Fact]
    public void The_dialect_comes_from_the_connection_type()
    {
        Assert.Equal("sqlite", ApplicationDatabase.Dialect(new SqliteConnection()));
        Assert.Equal("sqlserver", ApplicationDatabase.Dialect(new SqlConnection()));
        Assert.Contains(nameof(OtherConnection), Assert.Throws<InvalidOperationException>(() => ApplicationDatabase.Dialect(new OtherConnection())).Message);
    }

    [Fact]
    public async Task The_data_source_engine_finds_the_schema_ensured_before_its_first_statement()
    {
        var schema = ApplicationSchema.FromDirectory(Folder(("sqlite/001-notes.sql",
            "CREATE TABLE IF NOT EXISTS NOTES (NOTE_ID INTEGER PRIMARY KEY, BODY TEXT NOT NULL, VERSION INTEGER NOT NULL DEFAULT 1);")));
        var dataRoot = Folder();
        using var provider = Services(dataRoot, schema);
        var file = Path.Combine(dataRoot, "App_Data", "TestDb.sqlite.db");
        Assert.False(File.Exists(file));

        var errors = new List<string>();
        var values = new Dictionary<string, object?> {
            ["alias"] = "notes", ["connection"] = "TestDb", ["source"] = "NOTES", ["keyColumns"] = "NOTE_ID", ["concurrency"] = "VERSION",
            ["allowInsert"] = "1", ["allowUpdate"] = "1",
        };
        var definition = DataSourceDefinition.Read(Guid.NewGuid(), "Notes", alias => values.GetValueOrDefault(alias), picked => picked.Split(','), errors);
        Assert.True(definition is not null, string.Join(" ", errors));
        var source = new SqlDataSource(definition, provider.GetRequiredService<DataSourceRuntime>(), new OneUser(), []);
        Assert.True(source.Problem is null, source.Problem);

        var context = new FormExecutionContext(new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "1")], IdentityConstants.ApplicationScheme)),
            Guid.NewGuid(), Guid.NewGuid(), "1", Guid.NewGuid().ToString());
        var created = await source.CreateAsync(JsonSerializer.SerializeToElement(new { body = "Kept in the application database" }), context, default);
        Assert.True(created.Status == 200, JsonSerializer.Serialize(created.Errors));
        var loaded = await source.LoadAsync(created.Record!.Key!, context, default);
        Assert.Equal("Kept in the application database", loaded.Record!.Values["body"]);
        Assert.Equal("001-notes.sql", Scalar(file, "SELECT SCRIPT FROM APP_SCHEMA"));
    }

    [LocalDbFact]
    public async Task Sql_server_scripts_run_in_go_batches()
    {
        const string master = @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;Connect Timeout=90;TrustServerCertificate=true";
        var database = "app_db_test_" + Guid.NewGuid().ToString("N")[..12];
        ExecuteSqlServer(master, $"CREATE DATABASE [{database}]");
        var connectionString = master + $";Initial Catalog={database};Pooling=false";
        try { await ExerciseSqlServerSchema(connectionString); }
        finally
        {
            SqlConnection.ClearAllPools();
            ExecuteSqlServer(master, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
        }
    }

    internal static async Task ExerciseSqlServerSchema(string connectionString)
    {
        var root = Folder(("sqlserver/001-tables.sql", """
            IF OBJECT_ID(N'dbo.ITEMS', N'U') IS NULL
            CREATE TABLE dbo.ITEMS (ID int IDENTITY(1, 1) NOT NULL PRIMARY KEY, NAME nvarchar(50) NOT NULL);
            GO
            CREATE OR ALTER VIEW dbo.ITEM_NAMES AS SELECT NAME FROM dbo.ITEMS;
              go
            IF COL_LENGTH('dbo.ITEMS', 'NOTE') IS NULL ALTER TABLE dbo.ITEMS ADD NOTE nvarchar(100) NULL;
            """));
        var steps = 0;
        var schema = ApplicationSchema.FromDirectory(root).Then((_, dialect) => { Assert.Equal("sqlserver", dialect); steps++; });
        // Two connection strings to one database are two memo entries, so the second ensure runs every batch again.
        foreach (var name in new[] { "first", "second" })
        {
            await using var connection = new SqlConnection(connectionString + $";Application Name={name}");
            await connection.OpenAsync();
            await ApplicationDatabase.EnsureAsync(connection, schema);
        }
        Assert.Equal(2, steps);
        Assert.Equal(1, ScalarSqlServer(connectionString, "SELECT COUNT(*) FROM sys.views WHERE name = 'ITEM_NAMES'"));
        Assert.Equal(1, ScalarSqlServer(connectionString, "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.ITEMS') AND name = 'NOTE'"));
        Assert.Equal(64L, Convert.ToInt64(ScalarSqlServer(connectionString, "SELECT LEN(HASH) FROM APP_SCHEMA WHERE SCRIPT = '001-tables.sql'")));
    }

    private static ServiceProvider Services(string dataRoot, ApplicationSchema schema)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AppBlocks:DataRoot"] = dataRoot }).Build();
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<IConfiguration>(configuration);
        collection.AddTestConnections(connections => connections.AddApplicationDatabase("TestDb", schema));
        collection.AddSingleton<DataSourceRuntime>();
        return collection.BuildServiceProvider();
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

    private static SqliteConnection Open(string file)
    {
        var connection = new SqliteConnection($"Data Source={file};Pooling=False");
        connection.Open();
        return connection;
    }

    private static object? Scalar(string file, string sql)
    {
        using var connection = Open(file);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void ExecuteSqlServer(string connection, string sql)
    {
        using var database = new SqlConnection(connection);
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 90;
        command.ExecuteNonQuery();
    }

    private static object? ScalarSqlServer(string connection, string sql)
    {
        using var database = new SqlConnection(connection);
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private sealed class OneUser : IDataSourceUserResolver
    {
        public Task<DataSourceUser?> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct)
            => Task.FromResult<DataSourceUser?>(new("1", "S-1-5-21-1", "jsmith", null, [], false));
    }

    private sealed class OtherConnection : System.Data.Common.DbConnection
    {
        [System.Diagnostics.CodeAnalysis.AllowNull] public override string ConnectionString { get; set; } = "";
        public override string Database => "";
        public override string DataSource => "";
        public override string ServerVersion => "";
        public override System.Data.ConnectionState State => System.Data.ConnectionState.Closed;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override System.Data.Common.DbTransaction BeginDbTransaction(System.Data.IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override System.Data.Common.DbCommand CreateDbCommand() => throw new NotSupportedException();
    }
}
