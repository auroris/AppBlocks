using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;
using UmbracoAppHost.Reports;

namespace AppBlocks.Connections;

public sealed record ConnectionInfo(string Name, string Label, string Provider, bool IsDefault, bool ReadOnly);
public sealed record SaveConnection(string Name, string Provider, string? ConnectionString);
internal sealed record StoredConnection(string Name, string Provider, string ProtectedValue, string Revision);
internal sealed record ConnectionMaterial(string Provider, string Value, string Revision);

/// <summary>Credentials belong to the installation, never to content or a schema export.</summary>
public sealed class ConnectionCatalog(IConfiguration configuration, IWebHostEnvironment environment, IDataProtectionProvider protection)
{
    private readonly IDataProtector protector = protection.CreateProtector("AppBlocks.Connections.v1");
    private readonly SemaphoreSlim gate = new(1, 1);
    private string FilePath => Path.Combine(configuration["AppBlocks:DataRoot"] ?? configuration["UmbracoAppHost:DataRoot"]
        ?? environment.ContentRootPath, "App_Data", "AppBlocks", "connections.json");

    public IReadOnlyList<ConnectionInfo> List()
    {
        var result = new Dictionary<string, ConnectionInfo>(StringComparer.OrdinalIgnoreCase)
        {
            [InteractiveReportsSite.UmbracoConnection] = new(InteractiveReportsSite.UmbracoConnection,
                "Umbraco database", Provider(configuration["ConnectionStrings:umbracoDbDSN_ProviderName"] ?? "sqlServer"), true, true),
        };
        foreach (var item in configuration.GetSection("ConnectionStrings").GetChildren())
        {
            if (item.Key.Equals("umbracoDbDSN", StringComparison.OrdinalIgnoreCase) || item.Key.EndsWith("_ProviderName", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(item.Value)) continue;
            result.TryAdd(item.Key, new(item.Key, item.Key, Provider(configuration[$"ConnectionStrings:{item.Key}_ProviderName"] ?? "sqlServer"), false, true));
        }
        foreach (var item in Read()) result.TryAdd(item.Name, new(item.Name, item.Name, item.Provider, false, false));
        return result.Values.OrderByDescending(c => c.IsDefault).ThenBy(c => c.Label).ToArray();
    }

    internal ConnectionMaterial? Find(string name)
    {
        if (name.Equals(InteractiveReportsSite.UmbracoConnection, StringComparison.OrdinalIgnoreCase))
        {
            var value = configuration.GetConnectionString("umbracoDbDSN");
            return value is null ? null : new(Provider(configuration["ConnectionStrings:umbracoDbDSN_ProviderName"] ?? "sqlServer"), Expand(value), Hash(value));
        }
        if (configuration.GetConnectionString(name) is { Length: > 0 } configured)
            return new(Provider(configuration[$"ConnectionStrings:{name}_ProviderName"] ?? "sqlServer"), Expand(configured), Hash(configured));
        var stored = Read().SingleOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return stored is null ? null : new(stored.Provider, Expand(protector.Unprotect(stored.ProtectedValue)), stored.Revision);
    }

    internal string Revision(string name) => Find(name)?.Revision ?? "";
    public DbConnection Openable(string name) => Find(name) is { } material ? Create(material.Provider, material.Value)
        : throw new ArgumentException("Choose an available connection.", nameof(name));

    public async Task SaveAsync(SaveConnection request, CancellationToken ct)
    {
        if (!Regex.IsMatch(request.Name, "^[A-Za-z][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)
            || request.Name.Equals("umbracoDbDSN", StringComparison.OrdinalIgnoreCase)
            || request.Name.Equals(InteractiveReportsSite.ReportsConnection, StringComparison.OrdinalIgnoreCase)
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
            if (string.IsNullOrWhiteSpace(request.ConnectionString) && old!.Provider != provider) throw new ArgumentException("Enter a connection string for the new database type.");
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

    public async Task TestAsync(SaveConnection request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await using var connection = string.IsNullOrWhiteSpace(request.ConnectionString)
            ? Openable(request.Name) : Create(Provider(request.Provider), Expand(request.ConnectionString));
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
    private static string Expand(string value) => value.Replace("|DataDirectory|", AppDomain.CurrentDomain.GetData("DataDirectory") as string ?? AppContext.BaseDirectory,
        StringComparison.OrdinalIgnoreCase);

    public static string Provider(string value) => value.ToLowerInvariant() switch
    {
        "sqlite" or "microsoft.data.sqlite" => "sqlite",
        "sqlserver" or "microsoft.data.sqlclient" or "system.data.sqlclient" => "sqlServer",
        "postgres" or "postgresql" or "npgsql" => "postgres",
        "oracle" or "oracle.manageddataaccess.client" or "oracle.manageddataaccess.core" => "oracle",
        _ => throw new ArgumentException("Choose SQLite, SQL Server, PostgreSQL or Oracle."),
    };
    internal static DbConnection Create(string provider, string value) => provider switch
    {
        "sqlite" => new SqliteConnection(value), "sqlServer" => new SqlConnection(value),
        "postgres" => new NpgsqlConnection(value),
        "oracle" => (DbConnection)Activator.CreateInstance(Type.GetType("Oracle.ManagedDataAccess.Client.OracleConnection, Oracle.ManagedDataAccess")
            ?? throw new ArgumentException("This installation does not include an Oracle provider integration."), value)!,
        _ => throw new ArgumentException("Unsupported database type."),
    };
}
