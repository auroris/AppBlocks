using System.Data;
using System.Data.Common;
using System.Text;
using AppBlocks.Connections;

namespace AppBlocks.DataSources;

/// <summary>PostgreSQL metadata, RETURNING keys and row locks, using the connection's normal search path.</summary>
public sealed class PostgreSqlDialect : SqlDialect
{
    public static readonly PostgreSqlDialect Instance = new();
    private PostgreSqlDialect() { }
    public override DatabaseDialect Kind => DatabaseDialect.Postgres;
    public override IsolationLevel WriteIsolation => IsolationLevel.ReadCommitted;
    public override string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
    public override string CompileOnly(string statement) => "EXPLAIN " + statement;

    public override string? KindOf(string storeType) => storeType.ToLowerInvariant() switch {
        "bool" or "boolean" => FieldKinds.Checkbox,
        "int2" or "int4" or "int8" or "smallint" or "integer" or "bigint" or "numeric" or "decimal" or "real" or "float4" or "float8" or "double precision" or "money" => FieldKinds.Number,
        "date" => FieldKinds.Date,
        "time" or "time without time zone" or "time with time zone" or "timetz" => FieldKinds.Time,
        "timestamp" or "timestamp without time zone" or "timestamp with time zone" or "timestamptz" => FieldKinds.DateTime,
        "bytea" => null,
        _ => FieldKinds.Text,
    };
    public override string? ExpressionKind(SourceColumn column) => KindOf(column.StoreType ?? "text");
    public override bool IsInteger(TableColumn column) => column.StoreType.ToLowerInvariant() is "int2" or "int4" or "int8" or "smallint" or "integer" or "bigint"
        || column.StoreType.ToLowerInvariant() is "numeric" or "decimal" && column.Scale == 0;
    public override (decimal? Min, decimal? Max) Range(TableColumn column) => column.StoreType.ToLowerInvariant() switch {
        "int2" or "smallint" => (short.MinValue, short.MaxValue),
        "int4" or "integer" => (int.MinValue, int.MaxValue),
        "int8" or "bigint" => (long.MinValue, long.MaxValue),
        _ => (null, null),
    };

    public override object? ToDatabase(object? value, string kind, TableColumn? column)
    {
        var type = column?.StoreType.ToLowerInvariant() ?? "";
        try
        {
            return value switch {
                null => null,
                bool flag when column is not null && KindOf(type) == FieldKinds.Number => ToDatabase(flag ? 1m : 0m, FieldKinds.Number, column),
                string text when type == "uuid" => Guid.TryParse(text, out var id) ? id : throw new DataSourceException("Enter a valid identifier."),
                decimal number => type switch {
                    "int2" or "smallint" => checked((short)Whole(number)),
                    "int4" or "integer" => checked((int)Whole(number)),
                    "int8" or "bigint" => checked((long)Whole(number)),
                    "float4" or "real" => (float)number,
                    "float8" or "double precision" => (double)number,
                    "money" => new TypedValue(number, DbType.Currency),
                    "bool" or "boolean" => number != 0,
                    _ when IsTextType(type) || type is "character varying" or "character" => number.ToString(Invariant),
                    _ => number,
                },
                DateOnly date => new TypedValue(date, DbType.Date),
                TimeOnly time => new TypedValue(time.ToTimeSpan(), DbType.Time),
                DateTimeOffset moment when type is "timestamptz" or "timestamp with time zone" => moment.UtcDateTime,
                DateTimeOffset moment => new TypedValue(DateTime.SpecifyKind(moment.LocalDateTime, DateTimeKind.Unspecified), DbType.DateTime2),
                _ => value,
            };
        }
        catch (OverflowException) { throw new DataSourceException("The number is outside the allowed range."); }
        static decimal Whole(decimal number) => number == decimal.Truncate(number) ? number : throw new DataSourceException("Enter a whole number.");
    }

    public override object Now(DateTime now) => new TypedValue(DateTime.SpecifyKind(now, DateTimeKind.Unspecified), DbType.DateTime2);
    // A timestamptz, the moment itself: Npgsql writes an offset of zero as timestamp with time zone.
    public override object NowUtc(DateTime utc) => new TypedValue(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Unspecified), TimeSpan.Zero), DbType.DateTimeOffset);

    public override TableSchema? ReadTable(DbConnection connection, TableName name)
    {
        long id;
        string schema, table;
        bool view;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT CAST(c.oid AS bigint), n.nspname, c.relname, c.relkind IN ('v', 'm')
                FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE c.relname = @name AND c.relkind IN ('r', 'p', 'v', 'm', 'f')
                  AND ((CAST(@schema AS text) IS NULL AND pg_catalog.pg_table_is_visible(c.oid)) OR n.nspname = @schema)
                """;
            Bind(command, "@name", name.NameIsQuoted ? name.Name : name.Name.ToLowerInvariant());
            Bind(command, "@schema", name.SchemaIsQuoted ? name.Schema : name.Schema?.ToLowerInvariant());
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            (id, schema, table, view) = (reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3));
        }
        var columns = new List<TableColumn>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT a.attname, pg_catalog.format_type(a.atttypid, NULL), NOT a.attnotnull,
                    a.atthasdef OR a.attidentity <> '',
                    a.attidentity <> '' OR pg_catalog.pg_get_serial_sequence(quote_ident(@schema) || '.' || quote_ident(@table), a.attname) IS NOT NULL,
                    a.attgenerated <> '', i.character_maximum_length, i.numeric_precision, i.numeric_scale
                FROM pg_catalog.pg_attribute a
                LEFT JOIN information_schema.columns i ON i.table_schema = @schema AND i.table_name = @table AND i.column_name = a.attname
                WHERE a.attrelid = CAST(@id AS oid) AND a.attnum > 0 AND NOT a.attisdropped ORDER BY a.attnum
                """;
            Bind(command, "@schema", schema); Bind(command, "@table", table); Bind(command, "@id", id);
            using var reader = command.ExecuteReader();
            while (reader.Read()) columns.Add(new(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3),
                reader.GetBoolean(4), reader.GetBoolean(5), false, OptionalInt(reader, 6), OptionalInt(reader, 7), OptionalInt(reader, 8)));
        }
        var keys = new List<IReadOnlyList<string>>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT CAST(i.indexrelid AS bigint), a.attname FROM pg_catalog.pg_index i
                CROSS JOIN LATERAL unnest(i.indkey) WITH ORDINALITY k(attnum, position)
                JOIN pg_catalog.pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum
                WHERE i.indrelid = CAST(@id AS oid) AND i.indisunique AND i.indisvalid AND i.indisready
                  AND i.indpred IS NULL AND i.indexprs IS NULL AND k.position <= i.indnkeyatts
                ORDER BY i.indisprimary DESC, i.indexrelid, k.position
                """;
            Bind(command, "@id", id);
            using var reader = command.ExecuteReader();
            long current = -1;
            List<string>? key = null;
            while (reader.Read())
            {
                if (reader.GetInt64(0) != current) { current = reader.GetInt64(0); keys.Add(key = []); }
                key!.Add(reader.GetString(1));
            }
        }
        return new(new(schema, table) { SchemaIsQuoted = true, NameIsQuoted = true }, view, columns, keys);
    }

    private static int? OptionalInt(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal), Invariant);

    public override string Insert(TableName table, IReadOnlyList<(string Column, string Value)> values, IReadOnlyList<TableColumn> returning)
    {
        var text = new StringBuilder("INSERT INTO ").Append(Quote(table));
        if (values.Count == 0) text.Append(" DEFAULT VALUES");
        else text.Append(" (").AppendJoin(", ", values.Select(v => Quote(v.Column))).Append(") VALUES (").AppendJoin(", ", values.Select(v => v.Value)).Append(')');
        if (returning.Count > 0) text.Append(" RETURNING ").AppendJoin(", ", returning.Select(c => Quote(c.Name)));
        return text.ToString();
    }

    public override string? Lock(TableName table, IReadOnlyList<(string Column, string Marker)> keys)
        => $"SELECT 1 FROM {Quote(table)} WHERE {Match("", keys)} FOR UPDATE";
}
