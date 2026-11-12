using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using AppBlocks.Connections;

namespace AppBlocks.DataSources;

/// <summary>A table or view as the database's catalog names it.</summary>
public sealed record TableName(string? Schema, string Name)
{
    public bool SchemaIsQuoted { get; init; }
    public bool NameIsQuoted { get; init; }
    public override string ToString() => Schema is null ? Name : Schema + "." + Name;

    /// <summary>
    /// Reads a table name as an editor writes it: <c>ORDERS</c>, <c>dbo.ORDERS</c>, <c>[dbo].[Order lines]</c> or
    /// <c>"main"."ORDERS"</c>. The result is only looked up in the catalog; the catalog's own spelling is what a
    /// statement quotes.
    /// </summary>
    public static TableName? Parse(string text)
    {
        text = text.Trim();
        var parts = new List<string>();
        var quoted = new List<bool>();
        var i = 0;
        while (i < text.Length)
        {
            string part;
            if (text[i] is '[' or '"' or '`')
            {
                var close = text[i] == '[' ? ']' : text[i];
                var name = new StringBuilder();
                var closed = false;
                i++;
                while (i < text.Length)
                {
                    var c = text[i++];
                    if (c != close) { name.Append(c); continue; }
                    if (i < text.Length && text[i] == close) { name.Append(close); i++; continue; }
                    closed = true;
                    break;
                }
                if (!closed) return null;
                part = name.ToString();
                quoted.Add(true);
            }
            else
            {
                var end = i;
                while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] is '_' or '$' or '#')) end++;
                if (end == i) return null;
                part = text[i..end];
                i = end;
                quoted.Add(false);
            }
            if (part.Length is 0 or > 128 || part.Any(char.IsControl)) return null;
            parts.Add(part);
            if (i == text.Length) break;
            if (text[i] != '.' || ++i == text.Length) return null;
        }
        return parts.Count switch {
            1 => new(null, parts[0]) { NameIsQuoted = quoted[0] },
            2 => new(parts[0], parts[1]) { SchemaIsQuoted = quoted[0], NameIsQuoted = quoted[1] },
            _ => null,
        };
    }
}

/// <summary>A column of the table a data source writes, as the catalog describes it.</summary>
/// <param name="StoreType">The declared type (SQLite) or system type name (SQL Server).</param>
/// <param name="AutoValue">The database assigns a value when an insert leaves it out: an identity or SQLite rowid key.</param>
/// <param name="Computed">A computed or generated column, never written.</param>
public sealed record TableColumn(string Name, string StoreType, bool Nullable, bool HasDefault, bool AutoValue, bool Computed, bool RowVersion,
    int? Length = null, int? Precision = null, int? Scale = null, TableName? Sequence = null);

/// <summary>The catalog's description of a table: its columns and the column sets that identify a row.</summary>
public sealed record TableSchema(TableName Name, bool IsView, IReadOnlyList<TableColumn> Columns, IReadOnlyList<IReadOnlyList<string>> UniqueKeys)
{
    public TableColumn? Column(string name) => Columns.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A result column of the row source, read with a zero-row execution.</summary>
/// <param name="BaseColumn">The table column it reads, when the provider can tell; null for an expression.</param>
/// <param name="BaseCatalog">The database of that table, so a column read from another database is never written to a same-named table here.</param>
public sealed record SourceColumn(string Name, string? StoreType, string? BaseSchema, string? BaseTable, string? BaseColumn,
    int? Size = null, string? BaseCatalog = null);

/// <summary>
/// What differs between databases: identifier quoting, the parameter marker, how a new key comes back, how a row
/// is locked for the length of a write, the catalog queries, and how a canonical value is bound to a column type.
/// Statement text is assembled only from catalog names, which <see cref="Quote(string)"/> quotes, and parameter
/// markers; editor SQL enters only as a whole row source, predicate or custom statement.
/// </summary>
public abstract class SqlDialect
{
    protected static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>Only the dialects in this assembly: each is a reviewed statement builder, not an extension point.</summary>
    private protected SqlDialect() { }

    /// <summary>The DML builder for an application database dialect.</summary>
    public static SqlDialect? For(DatabaseDialect dialect) => dialect switch {
        DatabaseDialect.Sqlite => SqliteDialect.Instance,
        DatabaseDialect.SqlServer => SqlServerDialect.Instance,
        DatabaseDialect.Postgres => PostgreSqlDialect.Instance,
        DatabaseDialect.Oracle => OracleDialect.Instance,
        DatabaseDialect.Oracle11g => OracleDialect.Legacy,
        _ => null,
    };

    public abstract DatabaseDialect Kind { get; }
    public abstract string Quote(string identifier);
    public string Quote(TableName table) => table.Schema is null ? Quote(table.Name) : Quote(table.Schema) + "." + Quote(table.Name);
    public virtual string Marker(string name) => "@" + name;
    /// <summary>Provider-specific command setup, applied to metadata and runtime commands alike.</summary>
    public virtual void ConfigureCommand(DbCommand command) { }
    /// <summary>Compilers such as Oracle EXPLAIN inspect bind placeholders without accepting bound values.</summary>
    public virtual void ConfigureCompilation(DbCommand command) { }
    /// <summary>
    /// Whether the provider gave up waiting for the database, within the command timeout, rather than the database refusing the statement.
    /// Read by property name, as AppBlocks references no provider; a provider that raises a <see cref="TimeoutException"/> is recognised by it.
    /// </summary>
    internal virtual bool IsTimeout(DbException error) => error.InnerException is TimeoutException;
    protected static object? ErrorProperty(DbException error, string name) => error.GetType().GetProperty(name)?.GetValue(error);
    /// <summary>Binds <see cref="UntypedText"/> so the database reads the text as the column's own type.</summary>
    internal virtual void BindUntyped(DbParameter parameter) { }
    /// <summary>The isolation of a write transaction; a dialect without a row lock relies on its own.</summary>
    public virtual IsolationLevel WriteIsolation => IsolationLevel.Unspecified;

    /// <summary>The field kind of a column type; null for a type forms cannot show, such as binary data.</summary>
    public abstract string? KindOf(string storeType);
    /// <summary>The field kind of a result column that no table column backs, such as an expression in the SELECT.</summary>
    public abstract string? ExpressionKind(SourceColumn column);
    public abstract bool IsInteger(TableColumn column);
    public virtual (decimal? Min, decimal? Max) Range(TableColumn column) => (null, null);
    /// <summary>The provider value for a canonical value written to <paramref name="column"/>, or bound for a column-less use when it is null.</summary>
    /// <param name="dateTimes">The wall time a date-time column without a zone receives: server local time or UTC.</param>
    public abstract object? ToDatabase(object? value, string kind, TableColumn? column, DataSourceDateTimes dateTimes = DataSourceDateTimes.ServerLocal);

    /// <summary>The wall time a zone-less column stores for a moment.</summary>
    protected static DateTime WallTime(DateTimeOffset moment, DataSourceDateTimes dateTimes)
        => dateTimes == DataSourceDateTimes.Utc ? DateTime.SpecifyKind(moment.UtcDateTime, DateTimeKind.Unspecified) : moment.LocalDateTime;

    /// <summary>The value bound for <c>:NOW</c>, given the operation's clock reading as its wall time.</summary>
    public abstract object Now(DateTime now);
    /// <summary>
    /// The value bound for <c>:NOW_UTC</c>, given the moment as a UTC date and time: a typed timestamp holding the
    /// UTC wall time, as SQL Server's <c>SYSUTCDATETIME()</c> returns.
    /// </summary>
    public virtual object NowUtc(DateTime utc) => new TypedValue(DateTime.SpecifyKind(utc, DateTimeKind.Utc), DbType.DateTime2);

    public abstract TableSchema? ReadTable(DbConnection connection, TableName name, int commandTimeout = DataSourceOptions.DefaultCommandTimeoutSeconds);
    /// <summary>Resolves an explicitly configured Oracle sequence to its catalog spelling.</summary>
    public virtual TableName? ReadSequence(DbConnection connection, TableName name, int commandTimeout = DataSourceOptions.DefaultCommandTimeoutSeconds) => null;
    /// <summary>Text that compiles <paramref name="statement"/> without running it, for publish validation.</summary>
    public abstract string CompileOnly(string statement);
    /// <summary>Whether <see cref="CompileOnly"/> can check <paramref name="statement"/>; a block or procedure call some databases cannot explain.</summary>
    internal virtual bool Compiles(string statement) => true;
    /// <summary>An editor's custom statement as the provider runs it.</summary>
    internal virtual string Statement(string text) => text;

    /// <summary>The result columns of <paramref name="text"/>, a zero-row probe, without reading a row.</summary>
    internal IReadOnlyList<SourceColumn> Probe(DbConnection connection, string text, SqlParameters parameters)
    {
        using var command = parameters.Command(connection, null, text);
        using var reader = command.ExecuteReader(CommandBehavior.SchemaOnly | CommandBehavior.KeyInfo);
        return reader.GetColumnSchema().Where(c => c.IsHidden != true).Select(c => {
            var table = Blank(c.BaseTableName);
            return new SourceColumn(c.ColumnName, Blank(c.DataTypeName), Blank(c.BaseSchemaName), table,
                Blank(c.BaseColumnName) ?? (table is null || c.IsExpression == true ? null : c.ColumnName), c.ColumnSize is > 0 and < int.MaxValue ? c.ColumnSize : null,
                Blank(c.BaseCatalogName));
        }).ToArray();

        static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>The FROM item: the quoted table, or the SELECT in parentheses on lines of its own so a trailing comment cannot swallow the bracket.</summary>
    public string From(TableName? table, string? query) => table is not null ? Quote(table) : "(\n" + query + "\n)";
    public string ProbeText(string from, string? restriction = null) => $"SELECT * FROM {from} src WHERE 1 = 0{Restrict(restriction)}";

    /// <param name="order">Columns of the row source to sort by, as the schema spells them; null or empty leaves the order to the database.</param>
    public virtual string Select(string from, IEnumerable<string> columns, IReadOnlyList<(string Column, string Marker)> keys, string? restriction, int? limit = null,
        IReadOnlyList<(string Column, bool Descending)>? order = null)
        => $"SELECT {Top(limit)}{string.Join(", ", columns.Select(c => "src." + Quote(c)))} FROM {from} src WHERE {Match("src.", keys)}{Restrict(restriction)}{OrderBy(order)}{Limit(limit)}";

    protected string OrderBy(IReadOnlyList<(string Column, bool Descending)>? order) => order is not { Count: > 0 } ? ""
        : " ORDER BY " + string.Join(", ", order.Select(o => "src." + Quote(o.Column) + (o.Descending ? " DESC" : "")));

    /// <summary>The generated insert, with <c>RETURNING</c> for the keys the database assigns, as SQLite and PostgreSQL write it.</summary>
    public virtual string Insert(TableName table, IReadOnlyList<(string Column, string Value)> values, IReadOnlyList<TableColumn> returning)
    {
        var text = new StringBuilder("INSERT INTO ").Append(Quote(table));
        if (values.Count == 0) text.Append(" DEFAULT VALUES");
        else text.Append(" (").AppendJoin(", ", values.Select(v => Quote(v.Column))).Append(") VALUES (").AppendJoin(", ", values.Select(v => v.Value)).Append(')');
        if (returning.Count > 0) text.Append(" RETURNING ").AppendJoin(", ", returning.Select(c => Quote(c.Name)));
        return text.ToString();
    }

    /// <summary>Reads the values returned by an insert; Oracle overrides this with output parameters.</summary>
    internal virtual async Task<object?[]> ExecuteInsertAsync(DbCommand command, IReadOnlyList<TableColumn> returning, CancellationToken ct)
    {
        if (returning.Count == 0) { await command.ExecuteNonQueryAsync(ct); return []; }
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new DataSourceException("The database did not return the new record's key.");
        return Enumerable.Range(0, returning.Count).Select(reader.GetValue).ToArray();
    }

    public string Update(TableName table, IReadOnlyList<(string Column, string Value)> set, IReadOnlyList<(string Column, string Marker)> keys,
        string? exists)
        => $"UPDATE {Quote(table)} SET {string.Join(", ", set.Select(s => Quote(s.Column) + " = " + s.Value))} WHERE {Where(keys, exists)}";

    public string Delete(TableName table, IReadOnlyList<(string Column, string Marker)> keys, string? exists)
        => $"DELETE FROM {Quote(table)} WHERE {Where(keys, exists)}";

    /// <summary>The row restriction as a condition on the written table: the same row, seen through the row source, passes it.</summary>
    public string Exists(string from, IReadOnlyList<(string Column, string Marker)> sourceKeys, string restriction)
        => $"EXISTS (SELECT 1 FROM {from} src WHERE {Match("src.", sourceKeys)}{Restrict(restriction)})";

    /// <summary>A statement that locks the row until the transaction ends, so a read-compare-write cannot interleave; null when the transaction already does.</summary>
    public virtual string? Lock(TableName table, IReadOnlyList<(string Column, string Marker)> keys) => null;

    /// <summary>
    /// Whether a generated update or delete reports its own row count by a trailing SELECT rather than by the
    /// provider's count, which on SQL Server includes rows that triggers change and is -1 under NOCOUNT.
    /// </summary>
    public virtual bool CountsRowsBySelect => false;
    public virtual string CountRows(string statement) => statement;

    protected virtual string Top(int? limit) => "";
    protected virtual string Limit(int? limit) => limit is { } rows ? " LIMIT " + rows.ToString(Invariant) : "";
    protected string Match(string prefix, IEnumerable<(string Column, string Marker)> keys)
    {
        var conditions = keys.Select(k => prefix + Quote(k.Column) + " = " + k.Marker).ToArray();
        return conditions.Length == 0 ? "1 = 1" : string.Join(" AND ", conditions);
    }
    private string Where(IReadOnlyList<(string Column, string Marker)> keys, string? exists)
        => Match("", keys) + (exists is null ? "" : " AND " + exists);
    private static string Restrict(string? restriction) => restriction is null ? "" : " AND (\n" + restriction + "\n)";

    /// <summary>A catalog query's command, with the engine's timeout.</summary>
    protected static DbCommand CatalogCommand(DbConnection connection, int commandTimeout)
    {
        var command = connection.CreateCommand();
        command.CommandTimeout = commandTimeout;
        return command;
    }

    protected static int? OptionalInt(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal), Invariant);

    protected static void Bind(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    protected static bool IsTextType(string type) => type is "char" or "varchar" or "text" or "nchar" or "nvarchar" or "ntext" or "xml" or "sysname";
}

/// <summary>SQLite: double-quoted names, <c>@</c> markers, <c>RETURNING</c> for new keys, and <c>BEGIN IMMEDIATE</c> as the write lock.</summary>
public sealed class SqliteDialect : SqlDialect
{
    public static readonly SqliteDialect Instance = new();
    private SqliteDialect() { }

    public override DatabaseDialect Kind => DatabaseDialect.Sqlite;
    public override string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private enum Affinity { Integer, Text, Blob, Real, Numeric }

    /// <summary>SQLite's own affinity rules, in its order (section 3.1 of its datatype documentation).</summary>
    private static Affinity AffinityOf(string type)
    {
        var t = type.ToUpperInvariant();
        if (t.Contains("INT")) return Affinity.Integer;
        if (t.Contains("CHAR") || t.Contains("CLOB") || t.Contains("TEXT")) return Affinity.Text;
        if (t.Length == 0 || t.Contains("BLOB")) return Affinity.Blob;
        if (t.Contains("REAL") || t.Contains("FLOA") || t.Contains("DOUB")) return Affinity.Real;
        return Affinity.Numeric;
    }

    public override string? KindOf(string storeType)
    {
        var t = storeType.ToUpperInvariant();
        if (t.Contains("BOOL") || t == "BIT") return FieldKinds.Checkbox;
        if (t.StartsWith("DATETIME", StringComparison.Ordinal) || t.StartsWith("TIMESTAMP", StringComparison.Ordinal)) return FieldKinds.DateTime;
        if (t.StartsWith("DATE", StringComparison.Ordinal)) return FieldKinds.Date;
        if (t.StartsWith("TIME", StringComparison.Ordinal)) return FieldKinds.Time;
        return AffinityOf(t) switch {
            Affinity.Integer or Affinity.Real => FieldKinds.Number,
            Affinity.Numeric => t.Contains("NUM") || t.Contains("DEC") || t.Contains("MONEY") ? FieldKinds.Number : FieldKinds.Text,
            Affinity.Blob => t.Length == 0 ? FieldKinds.Text : null,
            _ => FieldKinds.Text,
        };
    }

    /// <summary>A zero-row probe reports every expression as a BLOB, so an expression is shown as text unless the mapping says otherwise.</summary>
    public override string? ExpressionKind(SourceColumn column) => FieldKinds.Text;

    public override bool IsInteger(TableColumn column) => AffinityOf(column.StoreType) == Affinity.Integer;

    public override object? ToDatabase(object? value, string kind, TableColumn? column, DataSourceDateTimes dateTimes = DataSourceDateTimes.ServerLocal)
    {
        var affinity = column is null ? Affinity.Blob : AffinityOf(column.StoreType);
        return value switch {
            null => null,
            bool flag => flag ? 1L : 0L,
            decimal number when affinity == Affinity.Text => number.ToString(Invariant),
            decimal number when number == decimal.Truncate(number) && number >= long.MinValue && number <= long.MaxValue => (long)number,
            // Bound as text, which a NUMERIC column stores as a number without a detour through double.
            decimal number when affinity == Affinity.Numeric => number,
            decimal number => (double)number,
            DateOnly date => date.ToString("yyyy-MM-dd", Invariant),
            TimeOnly time => FieldValues.ToIdentity(time),
            // SQLite's date functions read this form, zone-less in the data source's convention.
            DateTimeOffset moment => WallTime(moment, dateTimes).ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", Invariant).TrimEnd('.'),
            _ => value,
        };
    }

    public override object Now(DateTime now) => now.ToString("yyyy-MM-dd HH:mm:ss", Invariant);

    // ISO-8601 with Z: SQLite's date functions read it as UTC, and fixed-width text sorts in time order.
    public override object NowUtc(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", Invariant);

    public override string CompileOnly(string statement) => "EXPLAIN " + statement;

    // SQLITE_BUSY or SQLITE_LOCKED once the command timeout has passed: another connection held the database throughout.
    internal override bool IsTimeout(DbException error) => ErrorProperty(error, "SqliteErrorCode") is 5 or 6;

    public override TableSchema? ReadTable(DbConnection connection, TableName name, int commandTimeout = DataSourceOptions.DefaultCommandTimeoutSeconds)
    {
        if (name.Schema is not null && !name.Schema.Equals("main", StringComparison.OrdinalIgnoreCase)) return null;
        string table;
        bool view, withoutRowid;
        using (var command = CatalogCommand(connection, commandTimeout))
        {
            command.CommandText = "SELECT name, type, wr FROM pragma_table_list WHERE schema = 'main' AND type IN ('table', 'view') AND name = @name COLLATE NOCASE";
            Bind(command, "@name", name.Name);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            (table, view, withoutRowid) = (reader.GetString(0), reader.GetString(1) == "view", reader.GetInt64(2) != 0);
        }
        var rows = new List<(string Name, string Type, bool NotNull, bool Default, long Key, long Hidden)>();
        using (var command = CatalogCommand(connection, commandTimeout))
        {
            command.CommandText = "SELECT name, type, \"notnull\", dflt_value IS NOT NULL, pk, hidden FROM pragma_table_xinfo(@name)";
            Bind(command, "@name", table);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.IsDBNull(1) ? "" : reader.GetString(1), reader.GetInt64(2) != 0, reader.GetInt64(3) != 0, reader.GetInt64(4), reader.GetInt64(5)));
        }
        var primary = rows.Where(r => r.Key > 0).OrderBy(r => r.Key).Select(r => r.Name).ToArray();
        // Only a lone INTEGER PRIMARY KEY of a rowid table is an alias for the rowid, which SQLite assigns.
        var rowid = !view && !withoutRowid && primary.Length == 1 && rows.Single(r => r.Name == primary[0]).Type.Equals("INTEGER", StringComparison.OrdinalIgnoreCase);
        var keys = new List<IReadOnlyList<string>>();
        if (primary.Length > 0) keys.Add(primary);
        var indexes = new List<string>();
        using (var command = CatalogCommand(connection, commandTimeout))
        {
            command.CommandText = "SELECT name FROM pragma_index_list(@name) WHERE \"unique\" = 1 AND partial = 0";
            Bind(command, "@name", table);
            using var reader = command.ExecuteReader();
            while (reader.Read()) indexes.Add(reader.GetString(0));
        }
        foreach (var index in indexes)
        {
            using var command = CatalogCommand(connection, commandTimeout);
            command.CommandText = "SELECT name FROM pragma_index_info(@name) ORDER BY seqno";
            Bind(command, "@name", index);
            using var reader = command.ExecuteReader();
            var columns = new List<string?>();
            while (reader.Read()) columns.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
            if (columns.Count > 0 && columns.All(c => c is not null)) keys.Add(columns!);
        }
        return new TableSchema(new TableName(null, table), view, rows.Where(r => r.Hidden != 1).Select(r => new TableColumn(
            r.Name, r.Type, Nullable: !r.NotNull && !(rowid && r.Name == primary[0]), HasDefault: r.Default, AutoValue: rowid && r.Name == primary[0],
            Computed: r.Hidden is 2 or 3, RowVersion: false, Length: Length(r.Type))).ToArray(), keys);
    }

    private static int? Length(string type)
    {
        var open = type.IndexOf('(');
        var close = open < 0 ? -1 : type.IndexOfAny([')', ','], open);
        return close > open && int.TryParse(type[(open + 1)..close], NumberStyles.None, Invariant, out var length) && length > 0 ? length : null;
    }
}

/// <summary>
/// SQL Server: bracketed names, <c>@</c> markers, <c>OUTPUT INSERTED</c> into a table variable for new keys (which
/// also works on tables with triggers), and <c>UPDLOCK, HOLDLOCK</c> on the row for the length of a write.
/// </summary>
public sealed class SqlServerDialect : SqlDialect
{
    public static readonly SqlServerDialect Instance = new();
    private SqlServerDialect() { }

    public override DatabaseDialect Kind => DatabaseDialect.SqlServer;
    public override string Quote(string identifier) => "[" + identifier.Replace("]", "]]") + "]";
    public override IsolationLevel WriteIsolation => IsolationLevel.ReadCommitted;

    public override string? KindOf(string storeType) => storeType.ToLowerInvariant() switch {
        "bit" => FieldKinds.Checkbox,
        "tinyint" or "smallint" or "int" or "bigint" or "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real" => FieldKinds.Number,
        "date" => FieldKinds.Date,
        "time" => FieldKinds.Time,
        "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" => FieldKinds.DateTime,
        "binary" or "varbinary" or "image" or "timestamp" or "rowversion" or "geography" or "geometry" or "hierarchyid" or "sql_variant" => null,
        _ => FieldKinds.Text,
    };

    public override string? ExpressionKind(SourceColumn column) => KindOf(column.StoreType ?? "nvarchar");

    public override bool IsInteger(TableColumn column) => column.StoreType.ToLowerInvariant() is "tinyint" or "smallint" or "int" or "bigint";

    public override (decimal? Min, decimal? Max) Range(TableColumn column) => column.StoreType.ToLowerInvariant() switch {
        "tinyint" => (0, byte.MaxValue),
        "smallint" => (short.MinValue, short.MaxValue),
        "int" => (int.MinValue, int.MaxValue),
        "bigint" => (long.MinValue, long.MaxValue),
        "smallmoney" => (-214748.3648m, 214748.3647m),
        "money" => (-922337203685477.5808m, 922337203685477.5807m),
        "decimal" or "numeric" when column.Precision is { } p and > 0 and <= 28 => Bounds(p, column.Scale ?? 0),
        _ => (null, null),
    };

    private static (decimal?, decimal?) Bounds(int precision, int scale)
    {
        var max = (decimal)Math.Pow(10, precision - scale) - (decimal)Math.Pow(10, -scale);
        return (-max, max);
    }

    public override object? ToDatabase(object? value, string kind, TableColumn? column, DataSourceDateTimes dateTimes = DataSourceDateTimes.ServerLocal)
    {
        var type = column?.StoreType.ToLowerInvariant() ?? "";
        try
        {
            return value switch {
                null => null,
                string text when type == "uniqueidentifier" => Guid.TryParse(text, out var guid) ? guid : throw new DataSourceException("Enter a valid identifier."),
                string text when type is "char" or "varchar" or "text" => new TypedValue(text, DbType.AnsiString),
                string text => text,
                bool flag => flag,
                decimal number when IsTextType(type) => number.ToString(Invariant),
                decimal number => type switch {
                    "tinyint" => checked((byte)Whole(number)),
                    "smallint" => checked((short)Whole(number)),
                    "int" => checked((int)Whole(number)),
                    "bigint" => checked((long)Whole(number)),
                    "float" => (double)number,
                    "real" => (float)number,
                    "bit" => number != 0,
                    _ => number,
                },
                DateOnly date when IsTextType(type) => date.ToString("yyyy-MM-dd", Invariant),
                DateOnly date => new TypedValue(Stored(date.ToDateTime(TimeOnly.MinValue), type), type is "datetime" or "smalldatetime" ? DbType.DateTime : type == "datetime2" ? DbType.DateTime2 : DbType.Date),
                TimeOnly time when IsTextType(type) => FieldValues.ToIdentity(time),
                TimeOnly time => new TypedValue(time.ToTimeSpan(), DbType.Time),
                DateTimeOffset moment when IsTextType(type) => moment.ToString("O", Invariant),
                DateTimeOffset moment => type switch {
                    "datetimeoffset" => new TypedValue(moment, DbType.DateTimeOffset),
                    "date" => new TypedValue(WallTime(moment, dateTimes).Date, DbType.Date),
                    "datetime" or "smalldatetime" => new TypedValue(Stored(WallTime(moment, dateTimes), type), DbType.DateTime),
                    _ => new TypedValue(WallTime(moment, dateTimes), DbType.DateTime2),
                },
                byte[] bytes => new TypedValue(bytes, DbType.Binary),
                _ => value,
            };
        }
        catch (OverflowException) { throw new DataSourceException("The number is outside the allowed range."); }

        static decimal Whole(decimal number) => number == decimal.Truncate(number) ? number : throw new DataSourceException("Enter a whole number.");

        // The legacy types cover less than DateTime does, and SqlClient reports a date outside them as a type error.
        static DateTime Stored(DateTime moment, string type) => type switch {
            "datetime" when moment < new DateTime(1753, 1, 1) => throw new DataSourceException("The date is before 1753, the earliest this column stores."),
            "smalldatetime" when moment < new DateTime(1900, 1, 1) || moment > new DateTime(2079, 6, 6) => throw new DataSourceException("The date is outside 1900 to 2079, the range this column stores."),
            _ => moment,
        };
    }

    public override bool CountsRowsBySelect => true;
    public override string CountRows(string statement) => statement + ";\nSELECT @@ROWCOUNT;";

    public override object Now(DateTime now) => new TypedValue(now, DbType.DateTime2);

    public override string CompileOnly(string statement) => "SET NOEXEC ON;\n" + statement + "\nSET NOEXEC OFF;";

    // SqlClient reports its own timeout as error number -2.
    internal override bool IsTimeout(DbException error) => ErrorProperty(error, "Number") is -2;

    public override TableSchema? ReadTable(DbConnection connection, TableName name, int commandTimeout = DataSourceOptions.DefaultCommandTimeoutSeconds)
    {
        string schema, table;
        bool view;
        int id;
        using (var command = CatalogCommand(connection, commandTimeout))
        {
            // OBJECT_ID resolves the name the way the connection's own statements would, default schema included.
            command.CommandText = "SELECT OBJECT_SCHEMA_NAME(o.object_id), o.name, o.type, o.object_id FROM sys.objects o WHERE o.object_id = OBJECT_ID(@name) AND o.type IN ('U', 'V')";
            Bind(command, "@name", Quote(name));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            (schema, table, view, id) = (reader.GetString(0), reader.GetString(1), reader.GetString(2).Trim() == "V", reader.GetInt32(3));
        }
        var columns = new List<TableColumn>();
        using (var command = CatalogCommand(connection, commandTimeout))
        {
            command.CommandText = """
                SELECT c.name, COALESCE(TYPE_NAME(c.system_type_id), TYPE_NAME(c.user_type_id)), c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity, c.is_computed,
                    CAST(CASE WHEN c.default_object_id <> 0 THEN 1 ELSE 0 END AS bit)
                FROM sys.columns c WHERE c.object_id = @id ORDER BY c.column_id
                """;
            Bind(command, "@id", id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var type = reader.GetString(1);
                var bytes = reader.GetInt16(2);
                // max_length is a byte count, -1 for max; only the sized character and binary types have a length.
                int? length = bytes < 0 ? null : type switch { "nchar" or "nvarchar" => bytes / 2, "char" or "varchar" or "binary" or "varbinary" => bytes, _ => null };
                columns.Add(new TableColumn(reader.GetString(0), type, reader.GetBoolean(5), reader.GetBoolean(8), reader.GetBoolean(6), reader.GetBoolean(7),
                    RowVersion: type.Equals("timestamp", StringComparison.OrdinalIgnoreCase), Length: length, Precision: reader.GetByte(3), Scale: reader.GetByte(4)));
            }
        }
        var keys = new List<IReadOnlyList<string>>();
        using (var command = CatalogCommand(connection, commandTimeout))
        {
            command.CommandText = """
                SELECT i.index_id, c.name FROM sys.indexes i
                JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE i.object_id = @id AND (i.is_primary_key = 1 OR i.is_unique = 1) AND i.has_filter = 0 AND ic.is_included_column = 0
                ORDER BY i.is_primary_key DESC, i.index_id, ic.key_ordinal
                """;
            Bind(command, "@id", id);
            using var reader = command.ExecuteReader();
            var current = -1;
            List<string>? key = null;
            while (reader.Read())
            {
                if (reader.GetInt32(0) != current) { current = reader.GetInt32(0); keys.Add(key = []); }
                key!.Add(reader.GetString(1));
            }
        }
        return new TableSchema(new TableName(schema, table), view, columns, keys);
    }

    /// <summary>The declaration of a column's type for the table variable that receives new keys.</summary>
    public static string Declaration(TableColumn column)
    {
        var type = column.StoreType.ToLowerInvariant();
        return type switch {
            "char" or "varchar" or "binary" or "varbinary" or "nchar" or "nvarchar" => $"{type}({(column.Length is { } length ? length.ToString(Invariant) : "max")})",
            "decimal" or "numeric" => $"{type}({column.Precision ?? 18}, {column.Scale ?? 0})",
            "datetime2" or "time" or "datetimeoffset" => $"{type}({column.Scale ?? 7})",
            "timestamp" => "binary(8)",
            _ => type,
        };
    }

    public override string Insert(TableName table, IReadOnlyList<(string Column, string Value)> values, IReadOnlyList<TableColumn> returning)
    {
        var text = new StringBuilder();
        if (returning.Count > 0)
            text.Append("DECLARE @inserted TABLE (").AppendJoin(", ", returning.Select(c => Quote(c.Name) + " " + Declaration(c))).Append(");\n");
        text.Append("INSERT INTO ").Append(Quote(table));
        if (values.Count > 0) text.Append(" (").AppendJoin(", ", values.Select(v => Quote(v.Column))).Append(')');
        if (returning.Count > 0) text.Append(" OUTPUT ").AppendJoin(", ", returning.Select(c => "INSERTED." + Quote(c.Name))).Append(" INTO @inserted");
        text.Append(values.Count > 0 ? " VALUES (" + string.Join(", ", values.Select(v => v.Value)) + ")" : " DEFAULT VALUES").Append(';');
        if (returning.Count > 0) text.Append("\nSELECT ").AppendJoin(", ", returning.Select(c => Quote(c.Name))).Append(" FROM @inserted;");
        return text.ToString();
    }

    public override string? Lock(TableName table, IReadOnlyList<(string Column, string Marker)> keys)
        => $"SELECT 1 FROM {Quote(table)} WITH (UPDLOCK, HOLDLOCK, ROWLOCK) WHERE {Match("", keys)}";

    protected override string Top(int? limit) => limit is { } rows ? "TOP (" + rows.ToString(Invariant) + ") " : "";
    protected override string Limit(int? limit) => "";
}
