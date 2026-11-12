using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace AppBlocks.Connections;

public sealed record ConnectionInfo(string Name, string Label, string Provider, bool IsDefault, bool ReadOnly)
{
    /// <summary>The database type as a person reads it, such as <c>SQL Server</c> (<see cref="DatabaseKinds.DisplayName(string)"/>).</summary>
    public string DatabaseType => DatabaseKinds.DisplayName(Provider);

    /// <summary>Why the connection cannot be opened as it stands, such as a saved connection string this installation can no longer decrypt; null when nothing is known to be wrong.</summary>
    public string? Problem { get; init; }
}
public sealed record SaveConnection(string Name, string Provider, string? ConnectionString)
{
    /// <summary>Without the connection string: MVC writes an action's arguments to the log when tracing is on, and this is what it writes.</summary>
    public override string ToString() => $"{nameof(SaveConnection)} {{ {nameof(Name)} = {Name}, {nameof(Provider)} = {Provider} }}";
}
/// <summary>A connection as <see cref="ConnectionCatalog.ForCompilation"/> gives it: its provider name, <c>oracle11g</c> included, and its connection string.</summary>
public sealed record CompilableConnection(string Provider, string ConnectionString)
{
    /// <summary>Without the connection string, as <see cref="SaveConnection"/> leaves it out, so a log never carries it.</summary>
    public override string ToString() => $"{nameof(CompilableConnection)} {{ {nameof(Provider)} = {Provider} }}";
}
internal sealed record StoredConnection(string Name, string Provider, string ProtectedValue, string Revision);
/// <summary>What a name opens. <paramref name="Problem"/> says why it cannot be opened, and <see cref="ConnectionCatalog.Create(ConnectionMaterial)"/> throws it.</summary>
internal sealed record ConnectionMaterial(string Provider, string Value, string Revision, bool LocalDefault = false, string? Problem = null);

/// <summary>
/// Credentials belong to the installation, never to content or a schema export. A connection registered in code has
/// no material here: <see cref="Find"/> does not know it, and <see cref="List"/> and <see cref="Openable"/> go to its factory.
/// This is the one authority on what a name opens: <see cref="Find"/> first, and a registration's factory only when it finds nothing.
/// </summary>
public sealed class ConnectionCatalog(IConfiguration configuration, IWebHostEnvironment environment, IDataProtectionProvider protection,
    AppBlocksBuilder? registered = null, IServiceProvider? services = null)
{
    public const string UmbracoConnection = "UmbracoDb";
    public const string ApplicationConnection = "AppBlocksDb";
    public const string ApplicationDatabaseFile = "AppBlocks.sqlite.db";
    public const string AttachmentsConnection = "AttachmentsDb";
    public const string AttachmentsDatabaseFile = "Attachments.sqlite.db";
    /// <summary>The provider of a registered connection that neither declares a dialect nor can be recognized without opening it.</summary>
    public const string UnknownProvider = "unknown";
    private readonly ConcurrentDictionary<string, string> detectedProviders = new(StringComparer.OrdinalIgnoreCase);
    private readonly IDataProtector protector = protection.CreateProtector("AppBlocks.Connections.v1");
    private readonly SemaphoreSlim gate = new(1, 1);
    private string FilePath => Path.Combine(configuration["AppBlocks:DataRoot"]
        ?? environment.ContentRootPath, "App_Data", "AppBlocks", "connections.json");
    private SettingsFile<Saved[]>? savedFile;
    /// <summary>connections.json, parsed once per version of the file, so opening a connection or looking up a plan reads no disk.</summary>
    private SettingsFile<Saved[]> SavedFile => savedFile ??= new(() => FilePath, text => (text is null ? [] : JsonSerializer.Deserialize<StoredConnection[]>(text) ?? [])
        .Select(entry => new Saved(entry, new Lazy<string?>(() => Unprotect(entry.ProtectedValue), LazyThreadSafetyMode.PublicationOnly))).ToArray());

    /// <summary>A saved connection and its connection string, decrypted on first use and kept with the parsed file; null when this installation can no longer decrypt it.</summary>
    private sealed record Saved(StoredConnection Entry, Lazy<string?> Value);

    /// <summary>Null when the Data Protection keys the value was saved under are gone, as after a key ring change or a connections.json copied from another server.</summary>
    private string? Unprotect(string value)
    {
        try { return protector.Unprotect(value); }
        catch (CryptographicException) { return null; }
    }

    private static string Unreadable(string name) => $"The saved connection {name} cannot be read: it was saved under Data Protection keys this installation "
        + "no longer has. Save it again with its connection string.";

    public IReadOnlyList<ConnectionInfo> List()
    {
        var result = new Dictionary<string, ConnectionInfo>(StringComparer.OrdinalIgnoreCase)
        {
            [ApplicationConnection] = new(ApplicationConnection, "AppBlocks database", Listed(() => Find(ApplicationConnection)!.Provider), true, true),
            [AttachmentsConnection] = new(AttachmentsConnection, "Separate uploads database", Listed(() => Find(AttachmentsConnection)!.Provider), false, true),
            [UmbracoConnection] = new(UmbracoConnection,
                "Umbraco database", Listed(() => ConfiguredProvider(configuration, "umbracoDbDSN")), false, true),
        };
        foreach (var item in configuration.GetSection("ConnectionStrings").GetChildren())
        {
            if (item.Key.Equals("umbracoDbDSN", StringComparison.OrdinalIgnoreCase) || item.Key.EndsWith("_ProviderName", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(item.Value)) continue;
            if (Listable(item.Key, item.Value) is { } provider) result.TryAdd(item.Key, new(item.Key, item.Key, provider, false, true));
        }
        foreach (var item in SavedFile.Read())
            result.TryAdd(item.Entry.Name, new(item.Entry.Name, item.Entry.Name, item.Entry.Provider, false, false)
                { Problem = item.Value.Value is null ? Unreadable(item.Entry.Name) : null });
        // After the saved connections, because a saved connection is what a name shared with a registration opens.
        foreach (var name in registered?.ConnectionNames ?? [])
            if (!result.ContainsKey(name)) result[name] = new(name, name, RegisteredProvider(name), false, true);
        return result.Values.OrderByDescending(c => c.IsDefault).ThenBy(c => c.Label).ToArray();
    }

    /// <summary>A built-in entry's provider; a configured provider AppBlocks does not know lists it as <see cref="UnknownProvider"/>, and opening it says why.</summary>
    private static string Listed(Func<string> provider)
    {
        try { return provider(); }
        catch (ArgumentException) { return UnknownProvider; }
    }

    /// <summary>
    /// The provider of a <c>ConnectionStrings</c> entry AppBlocks can open, or null for an entry that belongs to something else on the
    /// site, such as a job store on MySQL or a cache: a <c>_ProviderName</c> AppBlocks does not know, or no provider and a string SQL Server
    /// cannot read. Asked to open such a name, <see cref="Find"/> still says what is wrong with it.
    /// </summary>
    private string? Listable(string name, string value)
    {
        if (!string.IsNullOrWhiteSpace(configuration[$"ConnectionStrings:{name}_ProviderName"]))
        {
            try { return ConfiguredProvider(configuration, name); }
            catch (ArgumentException) { return null; }
        }
        try { _ = new SqlConnectionStringBuilder(value); return DatabaseDialect.SqlServer.ProviderName(); }
        catch (Exception error) when (error is ArgumentException or FormatException or KeyNotFoundException) { return null; }
    }

    /// <summary>Whether the catalog supplies this name, including a configured connection it cannot currently open.</summary>
    public bool Contains(string name) => Find(name) is not null;

    /// <summary>
    /// What an external engine, such as Interactive Reports, compiles a name to, from one reading of the catalog. Null for a name the
    /// catalog does not supply, and for a package-owned local default, whose registered factory must create its directory and
    /// initialize SQLite, so the engine opens it through that factory instead. A saved connection this installation can no longer
    /// decrypt has an empty connection string, which fails when the engine opens it. Never return the connection string to a client.
    /// </summary>
    public CompilableConnection? ForCompilation(string name)
        => Find(name) is { LocalDefault: false } material ? new(material.Provider, material.Value) : null;

    internal ConnectionMaterial? Find(string name)
    {
        if (name.Equals(UmbracoConnection, StringComparison.OrdinalIgnoreCase))
        {
            var value = configuration.GetConnectionString("umbracoDbDSN");
            return value is null ? null : new(ConfiguredProvider(configuration, "umbracoDbDSN"), Expand(value), Hash(value));
        }
        if (Configured(configuration, name) is { } configured) return configured;
        if (name.Equals(ApplicationConnection, StringComparison.OrdinalIgnoreCase)
            || name.Equals(AttachmentsConnection, StringComparison.OrdinalIgnoreCase))
        {
            var uploads = name.Equals(AttachmentsConnection, StringComparison.OrdinalIgnoreCase);
            var value = LocalDatabaseFiles.ConnectionString(LocalDatabaseFiles.PathFor(configuration, environment,
                uploads ? AttachmentsDatabaseFile : ApplicationDatabaseFile));
            return new(DatabaseDialect.Sqlite.ProviderName(), value, Hash(value), LocalDefault: true);
        }
        var stored = SavedFile.Read().SingleOrDefault(c => c.Entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (stored is null) return null;
        return stored.Value.Value is { } saved ? new(stored.Entry.Provider, Expand(saved), stored.Entry.Revision)
            : new(stored.Entry.Provider, "", stored.Entry.Revision, Problem: Unreadable(stored.Entry.Name));
    }

    /// <summary><c>ConnectionStrings:&lt;name&gt;</c> and its provider, the only reading of those settings, which a builder with no catalog behind it shares.</summary>
    internal static ConnectionMaterial? Configured(IConfiguration configuration, string name)
        => configuration.GetConnectionString(name) is { Length: > 0 } value ? new(ConfiguredProvider(configuration, name), Expand(value), Hash(value)) : null;

    /// <summary><c>ConnectionStrings:&lt;name&gt;_ProviderName</c>. Without one it is SQL Server, as Umbraco reads a connection string with no provider.</summary>
    internal static string ConfiguredProvider(IConfiguration configuration, string name)
    {
        var value = configuration[$"ConnectionStrings:{name}_ProviderName"];
        if (string.IsNullOrWhiteSpace(value)) return DatabaseDialect.SqlServer.ProviderName();
        try { return Provider(value); }
        catch (ArgumentException error) { throw new ArgumentException($"ConnectionStrings:{name}_ProviderName is {value}. {error.Message}", error); }
    }

    /// <summary>An opaque revision for invalidating consumers of a managed connection.</summary>
    public string Revision(string name) => Find(name) is { } material ? Revision(material) : "";
    internal static string Revision(ConnectionMaterial material) => material.Provider + ":" + material.Revision;
    /// <summary>
    /// A connection for use, so opening an application database ensures its schema. <see cref="TestAsync"/> alone leaves that out.
    /// It takes <see cref="AppBlocksBuilder"/>'s one route from a name to a connection, and fails as that does.
    /// </summary>
    /// <exception cref="ConnectionNotRegisteredException">No connection by the name is configured, saved or registered.</exception>
    public DbConnection Openable(string name) => registered is not null ? registered.CreateConnection(name, this, services!)
        : Find(name) is { } material ? Create(material) : throw AppBlocksBuilder.NotRegistered(name);

    /// <summary>
    /// A connection for looking only: it ensures no schema and prepares no file when it opens, as <see cref="TestAsync"/>'s does not.
    /// <see cref="Databases.DatabaseInspector"/> also keeps a SQLite file from being created by opening this.
    /// </summary>
    public DbConnection Inspectable(string name) => Find(name) is { } material ? Create(material with { LocalDefault = false })
        : Registered(name) ?? throw new ArgumentException("Choose an available connection.", nameof(name));

    /// <summary>Whether opening this connection must initialize a registered application schema.</summary>
    public bool IsApplicationDatabase(string name) => registered?.Schemas.ContainsKey(name) == true;

    /// <summary>What a registration's own factory makes, without the schema of an application database.</summary>
    private DbConnection? Registered(string name) => registered?.LookOnlyConnections.TryGetValue(name, out var look) == true ? look(services!)
        : registered?.Connections.TryGetValue(name, out var factory) == true ? factory(services!) : null;

    /// <summary>The declared dialect, or else what an unopened connection's type says, as <see cref="ApplicationConnections"/> decides a dialect.</summary>
    private string RegisteredProvider(string name)
    {
        if (registered!.Dialects.TryGetValue(name, out var declared)) return declared.ProviderName();
        if (detectedProviders.TryGetValue(name, out var known)) return known;
        try
        {
            using var connection = Registered(name)!;
            return detectedProviders[name] = DatabaseKinds.Of(connection).ProviderName();
        }
        // One registration that cannot be built or recognized must not take the list with it; opening it says why.
        catch (Exception) { return UnknownProvider; }
    }

    public async Task SaveAsync(SaveConnection request, CancellationToken ct)
    {
        if (!Regex.IsMatch(request.Name, "^[A-Za-z][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)
            || request.Name.Equals("umbracoDbDSN", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use a unique name beginning with a letter, with up to 64 letters, numbers, hyphens or underscores.");
        // The listed connections the site manages, AttachmentsDb among them, and the names companions reserve for their own factories.
        // A name in ConnectionStrings is what Find opens, listed or not, so a connection saved under it would never be used.
        if (List().Any(c => c.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase) && c.ReadOnly)
            || configuration.GetConnectionString(request.Name) is { Length: > 0 }
            || registered?.ReservedConnectionNames.Contains(request.Name) == true)
            throw new ArgumentException("This connection is managed by the site and cannot be replaced here.");
        var provider = Provider(request.Provider);
        await gate.WaitAsync(ct);
        try
        {
            var entries = Read().ToList();
            var old = entries.SingleOrDefault(c => c.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(request.ConnectionString) && old is null) throw new ArgumentException("Enter a connection string.");
            // Oracle's two modes read the same connection string.
            if (string.IsNullOrWhiteSpace(request.ConnectionString) && old!.Provider != provider
                && !(IsOracle(old.Provider) && IsOracle(provider)))
                throw new ArgumentException("Enter a connection string for the new database type.");
            var value = string.IsNullOrWhiteSpace(request.ConnectionString)
                ? Unprotect(old!.ProtectedValue) ?? throw new ArgumentException(Unreadable(old.Name))
                : request.ConnectionString.Trim();
            using var check = Create(provider, Expand(value)); // Validates syntax without writing to the database.
            entries.RemoveAll(c => c.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase));
            entries.Add(new(request.Name, provider, protector.Protect(value), Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(entries), ct);
                File.Move(temporary, FilePath, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); SavedFile.Invalidate(); }
        }
        finally { gate.Release(); }
    }

    /// <summary>Says whether a database can be reached and changes nothing in it, so an application database's schema is not ensured here.</summary>
    public async Task TestAsync(SaveConnection request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var material = string.IsNullOrWhiteSpace(request.ConnectionString)
            ? Find(request.Name)
            : new ConnectionMaterial(Provider(request.Provider), Expand(request.ConnectionString), "");
        await using var connection = material is not null ? Create(material with { LocalDefault = false })
            : Registered(request.Name) ?? throw new ArgumentException("Choose an available connection.");
        // Testing a SQLite path must not create a new database as a side effect.
        if (connection is SqliteConnection sqlite)
        {
            var builder = new SqliteConnectionStringBuilder(sqlite.ConnectionString) { Mode = SqliteOpenMode.ReadOnly };
            sqlite.ConnectionString = builder.ConnectionString;
        }
        await connection.OpenAsync(timeout.Token);
    }

    private StoredConnection[] Read() => SavedFile.Read().Select(saved => saved.Entry).ToArray();
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static string Expand(string value) => value.Replace("|DataDirectory|", AppDomain.CurrentDomain.GetData("DataDirectory") as string ?? AppContext.BaseDirectory,
        StringComparison.OrdinalIgnoreCase);

    /// <summary>The provider name for any spelling a site may use (<see cref="DatabaseKinds.NormalizeProvider"/>).</summary>
    public static string Provider(string value) => DatabaseKinds.NormalizeProvider(value);
    private static bool IsOracle(string provider) => DatabaseKinds.TryFromProvider(provider, out var kind) && kind is DatabaseDialect.Oracle or DatabaseDialect.Oracle11g;
    /// <exception cref="ArgumentException">The material cannot be opened, for its <see cref="ConnectionMaterial.Problem"/> or a connection string the provider refuses.</exception>
    internal static DbConnection Create(ConnectionMaterial material) => material.Problem is { } problem ? throw new ArgumentException(problem)
        : material.LocalDefault ? LocalDatabaseFiles.Create(material.Value) : Create(material.Provider, material.Value);

    internal static DbConnection Create(string provider, string value) => !DatabaseKinds.TryFromProvider(provider, out var kind)
        ? throw new ArgumentException("Unsupported database type.")
        : kind switch {
            DatabaseDialect.Sqlite => new SqliteConnection(value),
            DatabaseDialect.SqlServer => new SqlConnection(value),
            DatabaseDialect.Postgres => DatabaseDrivers.PostgreSql.Create(value),
            _ => DatabaseDrivers.Oracle.Create(value),
        };
}
