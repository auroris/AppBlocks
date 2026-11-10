using System.Text.RegularExpressions;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Extensions;

namespace AppBlocks.Blocks;

/// <summary>
/// The page-level properties every field block has besides its binding: how it shows (hidden, width,
/// placeholder) and what a new record starts with (default value). They shape only the page; the form's resource
/// still decides what is valid and what is written, and read-only fields are never written whatever they show.
/// </summary>
public static class AppBlockField
{
    public const string HiddenAlias = "hidden";
    public const string DefaultValueAlias = "defaultValue";
    public const string WidthAlias = "width";
    public const string PlaceholderAlias = "placeholder";
    /// <summary>A Display value's own setting: the address its value links to, with the record's fields in braces.</summary>
    public const string LinkTemplateAlias = "linkTemplate";

    private static readonly Regex LinkPlaceholder = new(@"\{([^{}]*)\}", RegexOptions.CultureInvariant);
    private static readonly Regex LinkScheme = new(@"^([A-Za-z][A-Za-z0-9+.\-]*):", RegexOptions.CultureInvariant);

    /// <summary>The fields a link template names in braces, each once, in the order written.</summary>
    public static IReadOnlyList<string> LinkFields(string? template)
        => LinkPlaceholder.Matches(template ?? "").Select(match => match.Groups[1].Value.Trim()).Where(name => name.Length > 0)
            .Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Why publishing refuses a link template, or null when it is acceptable. The page escapes each field's value before it puts it
    /// in the address, so a value never chooses where a link leads; the template's own text does, and it is judged with its fields
    /// empty and filled. A template that is one field alone takes the whole address from that field, which the page checks against
    /// the same rule when it shows the record.
    /// </summary>
    public static string? LinkProblem(string? template)
    {
        var text = template?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (LinkPlaceholder.Match(text) is { Success: true } whole && whole.Length == text.Length) return null;
        var empty = LinkPlaceholder.Replace(text, "");
        return (empty.Length == 0 || IsSafeLink(empty)) && IsSafeLink(LinkPlaceholder.Replace(text, "x")) ? null
            : "A link is a path, or starts with http:, https:, mailto: or tel:.";
    }

    /// <summary>
    /// An address a page may link to: a path from the site's root or relative to the page, or an http, https, mailto or tel address.
    /// Anything with another scheme, such as javascript:, and an address that starts with two slashes, which leaves the site as a
    /// full address would, is refused. The runtime applies the same rule to the address it builds.
    /// </summary>
    public static bool IsSafeLink(string? link)
    {
        var text = link?.Trim();
        if (string.IsNullOrEmpty(text) || text.Any(char.IsControl)) return false;
        if (text.Length > 1 && text[0] is '/' or '\\' && text[1] is '/' or '\\') return false;
        return LinkScheme.Match(text) is not { Success: true } scheme || scheme.Groups[1].Value.ToLowerInvariant() is "http" or "https" or "mailto" or "tel";
    }

    /// <summary>
    /// A hidden field renders as a hidden input that is bound and submitted like any other, for keys and foreign
    /// keys. The person can still change what their browser submits, so a value that must be trusted belongs to the
    /// resource, not to a hidden field.
    /// </summary>
    public static bool IsHidden(IPublishedElement block)
        => AppBlockSchema.FieldAliases.Contains(block.ContentType.Alias) && block.Value<bool>(HiddenAlias);

    /// <summary>
    /// The columns of the twelve-column grid the field spans, or null to keep the span the backoffice gives the
    /// block. A value outside 1 to 12 is ignored rather than trusted, so a hand-edited export cannot push a field
    /// off the grid.
    /// </summary>
    public static int? Width(IPublishedElement block)
        => AppBlockSchema.FieldAliases.Contains(block.ContentType.Alias) && block.Value<int?>(WidthAlias) is >= 1 and <= 12 and var width
            ? width : null;
}
