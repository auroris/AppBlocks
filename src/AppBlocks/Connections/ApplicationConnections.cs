using System.Collections.Concurrent;
using System.Data.Common;
using AppBlocks.Databases;

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
    /// <summary>The application databases, apart from their factories: a configured or saved connection string never runs its factory.</summary>
    internal Dictionary<string, ApplicationSchema> Schemas { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal event Action<string>? ConnectionAdded;
    public IEnumerable<string> ConnectionNames => Connections.Keys;

    public AppBlocksBuilder AddConnection(string name, Func<IServiceProvider, DbConnection> factory, DatabaseDialect? dialect = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);
        Connections[name] = factory;
        Dialects.Remove(name);
        Schemas.Remove(name);
        if (dialect is { } declared) Dialects[name] = declared;
        ConnectionAdded?.Invoke(name);
        return this;
    }

    /// <summary>A connection to <paramref name="name"/> for use, which ensures the schema when it opens if the name is an application database.</summary>
    internal DbConnection Create(string name, Func<DbConnection> create, IServiceProvider provider)
        => Schemas.TryGetValue(name, out var schema) ? ApplicationDatabase.EnsureOnOpen(create(), create, name, schema, provider) : create();

    internal DbConnection CreateRegistered(string name, IServiceProvider provider) => Create(name, () => Connections[name](provider), provider);
}

internal sealed class ApplicationConnections(IServiceProvider provider, AppBlocksBuilder builder, ConnectionCatalog catalog) : IApplicationConnectionFactory
{
    private readonly ConcurrentDictionary<string, (DatabaseDialect Dialect, string Revision)> detected = new(StringComparer.OrdinalIgnoreCase);

    public DbConnection CreateConnection(string name) => catalog.Find(name) is { } material
        ? builder.Create(name, () => ConnectionCatalog.Create(material), provider)
        : builder.Connections.ContainsKey(name) ? builder.CreateRegistered(name, provider)
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
