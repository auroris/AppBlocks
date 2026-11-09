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
        var name = Path.GetFileName(root);
        Assert.Equal(name, schema.Name);

        using (var first = Open(file)) ApplicationDatabase.Ensure(first, schema);
        using (var second = Open(file)) await ApplicationDatabase.EnsureAsync(second, schema);
        Assert.Equal(["sqlite"], steps); // The second ensure in this process was skipped.
        Assert.Equal("seeded", Scalar(file, "SELECT NOTE FROM ITEMS WHERE ID = 1"));
        Assert.Equal(1L, Scalar(file, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name = 'ITEMS_ADDED'"));
        Assert.Equal("added; First; with a semicolon", Scalar(file, "SELECT ENTRY FROM ITEM_LOG"));
        Assert.Equal($"{name}/001-tables.sql|{name}/002-seed.sql", Scalar(file, "SELECT group_concat(SCRIPT, '|') FROM (SELECT SCRIPT FROM APP_SCHEMA ORDER BY SCRIPT)"));
        Assert.Equal(2L, Scalar(file, "SELECT COUNT(*) FROM APP_SCHEMA WHERE length(HASH) = 64 AND APPLIED_AT IS NOT NULL"));

        // A new process runs every script again: the column the ALTER adds is there, which counts as applied. A copy elsewhere under the same name is the same schema.
        var copy = Folder(("sqlite/001-tables.sql", File.ReadAllText(Path.Combine(root, "sqlite", "001-tables.sql"))),
            ("sqlite/002-seed.sql", File.ReadAllText(Path.Combine(root, "sqlite", "002-seed.sql"))));
        using (var again = Open(file)) await ApplicationDatabase.EnsureAsync(again, ApplicationSchema.FromDirectory(copy, name));
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
        Assert.Equal(Path.GetFileName(root) + "/001-good.sql", Scalar(file, "SELECT group_concat(SCRIPT) FROM APP_SCHEMA"));
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
    public async Task Run_once_scripts_and_steps_run_once_per_database_after_the_ensure_scripts()
    {
        var root = Folder(("sqlite/001-tables.sql", "CREATE TABLE IF NOT EXISTS ITEMS (ID INTEGER PRIMARY KEY, NAME TEXT NOT NULL);"),
            ("sqlite/migrations/002-rename.sql", "UPDATE ITEMS SET NAME = NAME || '!';"),
            ("sqlite/migrations/001-seed.sql", "INSERT INTO ITEMS (ID, NAME) VALUES (1, 'First');\nINSERT INTO ITEMS (ID, NAME) VALUES (2, 'Second');"),
            ("sqlite/migrations/readme.txt", "Not a script."));
        var order = new List<string>();
        ApplicationSchema Schema(string folder) => ApplicationSchema.FromDirectory(folder, "orders")
            .Then((_, _) => order.Add("every start"))
            .Once("count-items", (connection, transaction, dialect) => {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO ITEMS (ID, NAME) VALUES (3, 'From C#')";
                command.ExecuteNonQuery();
                order.Add("once " + dialect);
            });
        var file = Path.Combine(root, "once.db");
        using (var first = Open(file)) await ApplicationDatabase.EnsureAsync(first, Schema(root));
        Assert.Equal(["every start", "once sqlite"], order);
        Assert.Equal("First!|Second!|From C#", Scalar(file, "SELECT group_concat(NAME, '|') FROM (SELECT NAME FROM ITEMS ORDER BY ID)"));
        Assert.Equal("orders/001-tables.sql|orders/migrations/001-seed.sql|orders/migrations/002-rename.sql|orders/once/count-items",
            Scalar(file, "SELECT group_concat(SCRIPT, '|') FROM (SELECT SCRIPT FROM APP_SCHEMA ORDER BY SCRIPT)"));

        // A new process, or a copy of the schema elsewhere, finds the rows and runs only the ensure scripts and the every-start step.
        var copy = Folder(("sqlite/001-tables.sql", File.ReadAllText(Path.Combine(root, "sqlite", "001-tables.sql"))),
            // The same script with Windows line endings and a byte order mark is the same script.
            ("sqlite/migrations/001-seed.sql", "﻿" + File.ReadAllText(Path.Combine(root, "sqlite", "migrations", "001-seed.sql")).Replace("\n", "\r\n")),
            ("sqlite/migrations/002-rename.sql", File.ReadAllText(Path.Combine(root, "sqlite", "migrations", "002-rename.sql"))),
            ("sqlite/migrations/003-more.sql", "INSERT INTO ITEMS (ID, NAME) VALUES (4, 'Later');"));
        order.Clear();
        using (var again = Open(file)) ApplicationDatabase.Ensure(again, Schema(copy));
        Assert.Equal(["every start"], order);
        Assert.Equal("First!|Second!|From C#|Later", Scalar(file, "SELECT group_concat(NAME, '|') FROM (SELECT NAME FROM ITEMS ORDER BY ID)"));
        Assert.Equal(5L, Scalar(file, "SELECT COUNT(*) FROM APP_SCHEMA"));
    }

    [Fact]
    public void A_run_once_script_that_changed_after_it_ran_stops_the_ensure_and_names_itself()
    {
        var root = Folder(("sqlite/migrations/001-table.sql", "CREATE TABLE NOTES (ID INTEGER PRIMARY KEY);"));
        var file = Path.Combine(root, "changed.db");
        using (var connection = Open(file)) ApplicationDatabase.Ensure(connection, ApplicationSchema.FromDirectory(root, "notes"));
        var changed = Folder(("sqlite/migrations/001-table.sql", "CREATE TABLE NOTES (ID INTEGER PRIMARY KEY, BODY TEXT);"),
            ("sqlite/migrations/002-after.sql", "CREATE TABLE AFTER_IT (ID INTEGER PRIMARY KEY);"));
        using (var connection = Open(file))
        {
            var error = Assert.Throws<ApplicationSchemaException>(() => ApplicationDatabase.Ensure(connection, ApplicationSchema.FromDirectory(changed, "notes")));
            Assert.Contains("sqlite/migrations/001-table.sql", error.Message);
            Assert.Contains("has changed since it ran", error.Message);
        }
        // Nothing after it ran, and the original row stands.
        Assert.Equal(0L, Scalar(file, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'AFTER_IT'"));
        Assert.Equal(1L, Scalar(file, "SELECT COUNT(*) FROM APP_SCHEMA"));
    }

    [Fact]
    public void Two_schemas_in_one_database_keep_their_own_rows()
    {
        // Both have a 001-init.sql of their own, ensure and run-once, and a step of the same name.
        ApplicationSchema Schema(string name, string table) => ApplicationSchema.FromDirectory(Folder(
                ($"sqlite/001-init.sql", $"CREATE TABLE IF NOT EXISTS {table} (ID INTEGER PRIMARY KEY);"),
                ($"sqlite/migrations/001-init.sql", $"INSERT INTO {table} (ID) VALUES (1);")), name)
            .Once("seed", (connection, transaction, _) => {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"INSERT INTO {table} (ID) VALUES (2)";
                command.ExecuteNonQuery();
            });
        var file = Path.Combine(Folder(), "shared.db");
        using (var connection = Open(file)) ApplicationDatabase.Ensure(connection, Schema("orders", "ORDERS"));
        using (var connection = Open(file)) ApplicationDatabase.Ensure(connection, Schema("parts", "PARTS"));
        Assert.Equal(2L, Scalar(file, "SELECT COUNT(*) FROM ORDERS"));
        Assert.Equal(2L, Scalar(file, "SELECT COUNT(*) FROM PARTS"));
        Assert.Equal("orders/001-init.sql|orders/migrations/001-init.sql|orders/once/seed|parts/001-init.sql|parts/migrations/001-init.sql|parts/once/seed",
            Scalar(file, "SELECT group_concat(SCRIPT, '|') FROM (SELECT SCRIPT FROM APP_SCHEMA ORDER BY SCRIPT)"));

        // A schema may keep its rows in a table of its own.
        var own = ApplicationSchema.FromDirectory(Folder(("sqlite/migrations/001-notes.sql", "CREATE TABLE NOTES (ID INTEGER PRIMARY KEY);")), "notes").RecordIn("NOTES_VERSION");
        Assert.Equal("NOTES_VERSION", own.Table);
        using (var connection = Open(file)) ApplicationDatabase.Ensure(connection, own);
        Assert.Equal("notes/migrations/001-notes.sql", Scalar(file, "SELECT SCRIPT FROM NOTES_VERSION"));
        Assert.Equal(6L, Scalar(file, "SELECT COUNT(*) FROM APP_SCHEMA"));
        Assert.Throws<ArgumentException>(() => own.RecordIn("NOTES; DROP TABLE ORDERS"));
        Assert.Throws<ArgumentException>(() => ApplicationSchema.FromDirectory(Folder(), "with\nnewline"));
    }

    [Fact]
    public void Rows_written_before_they_carried_the_schema_name_are_taken_over_and_nothing_runs_again()
    {
        var root = Folder(("sqlite/001-tables.sql", "CREATE TABLE IF NOT EXISTS ITEMS (ID INTEGER PRIMARY KEY);"),
            ("sqlite/migrations/001-seed.sql", "INSERT INTO ITEMS (ID) VALUES (1);"));
        var file = Path.Combine(Folder(), "legacy.db");
        var steps = 0;
        ApplicationSchema Schema() => ApplicationSchema.FromDirectory(root, "items").Once("count", (_, _, _) => steps++);
        // The database as the earlier ledger left it: the scripts and the step ran, under keys without the schema's name.
        using (var connection = Open(file))
        using (var command = connection.CreateCommand())
        {
            var seed = ApplicationDatabase.MigrationHash(File.ReadAllBytes(Path.Combine(root, "sqlite", "migrations", "001-seed.sql")));
            var step = ApplicationDatabase.MigrationHash(System.Text.Encoding.UTF8.GetBytes("count"));
            command.CommandText = $"""
                CREATE TABLE APP_SCHEMA (SCRIPT TEXT NOT NULL PRIMARY KEY, HASH TEXT NOT NULL, APPLIED_AT TEXT NOT NULL);
                CREATE TABLE ITEMS (ID INTEGER PRIMARY KEY); INSERT INTO ITEMS (ID) VALUES (1);
                INSERT INTO APP_SCHEMA VALUES ('001-tables.sql', 'X', '2026-01-01'), ('migrations/001-seed.sql', '{seed}', '2026-01-01'),
                    ('once/count', '{step}', '2026-01-01'), ('migrations/999-other.sql', 'ABC', '2026-01-01');
                """;
            command.ExecuteNonQuery();
        }
        using (var connection = Open(file)) ApplicationDatabase.Ensure(connection, Schema());
        Assert.Equal(1L, Scalar(file, "SELECT COUNT(*) FROM ITEMS"));
        Assert.Equal(0, steps);
        // Another schema's earlier row is left for it to take.
        Assert.Equal("items/001-tables.sql|items/migrations/001-seed.sql|items/once/count|migrations/999-other.sql",
            Scalar(file, "SELECT group_concat(SCRIPT, '|') FROM (SELECT SCRIPT FROM APP_SCHEMA ORDER BY SCRIPT)"));
    }

    [Fact]
    public void A_failing_run_once_script_leaves_no_row_and_runs_again_once_fixed()
    {
        var root = Folder(("sqlite/migrations/001-broken.sql", "CREATE TABLE HALF (ID INTEGER PRIMARY KEY);\nCREATE TABEL NOPE (ID INTEGER);"));
        var file = Path.Combine(root, "broken-once.db");
        using (var connection = Open(file))
            Assert.Contains("migrations/001-broken.sql", Assert.Throws<ApplicationSchemaException>(() => ApplicationDatabase.Ensure(connection, ApplicationSchema.FromDirectory(root))).Message);
        Assert.Equal(0L, Scalar(file, "SELECT COUNT(*) FROM APP_SCHEMA"));
        Assert.Equal(0L, Scalar(file, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'HALF'"));
        File.WriteAllText(Path.Combine(root, "sqlite", "migrations", "001-broken.sql"), "CREATE TABLE HALF (ID INTEGER PRIMARY KEY);\nCREATE TABLE NOPE (ID INTEGER);");
        using (var connection = Open(file)) ApplicationDatabase.Ensure(connection, ApplicationSchema.FromDirectory(root, "notes"));
        Assert.Equal(2L, Scalar(file, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('HALF', 'NOPE')"));

        // A step that throws leaves no row either.
        var attempts = 0;
        var flaky = ApplicationSchema.FromDirectory(Folder()).Once("flaky", (_, _, _) => { if (++attempts == 1) throw new InvalidOperationException("Not yet."); });
        using (var connection = Open(file))
            Assert.Contains("flaky", Assert.Throws<ApplicationSchemaException>(() => ApplicationDatabase.Ensure(connection, flaky)).Message);
        using (var connection = Open(file)) ApplicationDatabase.Ensure(connection, flaky);
        Assert.Equal(2, attempts);
        Assert.Equal(1L, Scalar(file, "SELECT COUNT(*) FROM APP_SCHEMA WHERE SCRIPT LIKE '%/once/flaky'"));
    }

    [Fact]
    public void A_run_once_step_needs_a_unique_plain_name()
    {
        var schema = ApplicationSchema.FromDirectory(Folder()).Once("seed-1", (_, _, _) => { });
        Assert.Throws<ArgumentException>(() => schema.Once("seed-1", (_, _, _) => { }));
        Assert.Throws<ArgumentException>(() => schema.Once("", (_, _, _) => { }));
        Assert.Throws<ArgumentException>(() => schema.Once("a/b", (_, _, _) => { }));
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
            ["alias"] = "notes", ["access"] = "Public", ["connection"] = "TestDb", ["source"] = "NOTES", ["keyColumns"] = "NOTE_ID",
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
        Assert.EndsWith("/001-notes.sql", (string)Scalar(file, "SELECT SCRIPT FROM APP_SCHEMA")!);
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
            """), ("sqlserver/migrations/001-seed.sql", "INSERT INTO dbo.ITEMS (NAME) VALUES (N'Seeded once');\nGO\nUPDATE dbo.ITEMS SET NOTE = N'once';"));
        var steps = 0;
        var schema = ApplicationSchema.FromDirectory(root, "items").Then((_, dialect) => { Assert.Equal("sqlserver", dialect); steps++; });
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
        Assert.Equal(64L, Convert.ToInt64(ScalarSqlServer(connectionString, "SELECT LEN(HASH) FROM APP_SCHEMA WHERE SCRIPT = 'items/001-tables.sql'")));
        // The run-once script ran for the first connection string only: the second found its row.
        Assert.Equal(1, ScalarSqlServer(connectionString, "SELECT COUNT(*) FROM dbo.ITEMS WHERE NAME = N'Seeded once' AND NOTE = N'once'"));
        Assert.Equal(1, ScalarSqlServer(connectionString, "SELECT COUNT(*) FROM APP_SCHEMA WHERE SCRIPT = 'items/migrations/001-seed.sql'"));
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
