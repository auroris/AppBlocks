using System.Collections.Concurrent;
using System.Data.Common;
using AppBlocks.Databases;

namespace AppBlocks.Connections;

/// <summary>The databases AppBlocks works with. <see cref="DatabaseKinds"/> knows each one's provider name, schema folder and display name.</summary>
public enum DatabaseDialect { Sqlite, SqlServer, Postgres, Oracle, Oracle11g }

/// <summary>No connection by the name is registered or managed; other failures to make a connection keep their own exception.</summary>
public sealed class ConnectionNotRegisteredException(string message) : InvalidOperationException(message);

/// <summary>Named connections shared by forms, attachments and optional companions.</summary>
public interface IApplicationConnectionFactory
{
    DbConnection CreateConnection(string name);
    DatabaseDialect Dialect(string name);
    void SetDetectedDialect(string name, DatabaseDialect dialect);
}

internal sealed class ApplicationConnections(IServiceProvider provider, AppBlocksBuilder builder, ConnectionCatalog catalog) : IApplicationConnectionFactory
{
    private readonly ConcurrentDictionary<string, (DatabaseDialect Dialect, string Revision)> detected = new(StringComparer.OrdinalIgnoreCase);

    public DbConnection CreateConnection(string name) => builder.CreateConnection(name, catalog, provider);

    public DatabaseDialect Dialect(string name)
    {
        // Managed connections may change provider while the application is running.
        var material = catalog.Find(name);
        var revision = material is null ? "" : ConnectionCatalog.Revision(material);
        if (detected.TryGetValue(name, out var known) && known.Revision == revision) return known.Dialect;
        if (material is not null) return DatabaseKinds.FromProvider(material.Provider);
        if (builder.Dialects.TryGetValue(name, out var declared)) return declared;
        using var connection = CreateConnection(name);
        return DatabaseKinds.Of(connection);
    }

    public void SetDetectedDialect(string name, DatabaseDialect dialect) => detected[name] = (dialect, catalog.Revision(name));
}
