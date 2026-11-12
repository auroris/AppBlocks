using System.Data.Common;
using AppBlocks.Databases;

// The public namespace is retained for hosts; the builder configures the whole package.
namespace AppBlocks.Connections;

public sealed class AppBlocksBuilder
{
    internal Dictionary<string, Func<IServiceProvider, DbConnection>> Connections { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, DatabaseDialect> Dialects { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The application databases, apart from their factories: a configured or saved connection string never runs its factory.</summary>
    internal Dictionary<string, ApplicationSchema> Schemas { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>What a name's factory makes for something only looking, where that differs from <see cref="Connections"/>: nothing on disk is prepared for it.</summary>
    internal Dictionary<string, Func<IServiceProvider, DbConnection>> LookOnlyConnections { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal event Action<string>? ConnectionAdded;
    public IEnumerable<string> ConnectionNames => Connections.Keys;

    /// <summary>Registers current connections with another engine and keeps it informed of later registrations.</summary>
    public void RegisterConnections(Action<string, Func<IServiceProvider, DbConnection>, DatabaseDialect?> register)
    {
        void Register(string name) => register(name, provider => CreateRegistered(name, provider),
            Dialects.TryGetValue(name, out var dialect) ? dialect : null);
        foreach (var name in ConnectionNames) Register(name);
        ConnectionAdded += Register;
    }

    internal HashSet<string> ReservedConnectionNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal HashSet<string> KeySegmentContentTypes { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Keeps a connection name that a companion opens through its own factory, such as the reports store's <c>ReportsDb</c>, from being
    /// saved on the Connections screen, where a saved connection would take the name over.
    /// </summary>
    public AppBlocksBuilder ReserveConnectionName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ReservedConnectionNames.Add(name);
        return this;
    }

    /// <summary>
    /// Gives the nodes of a companion's definition document type, such as a report definition, a URL segment of their own key, as data
    /// source nodes have, so a node named like a page can never take that page's URL.
    /// </summary>
    public AppBlocksBuilder AddKeySegmentContentType(string alias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        KeySegmentContentTypes.Add(alias);
        return this;
    }

    internal List<Setup.BlockGridAllowance> BlockGridAllowances { get; } = [];

    /// <summary>
    /// Allows element type <paramref name="elementTypeAlias"/> in the site's Block Grid data type named <paramref name="blockGridDataTypeName"/>,
    /// once its definitions are installed, so a package's block can be placed on the site's pages without an editor configuring the grid by
    /// hand. Without <paramref name="areaKey"/> the block goes at the grid's root and in any area; with it, in that area only, named by key or
    /// alias, which also lists the block when the area allows only certain blocks. An element the data type already lists is left as the site
    /// configured it, and nothing is changed when either is missing. <see cref="Setup.BlockGridAllowances"/> applies it.
    /// </summary>
    public AppBlocksBuilder AllowInBlockGrid(string blockGridDataTypeName, string elementTypeAlias, int columnSpan = 12, int rowSpan = 1, string? areaKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blockGridDataTypeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(elementTypeAlias);
        ArgumentOutOfRangeException.ThrowIfLessThan(columnSpan, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rowSpan, 1);
        BlockGridAllowances.Add(new(blockGridDataTypeName, elementTypeAlias, columnSpan, rowSpan, string.IsNullOrWhiteSpace(areaKey) ? null : areaKey));
        return this;
    }

    public AppBlocksBuilder AddConnection(string name, Func<IServiceProvider, DbConnection> factory, DatabaseDialect? dialect = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);
        Connections[name] = factory;
        LookOnlyConnections.Remove(name);
        Dialects.Remove(name);
        Schemas.Remove(name);
        if (dialect is { } declared) Dialects[name] = declared;
        ConnectionAdded?.Invoke(name);
        return this;
    }

    /// <summary>A connection to <paramref name="name"/> for use, which ensures the schema when it opens if the name is an application database.</summary>
    internal DbConnection Create(string name, Func<DbConnection> create, IServiceProvider provider)
        => Schemas.TryGetValue(name, out var schema) ? ApplicationDatabase.EnsureOnOpen(create(), create, name, schema, provider) : create();

    /// <summary>
    /// What a registered name opens, for callers that come to the registration first, such as the report engine asking an unopened
    /// connection for its dialect: what the catalog holds under the name wins, as it does everywhere else, and the factory makes the rest.
    /// </summary>
    internal DbConnection CreateRegistered(string name, IServiceProvider provider) => CreateConnection(name, provider.GetService<ConnectionCatalog>(), provider);

    /// <summary>
    /// The one route from a name to a connection for use, which <see cref="ConnectionCatalog.Openable"/> and
    /// <see cref="IApplicationConnectionFactory.CreateConnection"/> both take: what <paramref name="catalog"/> holds under the name, else its factory.
    /// </summary>
    /// <exception cref="ConnectionNotRegisteredException">Neither knows the name.</exception>
    internal DbConnection CreateConnection(string name, ConnectionCatalog? catalog, IServiceProvider provider)
        => catalog?.Find(name) is { } material ? Create(name, () => ConnectionCatalog.Create(material), provider)
            : Connections.TryGetValue(name, out var factory) ? Create(name, () => factory(provider), provider)
            : throw NotRegistered(name);

    internal static ConnectionNotRegisteredException NotRegistered(string name) => new($"The connection {name} is not registered with AppBlocks. Choose an available connection.");
}

