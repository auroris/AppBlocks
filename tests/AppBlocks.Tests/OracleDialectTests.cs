using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using AppBlocks.Connections;
using Microsoft.Extensions.Logging.Abstractions;
using Oracle.ManagedDataAccess.Client;
using AppBlocks.DataSources;

namespace AppBlocks.Tests;

public sealed class OracleDialectTests
{
    [Theory]
    [InlineData("11.2.0.4.0", DatabaseDialect.Oracle, true)]
    [InlineData("11.2.0.4.0", DatabaseDialect.Oracle11g, true)]
    [InlineData("12.1.0.2.0", DatabaseDialect.Oracle, false)]
    [InlineData("19.3.0.0.0", DatabaseDialect.Oracle, false)]
    public void Forms_select_version_appropriate_metadata_and_generated_keys(string version, DatabaseDialect declared, bool legacy)
    {
        var connection = new SchemaConnection(version);
        var factory = new ConnectionFactory(connection, declared);
        var runtime = new DataSourceRuntime(factory, NullLogger<DataSourceRuntime>.Instance);
        var definition = new DataSourceDefinition {
            Key = Guid.NewGuid(), Name = "Orders", Alias = "orders", Connection = "Business", Source = "ORDERS",
            Access = DataSourceAccess.Public,
            KeyColumns = ["ID"], AllowInsert = true,
            KeySequences = legacy ? new Dictionary<string, string> { ["ID"] = "ORDERS_SEQ" } : new Dictionary<string, string>(),
        };

        var result = runtime.Plan(definition);
        Assert.Null(result.Problem);
        var plan = Assert.IsType<DataSourcePlan>(result.Plan);
        Assert.Equal(legacy ? DatabaseDialect.Oracle11g : DatabaseDialect.Oracle, plan.Dialect.Kind);
        Assert.Equal(plan.Dialect.Kind, factory.Dialect("Business"));
        Assert.Equal(!legacy, connection.Statements.Any(s => s.Contains("ALL_TAB_IDENTITY_COLS")));
        Assert.True(plan.Writable);
        var key = plan.Target!.Column("ID")!;
        Assert.True(key.AutoValue);
        Assert.Equal(legacy ? new TableName("APP", "ORDERS_SEQ") { SchemaIsQuoted = true, NameIsQuoted = true } : null, key.Sequence);

        var insert = plan.Dialect.Insert(plan.Table!, [("TITLE", ":title")], [key]);
        Assert.Contains("RETURNING \"ID\" INTO :ds_key0", insert);
        Assert.Equal(legacy, insert.Contains("\"APP\".\"ORDERS_SEQ\".NEXTVAL"));
        var select = plan.Dialect.Select("\"APP\".\"ORDERS\"", ["ID", "TITLE"], [], null, 25);
        Assert.Contains("ROWNUM <= 25", select);
        Assert.DoesNotContain("FETCH", select);
        Assert.DoesNotContain("OFFSET", select);
    }

    [Theory]
    [InlineData(DatabaseDialect.Oracle)]
    [InlineData(DatabaseDialect.Oracle11g)]
    public void Oracle_commands_bind_by_name_and_compile_without_values(DatabaseDialect kind)
    {
        using var command = new OracleCommand();
        var dialect = SqlDialect.For(kind)!;
        dialect.ConfigureCommand(command);
        Assert.True(command.BindByName);
        command.Parameters.Add(new OracleParameter("id", 1));
        dialect.ConfigureCompilation(command);
        Assert.Empty(command.Parameters.Cast<DbParameter>());
        Assert.Equal("EXPLAIN PLAN FOR SELECT :id FROM DUAL", dialect.CompileOnly("SELECT :id FROM DUAL"));
    }

    private sealed class ConnectionFactory(SchemaConnection connection, DatabaseDialect dialect) : IApplicationConnectionFactory
    {
        public DbConnection CreateConnection(string name) => connection;
        public DatabaseDialect Dialect(string name) => dialect;
        public void SetDetectedDialect(string name, DatabaseDialect detected) => dialect = detected;
    }

    private sealed class SchemaConnection(string version) : DbConnection
    {
        public List<string> Statements { get; } = [];
        [AllowNull] public override string ConnectionString { get; set; } = "";
        public override string Database => "ORCL";
        public override string DataSource => "test";
        public override string ServerVersion => version;
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new SchemaCommand(this);

        public DbDataReader Read(string sql)
        {
            Statements.Add(sql);
            if (sql.Contains("ALL_OBJECTS")) return Rows(["OWNER", "OBJECT_NAME", "OBJECT_TYPE"], ["APP", "ORDERS", "TABLE"]);
            if (sql.Contains("ALL_TAB_IDENTITY_COLS"))
            {
                Assert.False(version.StartsWith("11."), "Oracle 11.2 has no identity-column metadata view.");
                return Rows(["COLUMN_NAME"], ["ID"]);
            }
            if (sql.Contains("ALL_TAB_COLS")) return Rows(
                ["COLUMN_NAME", "DATA_TYPE", "NULLABLE", "DEFAULT_LENGTH", "VIRTUAL_COLUMN", "CHAR_LENGTH", "DATA_PRECISION", "DATA_SCALE"],
                ["ID", "NUMBER", "N", DBNull.Value, "NO", 0, 10, 0],
                ["TITLE", "VARCHAR2", "N", DBNull.Value, "NO", 100, DBNull.Value, DBNull.Value]);
            if (sql.Contains("ALL_CONSTRAINTS")) return Rows(["CONSTRAINT_NAME", "COLUMN_NAME"], ["ORDERS_PK", "ID"]);
            if (sql.Contains("ALL_SEQUENCES")) return Rows(["SEQUENCE_OWNER", "SEQUENCE_NAME"], ["APP", "ORDERS_SEQ"]);
            if (sql.Contains("WHERE 1 = 0"))
            {
                var table = new DataTable("ORDERS");
                table.Columns.Add("ID", typeof(decimal));
                table.Columns.Add("TITLE", typeof(string));
                return table.CreateDataReader();
            }
            throw new InvalidOperationException("Unexpected schema query: " + sql);
        }

        private static DbDataReader Rows(string[] names, params object[][] rows)
        {
            var table = new DataTable();
            foreach (var name in names) table.Columns.Add(name, typeof(object));
            foreach (var row in rows) table.Rows.Add(row);
            return table.CreateDataReader();
        }
    }

    private sealed class SchemaCommand(SchemaConnection connection) : DbCommand
    {
        private readonly OracleCommand parameters = new();
        public bool BindByName { get; set; }
        [AllowNull] public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = connection;
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => parameters.Parameters;
        protected override DbParameter CreateDbParameter() => new OracleParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            Assert.True(BindByName);
            return connection.Read(CommandText);
        }
        public override void Cancel() => throw new NotSupportedException();
        public override int ExecuteNonQuery() => throw new NotSupportedException();
        public override object? ExecuteScalar() => throw new NotSupportedException();
        public override void Prepare() => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) parameters.Dispose(); base.Dispose(disposing); }
    }
}
