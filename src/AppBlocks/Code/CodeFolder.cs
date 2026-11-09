using System.Buffers;
using System.Text;

namespace AppBlocks.Code;

/// <summary>A file or folder of the C# folder, by its path below the root with '/' between folders.</summary>
public sealed record CodeEntry(string Path, bool IsFolder, long Size, DateTimeOffset Modified);

/// <summary>A path the C# folder refuses, with the reason an editor sees.</summary>
public sealed class CodePathException(string message) : ArgumentException(message);

/// <summary>
/// The site's C# folder, <c>AppBlocks/Code</c> under the content root unless <c>AppBlocks:CodeDirectory</c> says otherwise. Every path it
/// takes is relative to the root and must stay below it: no <c>..</c>, no rooted or drive paths, no link anywhere below the root, and files
/// ending in <c>.cs</c>. A name is any the file system takes, in any language and with spaces inside it, such as <c>Réservations.cs</c>.
/// The root is created by the first write, never on startup.
/// </summary>
public sealed class CodeFolder
{
    public const string DefaultDirectory = "AppBlocks/Code";
    /// <summary>The largest file the folder writes or compiles.</summary>
    public const int MaxFileBytes = 512 * 1024;
    public const int MaxFiles = 500;
    public const int MaxDepth = 8;
    public const int MaxNameLength = 100;

    private static readonly string[] DeviceNames = ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    public CodeFolder(string root) => Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    /// <summary>The configured folder under <paramref name="contentRoot"/>; a rooted setting is used as it is.</summary>
    public static CodeFolder Under(string contentRoot, string? directory)
        => new(Path.Combine(contentRoot, string.IsNullOrWhiteSpace(directory) ? DefaultDirectory : directory));

    public string Root { get; }
    public bool Exists => Directory.Exists(Root);

    /// <summary>
    /// What no name may hold: control characters, what this system refuses in a file name, and what Windows refuses, so a folder
    /// copied between servers keeps every file.
    /// </summary>
    private static readonly SearchValues<char> Refused = SearchValues.Create(
        [.. Enumerable.Range(0, 32).Select(c => (char)c), '\x7f', .. Path.GetInvalidFileNameChars(), '\\', '/', ':', '*', '?', '"', '<', '>', '|']);

    /// <summary>The path in its one spelling, '/' between names; throws <see cref="CodePathException"/> for anything the folder refuses.</summary>
    public static string Normalize(string? path, bool file)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new CodePathException("Enter a path.");
        var text = path.Trim().Replace('\\', '/');
        if (text.StartsWith('/') || text.Contains(':') || System.IO.Path.IsPathRooted(text)) throw new CodePathException("Use a path inside the C# folder, such as Orders/OrderForm.cs.");
        var names = text.Split('/');
        if (names.Length > MaxDepth) throw new CodePathException($"Folders nest at most {MaxDepth} deep.");
        foreach (var name in names)
        {
            if (name is "" or "." or "..") throw new CodePathException("A path cannot have empty names, '.' or '..'.");
            if (name.Length > MaxNameLength) throw new CodePathException($"A name has at most {MaxNameLength} characters.");
            if (name.AsSpan().ContainsAny(Refused)) throw new CodePathException($"'{name}' is not a name: leave out control characters and \\ / : * ? \" < > |.");
            // Windows drops a trailing dot or space, which would make two spellings of one file; a leading dot hides the file from the list.
            if (name.EndsWith('.') || name.StartsWith('.') || name != name.Trim()) throw new CodePathException($"'{name}' is not a name: start and end it with a character other than a dot or a space.");
            if (DeviceNames.Contains(name.Split('.')[0], StringComparer.OrdinalIgnoreCase)) throw new CodePathException($"'{name}' is a reserved device name.");
        }
        if (file && !names[^1].EndsWith(".cs", StringComparison.Ordinal)) throw new CodePathException("A file name ends in .cs.");
        if (!file && names[^1].EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) throw new CodePathException("A folder name cannot end in .cs.");
        return string.Join('/', names);
    }

    /// <summary>The full path of a normalized path, checked to be below the root and to pass through no link.</summary>
    public string Resolve(string path, bool file)
    {
        var relative = Normalize(path, file);
        var full = Path.GetFullPath(Path.Combine(Root, relative));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new CodePathException("The path is outside the C# folder.");
        var current = Root;
        foreach (var name in relative.Split('/'))
        {
            current = Path.Combine(current, name);
            if (IsLink(current)) throw new CodePathException("The path goes through a link, which the C# folder does not follow.");
        }
        return full;
    }

    private static bool IsLink(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        return info.Exists && (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    public string Relative(string full) => Path.GetRelativePath(Root, full).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>Every folder and .cs file, folders before files and each in name order, without following links.</summary>
    public IReadOnlyList<CodeEntry> List()
    {
        var entries = new List<CodeEntry>();
        if (!Exists) return entries;
        void Walk(DirectoryInfo directory, int depth)
        {
            if (depth > MaxDepth) return;
            var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System, IgnoreInaccessible = true };
            foreach (var folder in directory.EnumerateDirectories("*", options).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                entries.Add(new(Relative(folder.FullName), true, 0, folder.LastWriteTimeUtc));
                Walk(folder, depth + 1);
            }
            foreach (var file in directory.EnumerateFiles("*.cs", options).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                if (file.Name.EndsWith(".cs", StringComparison.Ordinal)) entries.Add(new(Relative(file.FullName), false, file.Length, file.LastWriteTimeUtc));
        }
        Walk(new DirectoryInfo(Root), 1);
        return entries;
    }

    public IReadOnlyList<CodeEntry> Files() => List().Where(entry => !entry.IsFolder).ToArray();

    public async Task<string> ReadAsync(string path, CancellationToken ct)
    {
        var full = Resolve(path, file: true);
        if (!File.Exists(full)) throw new FileNotFoundException("The file is not there.", path);
        if (new FileInfo(full).Length > MaxFileBytes) throw new CodePathException($"The file is larger than {MaxFileBytes / 1024} KB.");
        return await File.ReadAllTextAsync(full, ct);
    }

    /// <summary>Writes a file, creating the root and its folders; <paramref name="create"/> refuses one that exists, otherwise it must exist.</summary>
    public async Task WriteAsync(string path, string content, bool create, CancellationToken ct)
    {
        var full = Resolve(path, file: true);
        if (Encoding.UTF8.GetByteCount(content) > MaxFileBytes) throw new CodePathException($"A file holds at most {MaxFileBytes / 1024} KB.");
        if (create && (File.Exists(full) || Directory.Exists(full))) throw new CodePathException("A file or folder with that name is already there.");
        if (!create && !File.Exists(full)) throw new FileNotFoundException("The file is not there.", path);
        if (create && Files().Count >= MaxFiles) throw new CodePathException($"The C# folder holds at most {MaxFiles} files.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content, new UTF8Encoding(false), ct);
    }

    public void CreateFolder(string path)
    {
        var full = Resolve(path, file: false);
        if (File.Exists(full) || Directory.Exists(full)) throw new CodePathException("A file or folder with that name is already there.");
        Directory.CreateDirectory(full);
    }

    /// <summary>Moves a file or folder to a new path; both must be the same kind.</summary>
    public void Rename(string from, string to)
    {
        var isFolder = !from.EndsWith(".cs", StringComparison.Ordinal);
        var source = Resolve(from, file: !isFolder);
        var target = Resolve(to, file: !isFolder);
        if (isFolder ? !Directory.Exists(source) : !File.Exists(source)) throw new FileNotFoundException("The file or folder is not there.", from);
        if (File.Exists(target) || Directory.Exists(target)) throw new CodePathException("A file or folder with that name is already there.");
        if (isFolder && (target + Path.DirectorySeparatorChar).StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new CodePathException("A folder cannot move into itself.");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (isFolder) Directory.Move(source, target); else File.Move(source, target);
    }

    /// <summary>Deletes a file, or a folder with everything in it.</summary>
    public void Delete(string path)
    {
        var isFolder = !path.EndsWith(".cs", StringComparison.Ordinal);
        var full = Resolve(path, file: !isFolder);
        if (isFolder ? !Directory.Exists(full) : !File.Exists(full)) throw new FileNotFoundException("The file or folder is not there.", path);
        if (isFolder) Directory.Delete(full, recursive: true); else File.Delete(full);
    }
}
