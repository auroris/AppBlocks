using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Extensions;

namespace AppBlocks.Attachments;

/// <summary>
/// The <c>Attachments</c> configuration section: the connection whose database keeps the files, and the limits
/// every Attachments block stays within. A block may narrow a limit on its page and never widen it.
/// </summary>
public sealed class AttachmentSettings
{
    public const string Section = "Attachments";

    /// <summary>Uploads share the application database unless the site selects a separate store.</summary>
    public const string DefaultConnection = AppBlocks.Connections.ConnectionCatalog.ApplicationConnection;
    public const string SeparateConnection = AppBlocks.Connections.ConnectionCatalog.AttachmentsConnection;
    /// <summary>The filename used when the optional separate upload store is selected.</summary>
    public const string DefaultDatabaseFile = AppBlocks.Connections.ConnectionCatalog.AttachmentsDatabaseFile;

    /// <summary>A registered connection (see the README's application databases). Its database receives the two attachment tables on first use.</summary>
    public string Connection { get; set; } = DefaultConnection;

    /// <summary>The largest file any block accepts, in bytes. 25 MB unless configured.</summary>
    public long MaxFileSize { get; set; } = 25L * 1024 * 1024;

    /// <summary>Extensions such as <c>.pdf</c> every block is limited to. Empty allows any; a block then chooses its own.</summary>
    public List<string> AllowedExtensions { get; set; } = [];

    /// <summary>How many days a removed file stays before an administrator's purge deletes its rows. The purge takes another value when asked.</summary>
    public int RetentionDays { get; set; } = 30;

    public string ConnectionName => string.IsNullOrWhiteSpace(Connection) ? DefaultConnection : Connection.Trim();
}

/// <summary>The size and type limits that apply to one block: the page's choice within the site's configured maxima.</summary>
public sealed record AttachmentLimits(long MaxFileSize, IReadOnlyList<string> Extensions)
{
    /// <summary>What the file input's <c>accept</c> attribute says, or null for any file.</summary>
    public string? Accept => Extensions.Count == 0 ? null : string.Join(",", Extensions);

    public bool Accepts(string fileName)
        => Extensions.Count == 0 || Extensions.Contains(Path.GetExtension(fileName).ToLowerInvariant(), StringComparer.Ordinal);

    /// <summary>
    /// A block's size in megabytes and its extensions, each capped by the configuration: a block cannot accept a
    /// larger file than the site allows, and when the site lists extensions a block can only list fewer.
    /// </summary>
    public static AttachmentLimits For(long? blockMegabytes, string? blockExtensions, AttachmentSettings settings)
    {
        var size = blockMegabytes is > 0 and var megabytes ? Math.Min(megabytes * 1024 * 1024, settings.MaxFileSize) : settings.MaxFileSize;
        var configured = Normalize(settings.AllowedExtensions);
        var chosen = Normalize(Parse(blockExtensions));
        var extensions = configured.Count == 0 ? chosen : chosen.Count == 0 ? configured : chosen.Where(configured.Contains).ToArray();
        return new(size, extensions);
    }

    /// <summary>Extensions as an editor writes them: separated by commas, spaces or lines, with or without their dot.</summary>
    public static IReadOnlyList<string> Parse(string? text)
        => text?.Split([',', ';', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    private static IReadOnlyList<string> Normalize(IEnumerable<string> extensions)
        => extensions.Select(e => (e.StartsWith('.') ? e : "." + e).ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
}

/// <summary>
/// An Attachments block as authored: whose files it shows and what it lets people do. Without an owner resource the
/// files belong to the record of the form the block is placed in; with one, to the record of that resource whose
/// key the owner key names.
/// </summary>
public sealed record AttachmentBlock(string? Heading, string? Help, string? OwnerResource, string? OwnerKey, IReadOnlyList<string> Purposes,
    AttachmentLimits Limits, bool ReadOnly)
{
    public const string HeadingAlias = "heading", HelpAlias = "helpText", OwnerResourceAlias = "ownerResource", OwnerKeyAlias = "ownerKey",
        PurposesAlias = "purposes", MaxSizeAlias = "maxSize", AllowedTypesAlias = "allowedTypes", ReadOnlyAlias = "readOnly";

    /// <summary>Whether the files belong to the enclosing form's record rather than to a record the block names.</summary>
    public bool OwnedByForm => string.IsNullOrWhiteSpace(OwnerResource);

    public static AttachmentBlock Read(IPublishedElement block, AttachmentSettings settings)
    {
        string? Text(string alias) => block.Value<string>(alias) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        return new(Text(HeadingAlias), Text(HelpAlias), Text(OwnerResourceAlias), Text(OwnerKeyAlias), ParsePurposes(Text(PurposesAlias)),
            AttachmentLimits.For(block.Value<int?>(MaxSizeAlias), Text(AllowedTypesAlias), settings), block.Value<bool>(ReadOnlyAlias));
    }

    /// <summary>The purposes a file can be filed under, one per line as the editor wrote them.</summary>
    public static IReadOnlyList<string> ParsePurposes(string? text)
        => text?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray() ?? [];
}
