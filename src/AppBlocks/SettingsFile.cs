namespace AppBlocks;

/// <summary>
/// A settings file under App_Data, such as connections.json, parsed once per version of the file. The parsed value is kept with the
/// file's last write time and length, and the file is read again when either changes, so a hand-edited file is picked up without a
/// restart. <see cref="Invalidate"/> drops the value once the application has written the file itself.
/// </summary>
/// <param name="path">Where the file is, asked on every read, since configuration may move it.</param>
/// <param name="parse">Turns the file's text into the value; null text means there is no file.</param>
internal sealed class SettingsFile<T>(Func<string> path, Func<string?, T> parse)
{
    private sealed record Version(string Path, bool Exists, DateTime Written, long Length, T Value);
    private Version? current;

    public T Read()
    {
        var file = new FileInfo(path());
        var known = current;
        if (known is not null && known.Path == file.FullName && known.Exists == file.Exists
            && (!file.Exists || known.Written == file.LastWriteTimeUtc && known.Length == file.Length))
            return known.Value;
        // Stamped before the read: a write in between leaves a stamp older than the file, so the next read parses it again.
        var (exists, written, length) = file.Exists ? (true, file.LastWriteTimeUtc, file.Length) : (false, default, 0L);
        var value = parse(exists ? File.ReadAllText(file.FullName) : null);
        current = new(file.FullName, exists, written, length, value);
        return value;
    }

    public void Invalidate() => current = null;
}
