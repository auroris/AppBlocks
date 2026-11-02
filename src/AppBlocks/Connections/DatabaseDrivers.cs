using System.Data.Common;

namespace AppBlocks.Connections;

/// <summary>
/// Opens PostgreSQL and Oracle connections through the driver the site installed. AppBlocks references neither driver
/// at compile time and ships neither in its package: a site adds the Npgsql or Oracle.ManagedDataAccess.Core package
/// when it connects to that database, as a site using Interactive Reports does, and a lookup here that finds nothing
/// names the package to add. SQLite and SQL Server need no lookup, because Umbraco brings Microsoft.Data.Sqlite and
/// Microsoft.Data.SqlClient into every site.
/// </summary>
internal static class DatabaseDrivers
{
    /// <summary>The site has not installed the driver a connection needs; the message names the package.</summary>
    public sealed class MissingDriverException(string message) : InvalidOperationException(message);

    /// <summary>A driver by the name of its connection type, which the site's own dependency graph resolves.</summary>
    internal sealed class Driver(string displayName, string package, params string[] typeNames)
    {
        private Type? connectionType;
        public string DisplayName => displayName;
        public string Package => package;

        /// <summary>The connection type, found once; a miss is looked up again, as nothing says the driver is there to stay missing.</summary>
        public Type? ConnectionType => connectionType ??= typeNames.Select(name => Type.GetType(name, throwOnError: false)).FirstOrDefault(type => type is not null);

        public DbConnection Create(string connectionString)
        {
            var type = ConnectionType ?? throw new MissingDriverException(
                $"No {DisplayName} driver is installed. Add the {Package} package to the site to connect to {DisplayName}.");
            var connection = (DbConnection)Activator.CreateInstance(type)!;
            try { connection.ConnectionString = connectionString; }
            catch { connection.Dispose(); throw; }
            return connection;
        }
    }

    public static readonly Driver PostgreSql = new("PostgreSQL", "Npgsql", "Npgsql.NpgsqlConnection, Npgsql");
    public static readonly Driver Oracle = new("Oracle", "Oracle.ManagedDataAccess.Core",
        "Oracle.ManagedDataAccess.Client.OracleConnection, Oracle.ManagedDataAccess");
}
