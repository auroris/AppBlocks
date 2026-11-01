using System.Data;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AppBlocks.DataSources;
using AppBlocks.Forms;

namespace AppBlocks.Tests;

/// <summary>The SQL Server statement builder's text, and the engine against LocalDB when one is installed.</summary>
public sealed class SqlServerDataSourceTests
{
    private static readonly SqlDialect SqlServer = SqlServerDialect.Instance;
    private static readonly TableName Orders = new("dbo", "ORDERS");
    private static readonly TableColumn Id = new("ORDER_ID", "int", false, false, AutoValue: true, Computed: false, RowVersion: false, Precision: 10);

    [Fact]
    public void Names_are_bracketed_and_values_are_markers()
    {
        Assert.Equal("[ORDER]]S]", SqlServer.Quote("ORDER]S"));
        Assert.Equal("[dbo].[Order lines]", SqlServer.Quote(new TableName("dbo", "Order lines")));
        Assert.Equal("@ds0", SqlServer.Marker("ds0"));
        Assert.Equal("SELECT * FROM [dbo].[ORDERS] src WHERE 1 = 0", SqlServer.ProbeText(SqlServer.From(Orders, null)));
        Assert.Equal("SELECT * FROM (\nSELECT * FROM ORDERS -- all\n) src WHERE 1 = 0 AND (\nOWNER = @ds0\n)",
            SqlServer.ProbeText(SqlServer.From(null, "SELECT * FROM ORDERS -- all"), "OWNER = @ds0"));
    }

    [Fact]
    public void New_keys_come_back_through_output_inserted_into_a_table_variable()
    {
        Assert.Equal("""
            DECLARE @inserted TABLE ([ORDER_ID] int);
            INSERT INTO [dbo].[ORDERS] ([CUSTOMER], [AMOUNT]) OUTPUT INSERTED.[ORDER_ID] INTO @inserted VALUES (@ds0, @ds1);
            SELECT [ORDER_ID] FROM @inserted;
            """.ReplaceLineEndings("\n"), SqlServer.Insert(Orders, [("CUSTOMER", "@ds0"), ("AMOUNT", "@ds1")], [Id]));
        Assert.Equal("INSERT INTO [dbo].[ORDERS] ([CUSTOMER]) VALUES (@ds0);", SqlServer.Insert(Orders, [("CUSTOMER", "@ds0")], []));
        var guid = new TableColumn("ID", "uniqueidentifier", false, HasDefault: true, AutoValue: false, Computed: false, RowVersion: false);
        Assert.Equal("DECLARE @inserted TABLE ([ID] uniqueidentifier);\nINSERT INTO [dbo].[ORDERS] OUTPUT INSERTED.[ID] INTO @inserted DEFAULT VALUES;\nSELECT [ID] FROM @inserted;",
            SqlServer.Insert(Orders, [], [guid]));
        Assert.Equal("nvarchar(50)", SqlServerDialect.Declaration(new("C", "nvarchar", true, false, false, false, false, Length: 50)));
        Assert.Equal("nvarchar(max)", SqlServerDialect.Declaration(new("C", "nvarchar", true, false, false, false, false)));
        Assert.Equal("decimal(12, 2)", SqlServerDialect.Declaration(new("C", "decimal", true, false, false, false, false, Precision: 12, Scale: 2)));
        Assert.Equal("datetime2(3)", SqlServerDialect.Declaration(new("C", "datetime2", true, false, false, false, false, Scale: 3)));
    }

    [Fact]
    public void Updates_and_deletes_match_key_version_and_restriction()
    {
        var exists = SqlServer.Exists(SqlServer.From(null, "SELECT o.ORDER_ID AS ID, o.OWNER FROM dbo.ORDERS o"), [("ID", "@ds1")], "OWNER = @ds3");
        Assert.Equal("EXISTS (SELECT 1 FROM (\nSELECT o.ORDER_ID AS ID, o.OWNER FROM dbo.ORDERS o\n) src WHERE src.[ID] = @ds1 AND (\nOWNER = @ds3\n))", exists);
        Assert.Equal("UPDATE [dbo].[ORDERS] SET [CUSTOMER] = @ds0, [VERSION] = [VERSION] + 1 WHERE [ORDER_ID] = @ds1 AND [VERSION] = @ds2 AND " + exists,
            SqlServer.Update(Orders, [("CUSTOMER", "@ds0"), ("VERSION", "[VERSION] + 1")], [("ORDER_ID", "@ds1")], ("VERSION", "@ds2"), exists));
        Assert.Equal("DELETE FROM [dbo].[ORDERS] WHERE [A] = @ds0 AND [B] = @ds1",
            SqlServer.Delete(Orders, [("A", "@ds0"), ("B", "@ds1")], null, null));
        Assert.Equal("SELECT 1 FROM [dbo].[ORDERS] WITH (UPDLOCK, HOLDLOCK, ROWLOCK) WHERE [ORDER_ID] = @ds0", SqlServer.Lock(Orders, [("ORDER_ID", "@ds0")]));
        Assert.Equal("SELECT TOP (2) src.[ORDER_ID], src.[CUSTOMER] FROM [dbo].[ORDERS] src WHERE src.[ORDER_ID] = @ds0 AND (\nOWNER = @ds1\n)",
            SqlServer.Select(SqlServer.From(Orders, null), ["ORDER_ID", "CUSTOMER"], [("ORDER_ID", "@ds0")], "OWNER = @ds1", limit: 2));
        Assert.Equal("SET NOEXEC ON;\nUPDATE X SET A = 1\nSET NOEXEC OFF;", SqlServer.CompileOnly("UPDATE X SET A = 1"));
    }

    [Fact]
    public void Sqlite_builds_the_same_statements_with_returning_and_limit()
    {
        var sqlite = SqliteDialect.Instance;
        var table = new TableName(null, "ORDERS");
        Assert.Equal("\"OR\"\"DERS\"", sqlite.Quote("OR\"DERS"));
        Assert.Equal("INSERT INTO \"ORDERS\" (\"CUSTOMER\") VALUES (@ds0) RETURNING \"ORDER_ID\"", sqlite.Insert(table, [("CUSTOMER", "@ds0")], [Id]));
        Assert.Equal("INSERT INTO \"ORDERS\" DEFAULT VALUES", sqlite.Insert(table, [], []));
        Assert.Equal("SELECT src.\"A\" FROM \"ORDERS\" src WHERE src.\"ORDER_ID\" = @ds0 LIMIT 2", sqlite.Select(sqlite.From(table, null), ["A"], [("ORDER_ID", "@ds0")], null, limit: 2));
        Assert.Null(sqlite.Lock(table, [("ORDER_ID", "@ds0")]));
        Assert.Equal("EXPLAIN DELETE FROM X", sqlite.CompileOnly("DELETE FROM X"));
    }

    [Fact]
    public void Values_are_bound_with_the_column_type()
    {
        TableColumn Column(string type) => new("C", type, true, false, false, false, false);
        Assert.Equal(5, SqlServer.ToDatabase(5m, FieldKinds.Number, Column("int")));
        Assert.Equal((byte)5, SqlServer.ToDatabase(5m, FieldKinds.Number, Column("tinyint")));
        Assert.Throws<DataSourceException>(() => SqlServer.ToDatabase(300m, FieldKinds.Number, Column("tinyint")));
        Assert.Throws<DataSourceException>(() => SqlServer.ToDatabase(1.5m, FieldKinds.Number, Column("int")));
        Assert.Throws<DataSourceException>(() => SqlServer.ToDatabase("not a guid", FieldKinds.Text, Column("uniqueidentifier")));
        Assert.Equal(Guid.Parse("8a8c49f5-0f8e-4d5a-9b51-d9f9e2b0a001"), SqlServer.ToDatabase("8a8c49f5-0f8e-4d5a-9b51-d9f9e2b0a001", FieldKinds.Text, Column("uniqueidentifier")));
        Assert.Equal("1.25", SqlServer.ToDatabase(1.25m, FieldKinds.Number, Column("nvarchar")));
        Assert.Equal((-999.99m, 999.99m), SqlServer.Range(new("C", "decimal", true, false, false, false, false, Precision: 5, Scale: 2)));
        Assert.Equal((0m, 255m), SqlServer.Range(Column("tinyint")));
        Assert.Equal("number", SqlServer.KindOf("money"));
        Assert.Equal("datetime", SqlServer.KindOf("datetimeoffset"));
        Assert.Null(SqlServer.KindOf("varbinary"));
        Assert.Equal("checkbox", SqliteDialect.Instance.KindOf("BOOLEAN"));
        Assert.Equal("date", SqliteDialect.Instance.KindOf("DATE"));
        Assert.Equal("number", SqliteDialect.Instance.KindOf("DECIMAL(10,2)"));
        Assert.Null(SqliteDialect.Instance.KindOf("BLOB"));
    }

    [LocalDbFact]
    public async Task The_engine_reads_and_writes_sql_server_through_localdb()
    {
        const string master = @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;Connect Timeout=90;TrustServerCertificate=true";
        var database = "ds_test_" + Guid.NewGuid().ToString("N")[..12];
        Execute(master, $"CREATE DATABASE [{database}]");
        var connection = master + $";Initial Catalog={database};Pooling=false";
        try { await ExerciseEngine(connection); }
        finally
        {
            SqlConnection.ClearAllPools();
            Execute(master, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
        }
    }

    internal static async Task ExerciseEngine(string connection)
    {
        Execute(connection, """
            CREATE TABLE dbo.ORDERS (ORDER_ID int IDENTITY(1,1) PRIMARY KEY, CUSTOMER nvarchar(200) NOT NULL, AMOUNT decimal(12,2) NOT NULL DEFAULT 0,
                ORDER_DATE date NOT NULL, NOTES nvarchar(max) NULL, DONE bit NOT NULL DEFAULT 0, OWNER nvarchar(100) NULL,
                VERSION int NOT NULL DEFAULT 1, UPDATED_BY nvarchar(100) NULL, UPDATED_AT datetime2 NULL, LEGACY_AT datetime NULL, TOTAL AS (AMOUNT * 2));
            CREATE TABLE dbo.ORDER_AUDIT (ID int IDENTITY PRIMARY KEY, ORDER_ID int NOT NULL);
            """);
        // Triggers that change other rows: their counts must not be taken for the statement's own.
        Execute(connection, "CREATE TRIGGER dbo.ORDERS_AUDIT ON dbo.ORDERS AFTER INSERT, UPDATE, DELETE AS INSERT INTO dbo.ORDER_AUDIT (ORDER_ID) SELECT ORDER_ID FROM inserted UNION ALL SELECT ORDER_ID FROM deleted;");
        Execute(connection, "CREATE TABLE dbo.NOTES (ID uniqueidentifier NOT NULL DEFAULT NEWSEQUENTIALID() PRIMARY KEY, BODY nvarchar(50) NOT NULL, RV rowversion);");
        var configuration = new ConfigurationBuilder().Build();
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<IConfiguration>(configuration);
        collection.AddTestConnections(connections => connections.AddConnection("LocalDb", _ => new SqlConnection(connection)));
        collection.AddSingleton<DataSourceRuntime>();
        using var provider = collection.BuildServiceProvider();
        var runtime = provider.GetRequiredService<DataSourceRuntime>();
        var user = new DataSourceUser("1001", "S-1-5-21-1", "jsmith", null, [], false);
        var context = new FormExecutionContext(new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "1001")], IdentityConstants.ApplicationScheme)),
            Guid.NewGuid(), Guid.NewGuid(), "1", Guid.NewGuid().ToString());

        var policy = new CountingPolicy();
        var orders = Source(runtime, user, new() {
            ["alias"] = "orders", ["connection"] = "LocalDb", ["source"] = "dbo.ORDERS", ["keyColumns"] = "ORDER_ID", ["concurrency"] = "VERSION",
            ["allowInsert"] = "1", ["allowUpdate"] = "1", ["allowDelete"] = "1", ["rowRestriction"] = "OWNER = :USER_NAME",
            ["insertDefaults"] = "OWNER = :USER_NAME\nORDER_DATE = :NOW", ["auditColumns"] = "updatedBy = UPDATED_BY\nupdatedAt = UPDATED_AT",
        }, policy);
        Assert.True(orders.Problem is null, orders.Problem);
        var fields = orders.Fields.ToDictionary(f => f.Key);
        Assert.True(fields["orderId"] is { ReadOnly: true, Kind: "number" } && fields["total"].ReadOnly && fields["done"].Kind == "checkbox");
        Assert.True(fields["customer"] is { MaxLength: 200, Required: true } && fields["orderDate"].Kind == "date" && fields["updatedAt"].Kind == "datetime");
        Assert.Equal(9999999999.99m, fields["amount"].Maximum);

        var created = await orders.CreateAsync(JsonSerializer.SerializeToElement(new { customer = "LocalDB", amount = "12.34", done = true }), context, default);
        Assert.True(created.Status == 200, JsonSerializer.Serialize(created.Errors));
        Assert.Equal("1", created.Record!.Key);
        Assert.Equal("24.68", created.Record.Values["total"]);
        Assert.Equal(1, Scalar(connection, "SELECT COUNT(*) FROM dbo.ORDER_AUDIT")); // The trigger ran beside OUTPUT INSERTED.
        var updated = await orders.UpdateAsync("1", JsonSerializer.SerializeToElement(new { notes = "Through SQL Server", orderDate = "2026-12-24" }), created.Record.Version!, context, default);
        Assert.True(updated.Status == 200, JsonSerializer.Serialize(updated.Errors));
        Assert.Equal("2", updated.Record!.Version);
        // The policy read the table through the engine's connection, inside the transaction holding the row.
        Assert.Equal((ConnectionState.Open, true, 1), policy.Updates[0]);
        var legacy = await orders.UpdateAsync("1", JsonSerializer.SerializeToElement(new { legacyAt = "1700-01-01T00:00:00Z" }), "2", context, default);
        Assert.Equal(422, legacy.Status);
        Assert.Contains("1753", legacy.Errors!["legacyAt"][0]);
        Assert.Equal("2026-12-24", updated.Record.Values["orderDate"]);
        Assert.Equal("jsmith", Scalar(connection, "SELECT UPDATED_BY FROM dbo.ORDERS WHERE ORDER_ID = 1"));
        Assert.Equal(409, (await orders.UpdateAsync("1", JsonSerializer.SerializeToElement(new { notes = "Stale" }), created.Record.Version!, context, default)).Status);
        Execute(connection, "INSERT INTO dbo.ORDERS (CUSTOMER, ORDER_DATE, OWNER) VALUES ('Someone else', '2026-01-01', 'kjones')");
        Assert.Equal(404, (await orders.LoadAsync("2", context, default)).Status);
        Assert.Equal(403, (await orders.CreateAsync(JsonSerializer.SerializeToElement(new { customer = "Not mine", owner = "kjones" }), context with { SubmissionKey = Guid.NewGuid().ToString() }, default)).Status);
        Assert.Equal(204, (await orders.DeleteAsync("1", "2", context, default)).Status);

        var notes = Source(runtime, user, new() {
            ["alias"] = "notes", ["connection"] = "LocalDb", ["source"] = "SELECT ID, BODY, RV FROM dbo.NOTES", ["keyColumns"] = "ID", ["concurrency"] = "RV",
            ["allowInsert"] = "1", ["allowUpdate"] = "1",
        });
        Assert.True(notes.Problem is null, notes.Problem);
        Assert.DoesNotContain(notes.Fields, f => f.Key == "rv");
        var note = await notes.CreateAsync(JsonSerializer.SerializeToElement(new { body = "First" }), context with { SubmissionKey = Guid.NewGuid().ToString() }, default);
        Assert.True(Guid.TryParse(note.Record!.Key, out _));
        Assert.Equal(16, note.Record.Version!.Length);
        var changed = await notes.UpdateAsync(note.Record.Key!, JsonSerializer.SerializeToElement(new { body = "Second" }), note.Record.Version, context, default);
        Assert.Equal(200, changed.Status);
        Assert.NotEqual(note.Record.Version, changed.Record!.Version);
        Assert.Equal(409, (await notes.UpdateAsync(note.Record.Key!, JsonSerializer.SerializeToElement(new { body = "Stale" }), note.Record.Version, context, default)).Status);
        Assert.Equal(404, (await notes.LoadAsync("not-a-guid", context, default)).Status);

        var errors = runtime.Validate(Definition(new() {
            ["alias"] = "broken", ["connection"] = "LocalDb", ["source"] = "dbo.ORDERS", ["keyColumns"] = "ORDER_ID", ["allowUpdate"] = "1",
            ["customStatements"] = "-- update\nUPDATE dbo.ORDERS SET NOPE = 1 WHERE ORDER_ID = :FIELD.orderId",
        }));
        Assert.Contains(errors, e => e.Contains("does not compile"));
        Assert.Equal(1, Scalar(connection, "SELECT COUNT(*) FROM dbo.ORDERS"));
    }

    private static SqlDataSource Source(DataSourceRuntime runtime, DataSourceUser user, Dictionary<string, object?> values, params IDataSourcePolicy[] policies)
        => new(Definition(values), runtime, new OneUser(user), policies);

    /// <summary>Counts the orders on every update through the request's connection and transaction.</summary>
    private sealed class CountingPolicy : IDataSourcePolicy
    {
        public List<(ConnectionState State, bool Transaction, int Orders)> Updates { get; } = [];

        public async ValueTask<bool> IsAllowedAsync(DataSourceRequest request, CancellationToken ct)
        {
            if (request.Action != DataSourceAction.Update) return true;
            await using var command = request.Connection!.CreateCommand();
            command.Transaction = request.Transaction;
            command.CommandText = "SELECT COUNT(*) FROM dbo.ORDERS";
            Updates.Add((request.Connection.State, request.Transaction is not null, (int)(await command.ExecuteScalarAsync(ct))!));
            return true;
        }
    }

    private static DataSourceDefinition Definition(Dictionary<string, object?> values)
    {
        var errors = new List<string>();
        var definition = DataSourceDefinition.Read(Guid.NewGuid(), "LocalDB", alias => values.GetValueOrDefault(alias), _ => [], errors);
        Assert.True(definition is not null, string.Join(" ", errors));
        return definition;
    }

    private sealed class OneUser(DataSourceUser user) : IDataSourceUserResolver
    {
        public Task<DataSourceUser?> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct) => Task.FromResult<DataSourceUser?>(user);
    }

    private static void Execute(string connection, string sql)
    {
        using var database = new SqlConnection(connection);
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 90;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(string connection, string sql)
    {
        using var database = new SqlConnection(connection);
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
