using System.Collections.Concurrent;
using System.Data.Common;
using UmbracoAppHost.Databases;

namespace AppBlocks.Connections;

public enum DatabaseDialect { Sqlite, SqlServer, Postgres, Oracle, Oracle11g }

/// <summary>Named connections shared by forms, attachments and optional companions.</summary>
public interface IApplicationConnectionFactory
{
    DbConnection CreateConnection(string name);
    DatabaseDialect Dialect(string name);
    void SetDetectedDialect(string name, DatabaseDialect dialect);
}

public sealed class AppBlocksBuilder
{
    internal Dictionary<string, Func<IServiceProvider, DbConnection>> Connections { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, DatabaseDialect> Dialects { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal event Action<string>? ConnectionAdded;
    public IEnumerable<string> ConnectionNames => Connections.Keys;

    public AppBlocksBuilder AddConnection(string name, Func<IServiceProvider, DbConnection> factory, DatabaseDialect? dialect = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);
        Connections[name] = factory;
        Dialects.Remove(name);
        if (dialect is { } declared) Dialects[name] = declared;
        ConnectionAdded?.Invoke(name);
        return this;
    }
}

internal sealed class ApplicationConnections(IServiceProvider provider, AppBlocksBuilder builder, ConnectionCatalog catalog) : IApplicationConnectionFactory
{
    private readonly ConcurrentDictionary<string, (DatabaseDialect Dialect, string Revision)> detected = new(StringComparer.OrdinalIgnoreCase);

    public DbConnection CreateConnection(string name) => catalog.Find(name) is { } material
        ? ConnectionCatalog.Create(material)
        : builder.Connections.TryGetValue(name, out var factory) ? factory(provider)
        : throw new InvalidOperationException($"The connection {name} is not registered with AppBlocks.");

    public DatabaseDialect Dialect(string name)
    {
        // Managed connections may change provider while the application is running.
        var material = catalog.Find(name);
        var revision = material is null ? "" : ConnectionCatalog.Revision(material);
        if (detected.TryGetValue(name, out var known) && known.Revision == revision) return known.Dialect;
        if (material is not null) return material.Provider switch
        {
            "sqlite" => DatabaseDialect.Sqlite, "sqlServer" => DatabaseDialect.SqlServer,
            "postgres" => DatabaseDialect.Postgres, "oracle" => DatabaseDialect.Oracle,
            "oracle11g" => DatabaseDialect.Oracle11g,
            _ => throw new InvalidOperationException("Unsupported database provider."),
        };
        if (builder.Dialects.TryGetValue(name, out var declared)) return declared;
        using var connection = CreateConnection(name);
        return ApplicationDatabase.Dialect(connection) switch
        {
            "sqlite" => DatabaseDialect.Sqlite, "sqlserver" => DatabaseDialect.SqlServer,
            "postgresql" => DatabaseDialect.Postgres, "oracle" => DatabaseDialect.Oracle,
            _ => throw new InvalidOperationException("Unsupported database provider."),
        };
    }

    public void SetDetectedDialect(string name, DatabaseDialect dialect) => detected[name] = (dialect, catalog.Revision(name));
}
