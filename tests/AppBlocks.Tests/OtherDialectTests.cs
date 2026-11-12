using System.Data;
using System.Data.Common;
using AppBlocks.Connections;
using AppBlocks.DataSources;

namespace AppBlocks.Tests;

public sealed class OtherDialectTests
{
    [Theory]
    [InlineData(DatabaseDialect.Sqlite, "TEXT")]
    [InlineData(DatabaseDialect.SqlServer, "varchar")]
    [InlineData(DatabaseDialect.Oracle, "VARCHAR2")]
    [InlineData(DatabaseDialect.Oracle11g, "VARCHAR2")]
    public void Time_values_bound_as_text_keep_submillisecond_precision(DatabaseDialect kind, string storeType)
    {
        var time = new TimeOnly(12, 0).Add(TimeSpan.FromTicks(1234567));
        var column = new TableColumn("value", storeType, false, false, false, false, false);
        Assert.Equal("12:00:00.1234567", SqlDialect.For(kind)!.ToDatabase(time, FieldKinds.Time, column));
    }

    private static readonly TableName Orders = new("app", "orders");
    private static readonly TableColumn Id = new("id", "bigint", false, true, true, false, false);

    [Fact]
    public void Every_application_dialect_has_a_form_builder()
    {
        foreach (var dialect in Enum.GetValues<DatabaseDialect>()) Assert.NotNull(SqlDialect.For(dialect));
    }

    [Fact]
    public void PostgreSql_returns_keys_locks_rows_and_bounds_reads()
    {
        var dialect = PostgreSqlDialect.Instance;
        Assert.Equal("INSERT INTO \"app\".\"orders\" (\"customer\") VALUES (@ds0) RETURNING \"id\"", dialect.Insert(Orders, [("customer", "@ds0")], [Id]));
        Assert.Equal("INSERT INTO \"app\".\"orders\" DEFAULT VALUES RETURNING \"id\"", dialect.Insert(Orders, [], [Id]));
        Assert.Equal("SELECT 1 FROM \"app\".\"orders\" WHERE \"id\" = @ds0 FOR UPDATE", dialect.Lock(Orders, [("id", "@ds0")]));
        Assert.Equal("SELECT src.\"id\" FROM \"app\".\"orders\" src WHERE 1 = 1 AND (\nowner = @ds0\n) LIMIT 10",
            dialect.Select(dialect.From(Orders, null), ["id"], [], "owner = @ds0", 10));
        Assert.Equal(IsolationLevel.ReadCommitted, dialect.WriteIsolation);
        Assert.Equal("EXPLAIN UPDATE x SET a = @ds0", dialect.CompileOnly("UPDATE x SET a = @ds0"));
    }

    [Fact]
    public void Oracle_returns_identity_and_sequence_keys_with_named_output_binds()
    {
        var dialect = OracleDialect.Instance;
        var id = Id with { StoreType = "NUMBER", Scale = 0 };
        Assert.Equal(":ds0", dialect.Marker("ds0"));
        Assert.Equal("INSERT INTO \"app\".\"orders\" (\"customer\") VALUES (:ds0) RETURNING \"id\" INTO :ds_key0",
            dialect.Insert(Orders, [("customer", ":ds0")], [id]));
        Assert.Equal("INSERT INTO \"app\".\"orders\" (\"id\") VALUES (DEFAULT) RETURNING \"id\" INTO :ds_key0", dialect.Insert(Orders, [], [id]));
        var sequenceId = id with { Sequence = new("app", "order_ids") };
        Assert.Equal("INSERT INTO \"app\".\"orders\" (\"customer\", \"id\") VALUES (:ds0, \"app\".\"order_ids\".NEXTVAL) RETURNING \"id\" INTO :ds_key0",
            OracleDialect.Legacy.Insert(Orders, [("customer", ":ds0")], [sequenceId]));
        using var command = new NamedCommand { BindByName = false };
        dialect.ConfigureCommand(command);
        Assert.True(command.BindByName);
    }

    [Theory]
    [InlineData(DatabaseDialect.Oracle)]
    [InlineData(DatabaseDialect.Oracle11g)]
    public void Oracle_bounded_reads_work_without_fetch_first_or_limit(DatabaseDialect kind)
    {
        var dialect = SqlDialect.For(kind)!;
        Assert.Equal("SELECT * FROM (\nSELECT src.\"id\" FROM \"app\".\"orders\" src WHERE src.\"id\" = :ds0 AND (\nowner = :ds1\n)\n) WHERE ROWNUM <= 2",
            dialect.Select(dialect.From(Orders, null), ["id"], [("id", ":ds0")], "owner = :ds1", 2));
        Assert.Equal("SELECT 1 FROM \"app\".\"orders\" WHERE \"id\" = :ds0 FOR UPDATE", dialect.Lock(Orders, [("id", ":ds0")]));
        var predicate = dialect.Exists(dialect.From(Orders, null), [("id", ":ds1")], "owner = :ds2");
        Assert.Contains("WHERE \"id\" = :ds1 AND EXISTS", dialect.Update(Orders, [("customer", ":ds0")], [("id", ":ds1")], predicate));
        Assert.Equal(IsolationLevel.ReadCommitted, dialect.WriteIsolation);
    }

    [Fact]
    public void Numeric_and_datetime_values_keep_their_database_semantics()
    {
        var postgres = PostgreSqlDialect.Instance;
        TableColumn Column(string type, int? scale = null) => new("value", type, true, false, false, false, false, Scale: scale);
        Assert.Equal(12L, postgres.ToDatabase(12m, FieldKinds.Number, Column("bigint")));
        Assert.Equal(1, postgres.ToDatabase(true, FieldKinds.Checkbox, Column("integer")));
        Assert.Throws<DataSourceException>(() => postgres.ToDatabase(12.5m, FieldKinds.Number, Column("bigint")));
        Assert.Throws<DataSourceException>(() => postgres.ToDatabase(100000m, FieldKinds.Number, Column("smallint")));
        var moment = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.FromHours(-6));
        Assert.Equal(moment.UtcDateTime, postgres.ToDatabase(moment, FieldKinds.DateTime, Column("timestamp with time zone")));
        Assert.Equal(FieldKinds.Checkbox, postgres.KindOf("boolean"));
        Assert.Equal(FieldKinds.DateTime, OracleDialect.Instance.KindOf("DATE"));
        Assert.Equal(1m, OracleDialect.Instance.ToDatabase(true, FieldKinds.Checkbox, Column("NUMBER", 0)));
        Assert.True(OracleDialect.Instance.IsInteger(Column("NUMBER", 0)));
        Assert.False(OracleDialect.Instance.IsInteger(Column("NUMBER", 2)));
        Assert.Null(OracleDialect.Instance.KindOf("BLOB"));
    }

    [Fact]
    public void PostgreSql_binds_text_for_an_unlisted_type_as_untyped_so_the_server_reads_it_as_the_column_type()
    {
        var postgres = PostgreSqlDialect.Instance;
        TableColumn Column(string type) => new("value", type, true, false, false, false, false);
        foreach (var type in new[] { "jsonb", "json", "xml", "inet", "interval", "integer[]", "mood" })
        {
            Assert.Equal(FieldKinds.Text, postgres.KindOf(type));
            Assert.Equal(new UntypedText("{\"a\": 1}"), postgres.ToDatabase("{\"a\": 1}", FieldKinds.Text, Column(type)));
        }
        foreach (var type in new[] { "text", "character varying", "character", "citext" })
            Assert.Equal("plain", postgres.ToDatabase("plain", FieldKinds.Text, Column(type)));

        var parameters = new SqlParameters(postgres);
        parameters.Add(postgres.ToDatabase("{\"a\": 1}", FieldKinds.Text, Column("jsonb")));
        parameters.Add("plain");
        using var command = parameters.Command(new Npgsql.NpgsqlConnection(), null, "UPDATE t SET doc = @ds0, name = @ds1");
        var bound = command.Parameters.Cast<Npgsql.NpgsqlParameter>().ToArray();
        Assert.Equal((NpgsqlTypes.NpgsqlDbType.Unknown, (object)"{\"a\": 1}"), (bound[0].NpgsqlDbType, bound[0].Value!));
        Assert.Equal("plain", bound[1].Value);
        // Npgsql also reports Unknown for a type it has yet to infer, so the setting itself is checked on a parameter typed beforehand.
        var typed = new Npgsql.NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text };
        postgres.BindUntyped(typed);
        Assert.Equal(NpgsqlTypes.NpgsqlDbType.Unknown, typed.NpgsqlDbType);
        // Other providers have no such type and keep the plain text.
        Assert.Equal("plain", SqliteDialect.Instance.ToDatabase("plain", FieldKinds.Text, Column("jsonb")));
    }

    [Fact]
    public void Each_dialect_recognises_its_providers_timeout_and_nothing_else()
    {
        Assert.True(SqlDialect.For(DatabaseDialect.SqlServer)!.IsTimeout(new NumberedException(-2)));
        Assert.False(SqlDialect.For(DatabaseDialect.SqlServer)!.IsTimeout(new NumberedException(2627)));
        Assert.True(OracleDialect.Instance.IsTimeout(new NumberedException(1013)));
        Assert.False(OracleDialect.Instance.IsTimeout(new NumberedException(1)));
        Assert.True(PostgreSqlDialect.Instance.IsTimeout(new Npgsql.NpgsqlException("Exception while reading from stream", new TimeoutException())));
        Assert.True(PostgreSqlDialect.Instance.IsTimeout(new StateException("57014")));
        Assert.False(PostgreSqlDialect.Instance.IsTimeout(new StateException("23505")));
        Assert.True(SqliteDialect.Instance.IsTimeout(new Microsoft.Data.Sqlite.SqliteException("database is locked", 5)));
        Assert.False(SqliteDialect.Instance.IsTimeout(new Microsoft.Data.Sqlite.SqliteException("constraint failed", 19)));
    }

    [Fact]
    public void Oracle_and_PostgreSql_leave_blocks_and_calls_uncompiled_and_Oracle_drops_a_statements_semicolon()
    {
        Assert.False(OracleDialect.Instance.Compiles("BEGIN orders_api.create(:FIELD.customer); END;"));
        Assert.False(OracleDialect.Instance.Compiles("-- make it\nDECLARE n NUMBER; BEGIN NULL; END;"));
        Assert.False(OracleDialect.Instance.Compiles("CALL orders_api.create(:FIELD.customer)"));
        Assert.True(OracleDialect.Instance.Compiles("UPDATE orders SET a = 1"));
        Assert.Equal("UPDATE orders SET a = 1", OracleDialect.Instance.Statement("UPDATE orders SET a = 1;\n"));
        Assert.Equal("BEGIN NULL; END;", OracleDialect.Instance.Statement("BEGIN NULL; END;"));
        Assert.False(PostgreSqlDialect.Instance.Compiles("CALL create_order(@ds0)"));
        Assert.False(PostgreSqlDialect.Instance.Compiles("DO $$ BEGIN PERFORM 1; END $$"));
        Assert.True(PostgreSqlDialect.Instance.Compiles("UPDATE orders SET a = 1"));
        Assert.True(SqliteDialect.Instance.Compiles("BEGIN"));
    }

    private sealed class NumberedException(int number) : DbException("failed")
    {
        public int Number { get; } = number;
    }

    private sealed class StateException(string state) : DbException("failed")
    {
        public override string SqlState => state;
    }

    [Fact]
    public void Quoted_names_preserve_case_and_escape_embedded_quotes()
    {
        var parsed = TableName.Parse("\"App\".\"Order\"\"s\"")!;
        Assert.Equal("App", parsed.Schema);
        Assert.Equal("Order\"s", parsed.Name);
        Assert.True(parsed.SchemaIsQuoted);
        Assert.True(parsed.NameIsQuoted);
        Assert.Equal("\"App\".\"Order\"\"s\"", PostgreSqlDialect.Instance.Quote(parsed));
    }

    [Fact]
    public void Sequence_configuration_is_validated_and_changes_the_contract_fingerprint()
    {
        var values = new Dictionary<string, object?> { ["alias"] = "orders", ["access"] = "Public", ["connection"] = "db", ["source"] = "ORDERS", ["keyColumns"] = "ID", ["keySequences"] = """{"ID":"APP.ORDER_IDS"}""" };
        var errors = new List<string>();
        var definition = DataSourceDefinition.Read(Guid.NewGuid(), "Orders", k => values.GetValueOrDefault(k), _ => [], errors)!;
        Assert.Empty(errors);
        Assert.Equal("APP.ORDER_IDS", definition.KeySequences["ID"]);
        Assert.NotEqual(definition.ComputeFingerprint(), (definition with { KeySequences = new Dictionary<string, string>() }).ComputeFingerprint());
        values["keySequences"] = """{"ID":"SEQ; DROP TABLE ORDERS"}""";
        Assert.Null(DataSourceDefinition.Read(Guid.NewGuid(), "Orders", k => values.GetValueOrDefault(k), _ => [], errors));
        Assert.Contains(errors, e => e.Contains("sequence"));
    }

    private sealed class NamedCommand : DbCommand
    {
        public bool BindByName { get; set; }
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();
        public override void Cancel() => throw new NotSupportedException();
        public override int ExecuteNonQuery() => throw new NotSupportedException();
        public override object? ExecuteScalar() => throw new NotSupportedException();
        public override void Prepare() => throw new NotSupportedException();
        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
    }
}
