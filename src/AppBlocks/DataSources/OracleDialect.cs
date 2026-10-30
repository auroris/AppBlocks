using System.Data;
using System.Data.Common;
using System.Text;
using InteractiveReport.Core.Model;

namespace UmbracoAppHost.DataSources;

/// <summary>Oracle 12c and 11g: named binds, RETURNING INTO, sequence keys and FOR UPDATE row locks.</summary>
public sealed class OracleDialect : SqlDialect
{
    public static readonly OracleDialect Instance = new(false);
    public static readonly OracleDialect Legacy = new(true);
    private readonly bool legacy;
    private OracleDialect(bool legacy) => this.legacy = legacy;
    public override ReportDialect Kind => legacy ? ReportDialect.Oracle11g : ReportDialect.Oracle;
    public override IsolationLevel WriteIsolation => IsolationLevel.ReadCommitted;
    public override string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
    public override string Marker(string name) => ":" + name;
    public override string CompileOnly(string statement) => "EXPLAIN PLAN FOR " + statement;
    public override void ConfigureCompilation(DbCommand command) => command.Parameters.Clear();

    // The provider is supplied by the host. Avoid introducing a mandatory Oracle dependency for SQLite sites.
    public override void ConfigureCommand(DbCommand command)
    {
        var property = command.GetType().GetProperty("BindByName");
        if (property?.PropertyType == typeof(bool) && property.CanWrite) property.SetValue(command, true);
    }

    public override string? KindOf(string storeType)
    {
        var type = storeType.ToUpperInvariant();
        if (type is "NUMBER" or "FLOAT" or "BINARY_FLOAT" or "BINARY_DOUBLE" or "INTEGER" or "DECIMAL") return FieldKinds.Number;
        if (type is "DATE" || type.StartsWith("TIMESTAMP", StringComparison.Ordinal)) return FieldKinds.DateTime;
        if (type is "BOOLEAN") return FieldKinds.Checkbox;
        return type is "RAW" or "LONG RAW" or "BLOB" or "BFILE" ? null : FieldKinds.Text;
    }
    public override string? ExpressionKind(SourceColumn column) => KindOf(column.StoreType ?? "VARCHAR2");
    public override bool IsInteger(TableColumn column) => column.StoreType.ToUpperInvariant() is "INTEGER"
        || column.StoreType.ToUpperInvariant() is "NUMBER" or "DECIMAL" && column.Scale == 0;

    public override object? ToDatabase(object? value, string kind, TableColumn? column)
    {
        var type = column?.StoreType.ToUpperInvariant() ?? "";
        return value switch {
            null => null,
            bool flag when type != "BOOLEAN" => flag ? 1m : 0m,
            decimal number when type is "VARCHAR2" or "NVARCHAR2" or "CHAR" or "NCHAR" or "CLOB" or "NCLOB" => number.ToString(Invariant),
            decimal number when column is not null && IsInteger(column) && number != decimal.Truncate(number) => throw new DataSourceException("Enter a whole number."),
            decimal number when type == "BINARY_FLOAT" => (float)number,
            decimal number when type == "BINARY_DOUBLE" => (double)number,
            DateOnly date => new TypedValue(date.ToDateTime(TimeOnly.MinValue), DbType.Date),
            TimeOnly time => time.ToString("HH:mm:ss.FFF", Invariant),
            DateTimeOffset moment when type.Contains("WITH TIME ZONE", StringComparison.Ordinal) => new TypedValue(moment, DbType.DateTimeOffset),
            DateTimeOffset moment => new TypedValue(moment.LocalDateTime, DbType.DateTime),
            _ => value,
        };
    }
    public override object Now(DateTime now) => new TypedValue(now, DbType.DateTime);

    private DbCommand Command(DbConnection connection, string text)
    {
        var command = connection.CreateCommand();
        command.CommandText = text;
        command.CommandTimeout = 30;
        ConfigureCommand(command);
        return command;
    }

    public override TableSchema? ReadTable(DbConnection connection, TableName name)
    {
        string owner, table;
        bool view;
        using (var command = Command(connection, """
            SELECT OWNER, OBJECT_NAME, OBJECT_TYPE FROM ALL_OBJECTS
            WHERE OWNER = COALESCE(:owner, SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA'))
              AND OBJECT_NAME = :name AND OBJECT_TYPE IN ('TABLE', 'VIEW')
            """))
        {
            Bind(command, ":owner", name.SchemaIsQuoted ? name.Schema : name.Schema?.ToUpperInvariant());
            Bind(command, ":name", name.NameIsQuoted ? name.Name : name.Name.ToUpperInvariant());
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            (owner, table, view) = (reader.GetString(0), reader.GetString(1), reader.GetString(2) == "VIEW");
        }
        var identity = new HashSet<string>(StringComparer.Ordinal);
        if (!legacy)
        {
            using var command = Command(connection, "SELECT COLUMN_NAME FROM ALL_TAB_IDENTITY_COLS WHERE OWNER = :owner AND TABLE_NAME = :table");
            Bind(command, ":owner", owner); Bind(command, ":table", table);
            using var reader = command.ExecuteReader();
            while (reader.Read()) identity.Add(reader.GetString(0));
        }
        var columns = new List<TableColumn>();
        using (var command = Command(connection, """
            SELECT COLUMN_NAME, DATA_TYPE, NULLABLE, DEFAULT_LENGTH, VIRTUAL_COLUMN, CHAR_LENGTH, DATA_PRECISION, DATA_SCALE
            FROM ALL_TAB_COLS WHERE OWNER = :owner AND TABLE_NAME = :table AND HIDDEN_COLUMN = 'NO' ORDER BY COLUMN_ID
            """))
        {
            Bind(command, ":owner", owner); Bind(command, ":table", table);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var column = reader.GetString(0);
                var type = reader.GetString(1);
                columns.Add(new(column, type, reader.GetString(2) == "Y", OptionalInt(reader, 3) > 0 || identity.Contains(column),
                    identity.Contains(column), reader.GetString(4) == "YES", false,
                    type is "CHAR" or "VARCHAR2" or "NCHAR" or "NVARCHAR2" ? OptionalInt(reader, 5) : null,
                    OptionalInt(reader, 6), OptionalInt(reader, 7)));
            }
        }
        var keys = new List<IReadOnlyList<string>>();
        using (var command = Command(connection, """
            SELECT c.CONSTRAINT_NAME, cc.COLUMN_NAME FROM ALL_CONSTRAINTS c
            JOIN ALL_CONS_COLUMNS cc ON cc.OWNER = c.OWNER AND cc.CONSTRAINT_NAME = c.CONSTRAINT_NAME
            WHERE c.OWNER = :owner AND c.TABLE_NAME = :table AND c.CONSTRAINT_TYPE IN ('P', 'U') AND c.STATUS = 'ENABLED'
            ORDER BY c.CONSTRAINT_TYPE, c.CONSTRAINT_NAME, cc.POSITION
            """))
        {
            Bind(command, ":owner", owner); Bind(command, ":table", table);
            using var reader = command.ExecuteReader();
            string? current = null;
            List<string>? key = null;
            while (reader.Read())
            {
                if (reader.GetString(0) != current) { current = reader.GetString(0); keys.Add(key = []); }
                key!.Add(reader.GetString(1));
            }
        }
        return new(new(owner, table) { SchemaIsQuoted = true, NameIsQuoted = true }, view, columns, keys);
    }

    public override TableName? ReadSequence(DbConnection connection, TableName name)
    {
        using var command = Command(connection, """
            SELECT SEQUENCE_OWNER, SEQUENCE_NAME FROM ALL_SEQUENCES
            WHERE SEQUENCE_OWNER = COALESCE(:owner, SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')) AND SEQUENCE_NAME = :name
            """);
        Bind(command, ":owner", name.SchemaIsQuoted ? name.Schema : name.Schema?.ToUpperInvariant());
        Bind(command, ":name", name.NameIsQuoted ? name.Name : name.Name.ToUpperInvariant());
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetString(1)) { SchemaIsQuoted = true, NameIsQuoted = true } : null;
    }

    private static int? OptionalInt(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal), Invariant);

    public override string Select(string from, IEnumerable<string> columns, IReadOnlyList<(string Column, string Marker)> keys, string? restriction, int? limit = null)
    {
        var query = base.Select(from, columns, keys, restriction);
        // ROWNUM works on both releases, including queries whose outer projection must stay unchanged.
        return limit is { } rows ? $"SELECT * FROM (\n{query}\n) WHERE ROWNUM <= {rows.ToString(Invariant)}" : query;
    }
    protected override string Limit(int? limit) => "";

    public override string Insert(TableName table, IReadOnlyList<(string Column, string Value)> values, IReadOnlyList<TableColumn> returning)
    {
        var supplied = values.ToList();
        foreach (var column in returning.Where(c => c.Sequence is not null))
            supplied.Add((column.Name, Quote(column.Sequence!) + ".NEXTVAL"));
        // Oracle has no INSERT DEFAULT VALUES; naming an omitted key with DEFAULT has the same effect.
        if (supplied.Count == 0 && returning.Count > 0) supplied.Add((returning[0].Name, "DEFAULT"));
        var text = new StringBuilder("INSERT INTO ").Append(Quote(table)).Append(" (")
            .AppendJoin(", ", supplied.Select(v => Quote(v.Column))).Append(") VALUES (")
            .AppendJoin(", ", supplied.Select(v => v.Value)).Append(')');
        return WithReturning(text.ToString(), returning);
    }

    internal string WithReturning(string statement, IReadOnlyList<TableColumn> returning) => returning.Count == 0 ? statement
        : statement.TrimEnd().TrimEnd(';') + " RETURNING " + string.Join(", ", returning.Select(c => Quote(c.Name)))
            + " INTO " + string.Join(", ", Enumerable.Range(0, returning.Count).Select(i => Marker("ds_key" + i.ToString(Invariant))));

    internal override async Task<object?[]> ExecuteInsertAsync(DbCommand command, IReadOnlyList<TableColumn> returning, CancellationToken ct)
    {
        var outputs = new List<DbParameter>();
        foreach (var (column, index) in returning.Select((c, i) => (c, i)))
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "ds_key" + index.ToString(Invariant);
            parameter.Direction = ParameterDirection.Output;
            parameter.DbType = KindOf(column.StoreType) switch {
                FieldKinds.Number => DbType.Decimal,
                FieldKinds.DateTime => DbType.DateTime,
                _ => DbType.String,
            };
            if (parameter.DbType == DbType.String) parameter.Size = column.Length ?? FieldKinds.DefaultTextLength;
            command.Parameters.Add(parameter);
            outputs.Add(parameter);
        }
        await command.ExecuteNonQueryAsync(ct);
        return outputs.Select(p => p.Value is DBNull ? null : p.Value).ToArray();
    }

    public override string? Lock(TableName table, IReadOnlyList<(string Column, string Marker)> keys)
        => $"SELECT 1 FROM {Quote(table)} WHERE {Match("", keys)} FOR UPDATE";
}
