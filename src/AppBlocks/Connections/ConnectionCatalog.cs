using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AppBlocks.Databases;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace AppBlocks.Connections;

public sealed record ConnectionInfo(string Name, string Label, string Provider, bool IsDefault, bool ReadOnly);
public sealed record SaveConnection(string Name, string Provider, string? ConnectionString)
{
    /// <summary>Without the connection string: MVC writes an action's arguments to the log when tracing is on, and this is what it writes.</summary>
    public override string ToString() => $"{nameof(SaveConnection)} {{ {nameof(Name)} = {Name}, {nameof(Provider)} = {Provider} }}";
}
internal sealed record StoredConnection(string Name, string Provider, string ProtectedValue, string Revision);
internal sealed record ConnectionMaterial(string Provider, string Value, string Revision, bool LocalDefault = false);

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

    public IReadOnlyList<ConnectionInfo> List()
    {
        var result = new Dictionary<string, ConnectionInfo>(StringComparer.OrdinalIgnoreCase)
        {
            [ApplicationConnection] = new(ApplicationConnection, "AppBlocks database", Find(ApplicationConnection)!.Provider, true, true),
            [AttachmentsConnection] = new(AttachmentsConnection, "Separate uploads database", Find(AttachmentsConnection)!.Provider, false, true),
            [UmbracoConnection] = new(UmbracoConnection,
                "Umbraco database", ConfiguredProvider(configuration, "umbracoDbDSN"), false, true),
        };
        foreach (var item in configuration.GetSection("ConnectionStrings").GetChildren())
        {
            if (item.Key.Equals("umbracoDbDSN", StringComparison.OrdinalIgnoreCase) || item.Key.EndsWith("_ProviderName", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(item.Value)) continue;
            result.TryAdd(item.Key, new(item.Key, item.Key, ConfiguredProvider(configuration, item.Key), false, true));
        }
        foreach (var item in Read()) result.TryAdd(item.Name, new(item.Name, item.Name, item.Provider, false, false));
        // After the saved connections, because a saved connection is what a name shared with a registration opens.
        foreach (var name in registered?.ConnectionNames ?? [])
            if (!result.ContainsKey(name)) result[name] = new(name, name, RegisteredProvider(name), false, true);
        return result.Values.OrderByDescending(c => c.IsDefault).ThenBy(c => c.Label).ToArray();
    }

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
            return new("sqlite", value, Hash(value), LocalDefault: true);
        }
        var stored = Read().SingleOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return stored is null ? null : new(stored.Provider, Expand(protector.Unprotect(stored.ProtectedValue)), stored.Revision);
    }

    /// <summary><c>ConnectionStrings:&lt;name&gt;</c> and its provider, the only reading of those settings, which a builder with no catalog behind it shares.</summary>
    internal static ConnectionMaterial? Configured(IConfiguration configuration, string name)
        => configuration.GetConnectionString(name) is { Length: > 0 } value ? new(ConfiguredProvider(configuration, name), Expand(value), Hash(value)) : null;

    /// <summary><c>ConnectionStrings:&lt;name&gt;_ProviderName</c>. Without one it is SQL Server, as Umbraco reads a connection string with no provider.</summary>
    internal static string ConfiguredProvider(IConfiguration configuration, string name)
    {
        var value = configuration[$"ConnectionStrings:{name}_ProviderName"];
        if (string.IsNullOrWhiteSpace(value)) return "sqlServer";
        try { return Provider(value); }
        catch (ArgumentException error) { throw new ArgumentException($"ConnectionStrings:{name}_ProviderName is {value}. {error.Message}", error); }
    }

    internal string Revision(string name) => Find(name) is { } material ? Revision(material) : "";
    internal static string Revision(ConnectionMaterial material) => material.Provider + ":" + material.Revision;
    /// <summary>A connection for use, so opening an application database ensures its schema. <see cref="TestAsync"/> alone leaves that out.</summary>
    public DbConnection Openable(string name) => Find(name) is { } material ? ForUse(name, material)
        : registered?.Connections.ContainsKey(name) == true ? registered.CreateRegistered(name, services!)
        : throw new ArgumentException("Choose an available connection.", nameof(name));

    internal DbConnection ForUse(string name, ConnectionMaterial material)
        => registered is null ? Create(material) : registered.Create(name, () => Create(material), services!);

    /// <summary>
    /// A connection for looking only: it ensures no schema and prepares no file when it opens, as <see cref="TestAsync"/>'s does not.
    /// <see cref="Databases.DatabaseInspector"/> also keeps a SQLite file from being created by opening this.
    /// </summary>
    internal DbConnection Inspectable(string name) => Find(name) is { } material ? Create(material.Provider, material.Value)
        : Registered(name) ?? throw new ArgumentException("Choose an available connection.", nameof(name));

    internal bool IsApplicationDatabase(string name) => registered?.Schemas.ContainsKey(name) == true;

    /// <summary>What a registration's own factory makes, without the schema of an application database.</summary>
    private DbConnection? Registered(string name) => registered?.LookOnlyConnections.TryGetValue(name, out var look) == true ? look(services!)
        : registered?.Connections.TryGetValue(name, out var factory) == true ? factory(services!) : null;

    /// <summary>The declared dialect, or else what an unopened connection's type says, as <see cref="ApplicationConnections"/> decides a dialect.</summary>
    private string RegisteredProvider(string name)
    {
        if (registered!.Dialects.TryGetValue(name, out var declared)) return Provider(declared.ToString());
        if (detectedProviders.TryGetValue(name, out var known)) return known;
        try
        {
            using var connection = Registered(name)!;
            return detectedProviders[name] = Provider(ApplicationDatabase.Dialect(connection));
        }
        // One registration that cannot be built or recognized must not take the list with it; opening it says why.
        catch (Exception) { return UnknownProvider; }
    }

    public async Task SaveAsync(SaveConnection request, CancellationToken ct)
    {
        if (!Regex.IsMatch(request.Name, "^[A-Za-z][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)
            || request.Name.Equals("umbracoDbDSN", StringComparison.OrdinalIgnoreCase)
            || request.Name.Equals("ReportsDb", StringComparison.OrdinalIgnoreCase)
            || request.Name.Equals("AttachmentsDb", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use a unique name beginning with a letter, with up to 64 letters, numbers, hyphens or underscores.");
        if (List().Any(c => c.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase) && c.ReadOnly))
            throw new ArgumentException("This connection is managed by the site and cannot be replaced here.");
        var provider = Provider(request.Provider);
        await gate.WaitAsync(ct);
        try
        {
            var entries = Read().ToList();
            var old = entries.SingleOrDefault(c => c.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(request.ConnectionString) && old is null) throw new ArgumentException("Enter a connection string.");
            if (string.IsNullOrWhiteSpace(request.ConnectionString) && old!.Provider != provider
                && !(old.Provider is "oracle" or "oracle11g" && provider is "oracle" or "oracle11g"))
                throw new ArgumentException("Enter a connection string for the new database type.");
            var value = string.IsNullOrWhiteSpace(request.ConnectionString) ? protector.Unprotect(old!.ProtectedValue) : request.ConnectionString.Trim();
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
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
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
        await using var connection = material is not null ? Create(material.Provider, material.Value)
            : Registered(request.Name) ?? throw new ArgumentException("Choose an available connection.");
        // Testing a SQLite path must not create a new database as a side effect.
        if (connection is SqliteConnection sqlite)
        {
            var builder = new SqliteConnectionStringBuilder(sqlite.ConnectionString) { Mode = SqliteOpenMode.ReadOnly };
            sqlite.ConnectionString = builder.ConnectionString;
        }
        await connection.OpenAsync(timeout.Token);
    }

    private StoredConnection[] Read() => File.Exists(FilePath)
        ? JsonSerializer.Deserialize<StoredConnection[]>(File.ReadAllText(FilePath)) ?? [] : [];
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static string Expand(string value) => value.Replace("|DataDirectory|", AppDomain.CurrentDomain.GetData("DataDirectory") as string ?? AppContext.BaseDirectory,
        StringComparison.OrdinalIgnoreCase);

    public static string Provider(string value) => value.Trim().ToLowerInvariant() switch
    {
        "sqlite" or "microsoft.data.sqlite" => "sqlite",
        "sqlserver" or "microsoft.data.sqlclient" or "system.data.sqlclient" => "sqlServer",
        "postgres" or "postgresql" or "npgsql" => "postgres",
        "oracle" or "oracle.manageddataaccess.client" or "oracle.manageddataaccess.core" => "oracle",
        "oracle11g" => "oracle11g",
        _ => throw new ArgumentException("Choose SQLite, SQL Server, PostgreSQL, Oracle 11.2 or Oracle 12+."),
    };
    internal static DbConnection Create(ConnectionMaterial material) => material.LocalDefault
        ? LocalDatabaseFiles.Create(material.Value) : Create(material.Provider, material.Value);

    internal static DbConnection Create(string provider, string value) => provider switch
    {
        "sqlite" => new SqliteConnection(value), "sqlServer" => new SqlConnection(value),
        "postgres" => new NpgsqlConnection(value),
        "oracle" or "oracle11g" => OracleDriver.Create(value),
        _ => throw new ArgumentException("Unsupported database type."),
    };
}
