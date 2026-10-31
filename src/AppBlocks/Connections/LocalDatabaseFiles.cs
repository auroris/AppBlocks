using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.Sqlite;

namespace AppBlocks.Connections;

/// <summary>Package-owned SQLite files, separate from the CMS but stored beside it when possible.</summary>
internal static class LocalDatabaseFiles
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly ConcurrentDictionary<string, object> Gates = new(PathComparer);
    private static readonly ConcurrentDictionary<string, bool> Initialized = new(PathComparer);

    internal static string FallbackPath(IConfiguration configuration, IWebHostEnvironment environment, string fileName)
        => Path.GetFullPath(Path.Combine(configuration["AppBlocks:DataRoot"]
            ?? environment.ContentRootPath, "App_Data", fileName));

    internal static string? UmbracoFile(IConfiguration configuration)
    {
        var value = configuration.GetConnectionString("umbracoDbDSN");
        if (string.IsNullOrWhiteSpace(value)
            || ConnectionCatalog.ConfiguredProvider(configuration, "umbracoDbDSN") != "sqlite") return null;
        var builder = new SqliteConnectionStringBuilder(ConnectionCatalog.Expand(value));
        var source = builder.DataSource;
        if (builder.Mode == SqliteOpenMode.Memory || string.IsNullOrWhiteSpace(source) || source == ":memory:") return null;
        if (source.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            if (source.Contains("mode=memory", StringComparison.OrdinalIgnoreCase)) return null;
            source = Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.IsFile
                ? uri.LocalPath : Uri.UnescapeDataString(source[5..].Split('?')[0]);
        }
        // Microsoft.Data.Sqlite resolves relative filenames against the process working directory.
        return Path.GetFullPath(source);
    }

    internal static string PathFor(IConfiguration configuration, IWebHostEnvironment environment, string fileName)
    {
        var cms = UmbracoFile(configuration);
        var path = cms is null ? FallbackPath(configuration, environment, fileName) : Path.Combine(Path.GetDirectoryName(cms)!, fileName);
        if (PathComparer.Equals(path, cms))
            throw new InvalidOperationException($"The default application database {fileName} would be Umbraco's database. Configure a separate named connection.");
        return path;
    }

    internal static string ConnectionString(string path)
        => new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, Pooling = true }.ToString();

    internal static SqliteConnection Create(string value)
    {
        var connection = new SqliteConnection(value);
        var path = new SqliteConnectionStringBuilder(value).DataSource;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        connection.StateChange += (_, change) =>
        {
            if (change.CurrentState != ConnectionState.Open || Initialized.ContainsKey(path)) return;
            lock (Gates.GetOrAdd(path, _ => new object()))
            {
                if (Initialized.ContainsKey(path)) return;
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA journal_mode=WAL";
                if (!string.Equals(Convert.ToString(command.ExecuteScalar()), "wal", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The application database could not enable write-ahead logging.");
                Initialized[path] = true;
            }
        };
        return connection;
    }
}
