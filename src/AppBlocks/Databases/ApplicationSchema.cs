using System.Data.Common;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileProviders;

namespace AppBlocks.Databases;

/// <summary>
/// The schema of an application database: one folder per dialect (<c>sqlite</c>, <c>sqlserver</c>, <c>postgresql</c>,
/// <c>oracle</c>) holding idempotent <c>*.sql</c> "ensure" scripts that run in file-name order at every process start; a
/// <c>migrations</c> folder inside each dialect folder whose <c>*.sql</c> scripts run once each, in file-name order, after
/// them; and optional C# steps that run after both, at every start (<see cref="Then"/>) or once (<see cref="Once"/>).
/// <see cref="ApplicationDatabase"/> ensures it. The files are read each time the schema is ensured, so a script fixed
/// after a failure is the one the next attempt runs.
/// </summary>
public sealed class ApplicationSchema
{
    /// <summary>The folder inside each dialect folder whose scripts run once each.</summary>
    public const string MigrationsFolder = "migrations";

    /// <summary>A C# step: <paramref name="Always"/> runs at every start, <paramref name="Once"/> once under <paramref name="Name"/>.</summary>
    internal sealed record Step(string? Name, Action<DbConnection, string>? Always, Action<DbConnection, DbTransaction, string>? Once);

    private readonly Func<string, IEnumerable<(string Name, Func<byte[]> Read)>> files;
    private readonly List<Step> steps = [];

    private ApplicationSchema(string id, string name, Func<string, IEnumerable<(string Name, Func<byte[]> Read)>> files)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 150 || name.Any(char.IsControl))
            throw new ArgumentException("A schema's name is 1 to 150 characters with no control characters.", nameof(name));
        Id = id;
        Name = name;
        this.files = files;
    }

    /// <summary>What identifies this schema for the once-per-process memo: the root path, or the assembly name and root.</summary>
    public string Id { get; }

    /// <summary>
    /// What the version table records this schema's scripts under, as the first part of each row: <c>&lt;name&gt;/&lt;file&gt;</c>,
    /// <c>&lt;name&gt;/migrations/&lt;file&gt;</c> and <c>&lt;name&gt;/once/&lt;step&gt;</c>. Two schemas that share one database keep
    /// apart by it, and it stays the same when the site moves to another folder, so their rows still match.
    /// </summary>
    public string Name { get; }

    /// <summary>The version table, <c>APP_SCHEMA</c> unless <see cref="RecordIn"/> names another.</summary>
    public string Table { get; private set; } = "APP_SCHEMA";

    internal IReadOnlyList<Step> Steps => steps;

    /// <summary>
    /// Scripts in <c>&lt;root&gt;/&lt;dialect&gt;/*.sql</c>, and run-once scripts in <c>&lt;root&gt;/&lt;dialect&gt;/migrations/*.sql</c>;
    /// a missing folder means no scripts.
    /// </summary>
    /// <param name="name">The <see cref="Name"/>; by default the root folder's own name, such as <c>Schema</c>.</param>
    public static ApplicationSchema FromDirectory(string root, string? name = null)
    {
        var full = Path.GetFullPath(root);
        return new(full, name ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(full)), dialect => Path.Combine(full, dialect) is var folder && Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder).Select(path => (Path.GetFileName(path), (Func<byte[]>)(() => File.ReadAllBytes(path))))
            : []);
    }

    /// <summary>
    /// The same layout embedded in <paramref name="assembly"/>, which must generate an embedded files manifest
    /// (<c>GenerateEmbeddedFilesManifest</c>) and embed the scripts, such as <c>&lt;EmbeddedResource Include="Schema\**\*.sql" /&gt;</c>.
    /// </summary>
    /// <param name="name">The <see cref="Name"/>; by default <c>&lt;assembly name&gt;:&lt;root&gt;</c>, as <see cref="Id"/> is.</param>
    public static ApplicationSchema FromEmbedded(Assembly assembly, string root, string? name = null)
    {
        var provider = new ManifestEmbeddedFileProvider(assembly, root);
        var id = $"{assembly.GetName().Name}:{root}";
        return new(id, name ?? id, dialect => provider.GetDirectoryContents(dialect).Where(file => !file.IsDirectory)
            .Select(file => (file.Name, (Func<byte[]>)(() => {
                using var stream = file.CreateReadStream();
                using var bytes = new MemoryStream();
                stream.CopyTo(bytes);
                return bytes.ToArray();
            }))));
    }

    /// <summary>
    /// Records this schema's scripts in <paramref name="table"/> rather than <c>APP_SCHEMA</c>, created there when missing; returns this
    /// schema. A table name, optionally qualified by its schema or owner, such as <c>orders.SCHEMA_VERSION</c>.
    /// </summary>
    public ApplicationSchema RecordIn(string table)
    {
        if (string.IsNullOrEmpty(table) || table.Length > 128 || !Regex.IsMatch(table, @"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)?$", RegexOptions.CultureInvariant))
            throw new ArgumentException("A version table is a table name of letters, digits and underscores, optionally after its schema's name and a dot.", nameof(table));
        Table = table;
        return this;
    }

    /// <summary>
    /// Adds a step that runs after the scripts, at every process start, with the open connection and its dialect folder name;
    /// returns this schema. Steps run in the order they are added.
    /// </summary>
    public ApplicationSchema Then(Action<DbConnection, string> step)
    {
        ArgumentNullException.ThrowIfNull(step);
        steps.Add(new(null, step, null));
        return this;
    }

    /// <summary>
    /// Adds a step that runs once per database, recorded in the version table under <paramref name="name"/>; returns this schema.
    /// It runs after the scripts, in the order steps are added, inside the transaction that records it: its commands set
    /// <c>command.Transaction</c> to the transaction it is given, and it must not commit, roll back or dispose it. A step that
    /// throws leaves no row, so the next start runs it again.
    /// </summary>
    /// <param name="name">What the step is recorded as, unique within this schema: letters, digits and <c>. _ -</c>, up to 200 characters.</param>
    public ApplicationSchema Once(string name, Action<DbConnection, DbTransaction, string> step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (string.IsNullOrEmpty(name) || name.Length > 200 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            throw new ArgumentException("A run-once step's name is 1 to 200 letters, digits, dots, underscores or hyphens.", nameof(name));
        if (steps.Any(existing => existing.Name == name)) throw new ArgumentException($"This schema already has a run-once step named {name}.", nameof(name));
        steps.Add(new(name, null, step));
        return this;
    }

    /// <summary>A dialect's scripts in file-name order, with their bytes as stored.</summary>
    internal IReadOnlyList<(string Name, byte[] Content)> Scripts(string dialect) => Read(dialect);

    /// <summary>A dialect's run-once scripts, from its <see cref="MigrationsFolder"/>, in file-name order.</summary>
    internal IReadOnlyList<(string Name, byte[] Content)> Migrations(string dialect) => Read(dialect + "/" + MigrationsFolder);

    private (string Name, byte[] Content)[] Read(string folder)
        => files(folder).Where(file => file.Name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)).OrderBy(file => file.Name, StringComparer.Ordinal)
            .Select(file => (file.Name, file.Read())).ToArray();
}
