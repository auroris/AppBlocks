using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace AppBlocks.Databases;

/// <summary>What a database says about one of a feature's tables.</summary>
public enum InspectedTableState
{
    /// <summary>This account can read and write it, as far as the database will say without trying.</summary>
    Ready,
    /// <summary>It is not there, or this account cannot see it.</summary>
    Missing,
    /// <summary>It is there, and this account is refused reading or writing it.</summary>
    NoAccess,
}

public sealed record InspectedTable(string Name, InspectedTableState State);

/// <summary>What a read-only look at a database found.</summary>
/// <param name="Account">The database user this connection signs in as, when the database says.</param>
/// <param name="CanCreateTables">Whether the account may create the tables and use them; null when the database cannot tell without trying.</param>
/// <param name="ServerVersion">The server's version text, which decides between Oracle's two forms of a script.</param>
public sealed record DatabaseInspection(bool Reachable, string? Account, bool? CanCreateTables, string? ServerVersion, IReadOnlyList<InspectedTable> Tables)
{
    public InspectedTableState StateOf(string table) => Tables.FirstOrDefault(t => t.Name.Equals(table, StringComparison.OrdinalIgnoreCase))?.State ?? InspectedTableState.Missing;
}

/// <summary>How a database refused a statement, when it is about its tables or rights rather than a passing failure.</summary>
public enum DatabaseRefusal { None, Missing, Denied }

/// <summary>
/// Looks at a database without changing it: empty reads, and each dialect's own question about the account's rights. It
/// opens a SQLite file only if it exists. This is what lets the setup screen and the features' readiness checks run as
/// often as they like. PostgreSQL's and Oracle's questions are written from their documentation; only SQLite and SQL
/// Server are exercised by this repository's tests.
/// </summary>
public static class DatabaseInspector
{
    private const int TimeoutSeconds = 10;

    /// <param name="connection">An unopened connection this call owns the use of. It must not be one that ensures a schema when it opens.</param>
    /// <param name="legacyOracle">The connection is in Oracle 11.2 mode, whose identity needs a trigger as well as a sequence.</param>
    public static async Task<DatabaseInspection> InspectAsync(DbConnection connection, IReadOnlyList<string> tables, bool legacyOracle, CancellationToken ct)
    {
        var dialect = ApplicationDatabase.Dialect(connection);
        var readOnlyFile = false;
        if (connection is SqliteConnection sqlite)
        {
            var builder = new SqliteConnectionStringBuilder(sqlite.ConnectionString);
            readOnlyFile = builder.Mode == SqliteOpenMode.ReadOnly;
            if (builder.Mode == SqliteOpenMode.ReadWriteCreate) { builder.Mode = SqliteOpenMode.ReadWrite; sqlite.ConnectionString = builder.ToString(); }
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        try { await connection.OpenAsync(timeout.Token); }
        catch (SqliteException error) when (error.SqliteErrorCode == 14)
        {
            // SQLITE_CANTOPEN: the file is not there yet, and a store creates it with its folder on first use.
            return new(true, null, readOnlyFile ? false : null, null, tables.Select(table => new InspectedTable(table, InspectedTableState.Missing)).ToArray());
        }
        catch (Exception error) when (!ct.IsCancellationRequested && error is DbException or InvalidOperationException or OperationCanceledException or TimeoutException)
        {
            return new(false, null, null, null, tables.Select(table => new InspectedTable(table, InspectedTableState.Missing)).ToArray());
        }
        var version = connection is SqliteConnection ? null : connection.ServerVersion;
        var found = new List<InspectedTable>();
        foreach (var table in tables)
        {
            var state = await TableStateAsync(connection, dialect, table, timeout.Token);
            if (state == InspectedTableState.Ready && await MayWriteAsync(connection, dialect, table, timeout.Token) == false) state = InspectedTableState.NoAccess;
            found.Add(new(table, state));
        }
        var (account, canCreate) = await AccountAsync(connection, dialect, legacyOracle || MajorVersion(version) < 12, timeout.Token);
        return new(true, account, canCreate, version, found);
    }

    /// <summary>Whether this account can read every table, by an empty read of each on an open connection.</summary>
    public static async Task<bool> TablesReadableAsync(DbConnection connection, IEnumerable<string> tables, CancellationToken ct)
    {
        var dialect = ApplicationDatabase.Dialect(connection);
        foreach (var table in tables)
            if (await TableStateAsync(connection, dialect, table, ct) != InspectedTableState.Ready) return false;
        return true;
    }

    /// <summary>Whether a failure says a table is missing or an account was refused, by each provider's own error number.</summary>
    public static DatabaseRefusal Refusal(Exception? error)
    {
        for (; error is not null; error = error.InnerException)
        {
            if (error is SqlException sql) return sql.Number switch {
                208 => DatabaseRefusal.Missing,
                // Permission denied on an object, column or database; CREATE TABLE denied; no rights on the schema; object not found or no permission.
                229 or 230 or 262 or 297 or 300 or 916 or 2760 or 1088 or 15151 => DatabaseRefusal.Denied,
                _ => DatabaseRefusal.None,
            };
            if (error is SqliteException sqlite)
                return sqlite.SqliteErrorCode == 8 ? DatabaseRefusal.Denied
                    : sqlite.SqliteErrorCode == 1 && sqlite.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase) ? DatabaseRefusal.Missing : DatabaseRefusal.None;
            if (error is DbException { SqlState: "42P01" }) return DatabaseRefusal.Missing;
            if (error is DbException { SqlState: "42501" }) return DatabaseRefusal.Denied;
            if (error is DbException oracle && oracle.GetType().Name == "OracleException")
                return oracle.GetType().GetProperty("Number")?.GetValue(oracle) switch {
                    942 => DatabaseRefusal.Missing,
                    1031 or 1950 => DatabaseRefusal.Denied,
                    _ => DatabaseRefusal.None,
                };
        }
        return DatabaseRefusal.None;
    }

    private static int MajorVersion(string? version) => int.TryParse(version?.Split('.')[0], out var major) ? major : int.MaxValue;

    private static async Task<InspectedTableState> TableStateAsync(DbConnection connection, string dialect, string table, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = TimeoutSeconds;
        command.CommandText = $"SELECT * FROM {Quote(dialect, table)} WHERE 1 = 0";
        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            return InspectedTableState.Ready;
        }
        catch (DbException error) { return Refusal(error) == DatabaseRefusal.Denied ? InspectedTableState.NoAccess : InspectedTableState.Missing; }
    }

    /// <summary>Whether the account may write to the table; null when the database cannot say without trying.</summary>
    private static async Task<bool?> MayWriteAsync(DbConnection connection, string dialect, string table, CancellationToken ct)
    {
        if (connection is SqliteConnection sqlite) return SQLitePCL.raw.sqlite3_db_readonly(sqlite.Handle, "main") != 1;
        var quoted = Quote(dialect, table).Replace("'", "''", StringComparison.Ordinal);
        var check = dialect switch {
            "sqlserver" => $"SELECT CASE WHEN HAS_PERMS_BY_NAME(N'{quoted}', 'OBJECT', 'INSERT') = 1 AND HAS_PERMS_BY_NAME(N'{quoted}', 'OBJECT', 'UPDATE') = 1 AND HAS_PERMS_BY_NAME(N'{quoted}', 'OBJECT', 'DELETE') = 1 THEN 1 ELSE 0 END",
            "postgresql" => $"SELECT CASE WHEN has_table_privilege('{quoted}', 'INSERT') AND has_table_privilege('{quoted}', 'UPDATE') AND has_table_privilege('{quoted}', 'DELETE') THEN 1 ELSE 0 END",
            _ => null,
        };
        return check is null ? null : await ScalarAsync(connection, check, ct) is { } value ? Convert.ToInt32(value) == 1 : null;
    }

    /// <summary>Who the account is, and whether it may create the tables and then read and write them.</summary>
    private static async Task<(string? Account, bool? CanCreate)> AccountAsync(DbConnection connection, string dialect, bool legacyOracle, CancellationToken ct)
    {
        switch (dialect)
        {
            case "sqlite":
                return (null, SQLitePCL.raw.sqlite3_db_readonly(((SqliteConnection)connection).Handle, "main") != 1);
            case "sqlserver":
                var rights = await ScalarAsync(connection, """
                    SELECT CASE WHEN HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE TABLE') = 1 AND HAS_PERMS_BY_NAME('dbo', 'SCHEMA', 'ALTER') = 1
                        AND HAS_PERMS_BY_NAME('dbo', 'SCHEMA', 'SELECT') = 1 AND HAS_PERMS_BY_NAME('dbo', 'SCHEMA', 'INSERT') = 1
                        AND HAS_PERMS_BY_NAME('dbo', 'SCHEMA', 'UPDATE') = 1 AND HAS_PERMS_BY_NAME('dbo', 'SCHEMA', 'DELETE') = 1 AND HAS_PERMS_BY_NAME('dbo', 'SCHEMA', 'REFERENCES') = 1 THEN 1 ELSE 0 END
                    """, ct);
                return (Convert.ToString(await ScalarAsync(connection, "SELECT USER_NAME()", ct)), rights is null ? null : Convert.ToInt32(rights) == 1);
            case "postgresql":
                var schema = await ScalarAsync(connection, "SELECT CASE WHEN has_schema_privilege(current_schema(), 'CREATE') THEN 1 ELSE 0 END", ct);
                return (Convert.ToString(await ScalarAsync(connection, "SELECT current_user", ct)), schema is null ? null : Convert.ToInt32(schema) == 1);
            default:
                var needed = legacyOracle ? "('CREATE TABLE', 'CREATE SEQUENCE', 'CREATE TRIGGER')" : "('CREATE TABLE', 'CREATE SEQUENCE')";
                var held = await ScalarAsync(connection, $"SELECT COUNT(*) FROM SESSION_PRIVS WHERE PRIVILEGE IN {needed}", ct);
                return (Convert.ToString(await ScalarAsync(connection, "SELECT USER FROM DUAL", ct)), held is null ? null : Convert.ToInt32(held) == (legacyOracle ? 3 : 2));
        }
    }

    private static async Task<object?> ScalarAsync(DbConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = TimeoutSeconds;
        command.CommandText = sql;
        try { return await command.ExecuteScalarAsync(ct) is { } value and not DBNull ? value : null; }
        catch (DbException) { return null; }
    }

    private static string Quote(string dialect, string table) => dialect == "sqlserver" ? $"[{table}]" : $"\"{table}\"";
}
