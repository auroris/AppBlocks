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
