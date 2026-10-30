using System.Data.Common;
using System.Reflection;
using Microsoft.Extensions.FileProviders;

namespace UmbracoAppHost.Databases;

/// <summary>
/// The idempotent schema of an application database: one folder per dialect (<c>sqlite</c>, <c>sqlserver</c>,
/// <c>postgresql</c>, <c>oracle</c>) holding <c>*.sql</c> files that run in file-name order, and optional C# steps
/// that run after them, such as a seed. <see cref="ApplicationDatabase"/> ensures it. The files are read each time
/// the schema is ensured, so a script fixed after a failure is the one the next attempt runs.
/// </summary>
public sealed class ApplicationSchema
{
    private readonly Func<string, IEnumerable<(string Name, Func<byte[]> Read)>> files;
    private readonly List<Action<DbConnection, string>> steps = [];

    private ApplicationSchema(string id, Func<string, IEnumerable<(string Name, Func<byte[]> Read)>> files)
    {
        Id = id;
        this.files = files;
    }

    /// <summary>What identifies this schema for the once-per-process memo: the root path, or the assembly name and root.</summary>
    public string Id { get; }

    internal IReadOnlyList<Action<DbConnection, string>> Steps => steps;

    /// <summary>Scripts in <c>&lt;root&gt;/&lt;dialect&gt;/*.sql</c>; a missing dialect folder means no scripts.</summary>
    public static ApplicationSchema FromDirectory(string root)
    {
        var full = Path.GetFullPath(root);
        return new(full, dialect => Path.Combine(full, dialect) is var folder && Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder).Select(path => (Path.GetFileName(path), (Func<byte[]>)(() => File.ReadAllBytes(path))))
            : []);
    }

    /// <summary>
    /// The same layout embedded in <paramref name="assembly"/>, which must generate an embedded files manifest
    /// (<c>GenerateEmbeddedFilesManifest</c>) and embed the scripts, such as <c>&lt;EmbeddedResource Include="Schema\**\*.sql" /&gt;</c>.
    /// </summary>
    public static ApplicationSchema FromEmbedded(Assembly assembly, string root)
    {
        var provider = new ManifestEmbeddedFileProvider(assembly, root);
        return new($"{assembly.GetName().Name}:{root}", dialect => provider.GetDirectoryContents(dialect).Where(file => !file.IsDirectory)
            .Select(file => (file.Name, (Func<byte[]>)(() => {
                using var stream = file.CreateReadStream();
                using var bytes = new MemoryStream();
                stream.CopyTo(bytes);
                return bytes.ToArray();
            }))));
    }

    /// <summary>Adds a step that runs after the scripts with the open connection and its dialect folder name; returns this schema.</summary>
    public ApplicationSchema Then(Action<DbConnection, string> step)
    {
        steps.Add(step);
        return this;
    }

    /// <summary>A dialect's scripts in file-name order, with their bytes as stored.</summary>
    internal IReadOnlyList<(string Name, byte[] Content)> Scripts(string dialect)
        => files(dialect).Where(file => file.Name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)).OrderBy(file => file.Name, StringComparer.Ordinal)
            .Select(file => (file.Name, file.Read())).ToArray();
}
