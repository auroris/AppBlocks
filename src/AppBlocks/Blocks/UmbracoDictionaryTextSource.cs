using Umbraco.Cms.Core.Dictionary;

namespace AppBlocks.Blocks;

/// <summary>
/// The blocks' texts from the site's Umbraco dictionary: the children of the item whose key is <c>AppBlocks</c>, in the request's
/// culture. A child's key is the text's id, with a leading <c>AppBlocks.</c> left out, so <c>AppBlocks.saved</c> and <c>saved</c>
/// both answer <c>saved</c>. A site without the item, or a child with no translation for the culture, keeps AppBlocks' English.
/// </summary>
public sealed class UmbracoDictionaryTextSource(ICultureDictionaryFactory dictionaries) : IAppBlocksTextSource
{
    public const string ParentKey = "AppBlocks";
    private const string Prefix = ParentKey + ".";

    // Read once per request scope, when the first text is asked for, by which time Umbraco has set the request's culture.
    private IReadOnlyDictionary<string, string>? texts;
    private IReadOnlyDictionary<string, string> Texts => texts ??= Read();

    public string? Get(string id) => Texts.GetValueOrDefault(id);

    public IReadOnlyDictionary<string, string> All() => Texts;

    private IReadOnlyDictionary<string, string> Read()
    {
        var read = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in dictionaries.CreateDictionary().GetChildren(ParentKey))
        {
            var id = key.StartsWith(Prefix, StringComparison.Ordinal) ? key[Prefix.Length..] : key;
            if (id.Length > 0 && !string.IsNullOrEmpty(value)) read.TryAdd(id, value);
        }
        return read;
    }
}
