namespace AppBlocks.Blocks;

/// <summary>
/// A source of the texts the blocks show, by id, for the current request's culture: a site's Umbraco dictionary, or anything
/// else a host registers. Return null for an id the source does not know, and AppBlocks shows its own English.
/// </summary>
public interface IAppBlocksTextSource
{
    string? Get(string id);
    /// <summary>Every id the source knows for the current culture, for the page's scripts; empty when it knows none.</summary>
    IReadOnlyDictionary<string, string> All();
}

/// <summary>
/// The texts the server-rendered blocks show, each with its English default beside it in the view:
/// <c>Text["saved", "Saved."]</c>. The first registered <see cref="IAppBlocksTextSource"/> that answers an id wins.
/// </summary>
public sealed class AppBlocksText(IEnumerable<IAppBlocksTextSource> sources)
{
    private readonly IAppBlocksTextSource[] sources = sources.ToArray();

    public string this[string id, string english]
    {
        get
        {
            foreach (var source in sources)
                if (source.Get(id) is { Length: > 0 } text) return text;
            return english;
        }
    }

    /// <summary>Every override the sources know, first source winning, for the runtime script to read.</summary>
    public IReadOnlyDictionary<string, string> Overrides()
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in sources)
            foreach (var (id, text) in source.All())
                if (!string.IsNullOrEmpty(text)) all.TryAdd(id, text);
        return all;
    }
}
