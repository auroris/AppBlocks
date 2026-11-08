using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace AppBlocks.Connections;

/// <summary>
/// Every name a database kind goes by, and the conversions between them: the provider name that connections.json, configuration and
/// the screens use (<c>sqlite</c>, <c>sqlServer</c>, <c>postgres</c>, <c>oracle</c>, <c>oracle11g</c>), the folder an application
/// schema keeps its scripts in (<c>sqlite</c>, <c>sqlserver</c>, <c>postgresql</c>, <c>oracle</c> for both Oracle modes), and the name a
/// person reads. Code that needs one of these names asks here.
/// </summary>
public static class DatabaseKinds
{
    /// <summary>Every kind, in the order the Connections screen offers them.</summary>
    public static IReadOnlyList<DatabaseDialect> All { get; } =
        [DatabaseDialect.SqlServer, DatabaseDialect.Sqlite, DatabaseDialect.Postgres, DatabaseDialect.Oracle11g, DatabaseDialect.Oracle];

    /// <summary>The provider name, as connections.json, configuration and <see cref="ConnectionInfo.Provider"/> spell it.</summary>
    public static string ProviderName(this DatabaseDialect dialect) => dialect switch {
        DatabaseDialect.Sqlite => "sqlite",
        DatabaseDialect.SqlServer => "sqlServer",
        DatabaseDialect.Postgres => "postgres",
        DatabaseDialect.Oracle => "oracle",
        DatabaseDialect.Oracle11g => "oracle11g",
        _ => throw new ArgumentOutOfRangeException(nameof(dialect)),
    };

    /// <summary>The folder an application schema keeps this kind's scripts in. Both Oracle modes share <c>oracle</c>.</summary>
    public static string SchemaFolder(this DatabaseDialect dialect) => dialect switch {
        DatabaseDialect.Sqlite => "sqlite",
        DatabaseDialect.SqlServer => "sqlserver",
        DatabaseDialect.Postgres => "postgresql",
        DatabaseDialect.Oracle or DatabaseDialect.Oracle11g => "oracle",
        _ => throw new ArgumentOutOfRangeException(nameof(dialect)),
    };

    /// <summary>The name a person reads, such as <c>SQL Server</c> or <c>Oracle 11.2</c>.</summary>
    public static string DisplayName(this DatabaseDialect dialect) => dialect switch {
        DatabaseDialect.Sqlite => "SQLite",
        DatabaseDialect.SqlServer => "SQL Server",
        DatabaseDialect.Postgres => "PostgreSQL",
        DatabaseDialect.Oracle => "Oracle 12+",
        DatabaseDialect.Oracle11g => "Oracle 11.2",
        _ => throw new ArgumentOutOfRangeException(nameof(dialect)),
    };

    /// <summary>The name a person reads for a provider name: <c>Unknown</c> for <see cref="ConnectionCatalog.UnknownProvider"/>, and the text itself for a name no kind has.</summary>
    public static string DisplayName(string provider) => TryFromProvider(provider, out var dialect) ? dialect.DisplayName()
        : provider == ConnectionCatalog.UnknownProvider ? "Unknown" : provider;

    /// <summary>The kind a provider name, as <see cref="ProviderName"/> spells it, stands for.</summary>
    public static bool TryFromProvider(string? provider, out DatabaseDialect dialect)
    {
        foreach (var kind in All)
            if (kind.ProviderName() == provider) { dialect = kind; return true; }
        dialect = default;
        return false;
    }

    /// <inheritdoc cref="TryFromProvider"/>
    /// <exception cref="InvalidOperationException">No kind has that provider name.</exception>
    public static DatabaseDialect FromProvider(string provider)
        => TryFromProvider(provider, out var dialect) ? dialect : throw new InvalidOperationException("Unsupported database provider.");

    /// <summary>The kind whose scripts an application schema folder holds; <c>oracle</c> is the automatic Oracle mode.</summary>
    /// <exception cref="InvalidOperationException">No kind has that folder.</exception>
    public static DatabaseDialect FromSchemaFolder(string folder) => folder switch {
        "sqlite" => DatabaseDialect.Sqlite,
        "sqlserver" => DatabaseDialect.SqlServer,
        "postgresql" => DatabaseDialect.Postgres,
        "oracle" => DatabaseDialect.Oracle,
        _ => throw new InvalidOperationException("Unsupported database provider."),
    };

    /// <summary>
    /// The provider name for any spelling a site may write in configuration or send from the screen: a provider name in any case, or a
    /// driver's invariant name such as <c>Microsoft.Data.SqlClient</c> or <c>Npgsql</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The value names no supported database.</exception>
    public static string NormalizeProvider(string value) => value.Trim().ToLowerInvariant() switch {
        "sqlite" or "microsoft.data.sqlite" => DatabaseDialect.Sqlite.ProviderName(),
        "sqlserver" or "microsoft.data.sqlclient" or "system.data.sqlclient" => DatabaseDialect.SqlServer.ProviderName(),
        "postgres" or "postgresql" or "npgsql" => DatabaseDialect.Postgres.ProviderName(),
        "oracle" or "oracle.manageddataaccess.client" or "oracle.manageddataaccess.core" => DatabaseDialect.Oracle.ProviderName(),
        "oracle11g" => DatabaseDialect.Oracle11g.ProviderName(),
        _ => throw new ArgumentException("Choose SQLite, SQL Server, PostgreSQL, Oracle 11.2 or Oracle 12+."),
    };

    /// <summary>
    /// The kind of a connection by its type, without opening it. PostgreSQL and Oracle connections are recognized by their type's name,
    /// since AppBlocks references neither driver; an Oracle connection is the automatic mode, which opening it may narrow to 11.2.
    /// </summary>
    /// <exception cref="InvalidOperationException">The connection is none of the supported databases.</exception>
    public static DatabaseDialect Of(DbConnection connection) => connection switch {
        SqliteConnection => DatabaseDialect.Sqlite,
        SqlConnection => DatabaseDialect.SqlServer,
        _ => connection.GetType().Name switch {
            "NpgsqlConnection" => DatabaseDialect.Postgres,
            "OracleConnection" => DatabaseDialect.Oracle,
            _ => throw new InvalidOperationException(
                $"AppBlocks works with SQLite, SQL Server, PostgreSQL and Oracle; {connection.GetType().FullName} is none of them."),
        },
    };
}
