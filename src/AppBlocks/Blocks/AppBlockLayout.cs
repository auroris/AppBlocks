using Umbraco.Cms.Core.Models.Blocks;
using Umbraco.Extensions;

namespace UmbracoAppHost.Blocks;

/// <summary>
/// The column arithmetic of the layout blocks. Umbraco's grid stylesheet places each area from the
/// span written on it; this decides that span. Two columns keeps the spans its data type configures
/// (6 + 6), so pages saved before the split setting existed render as they did, and its
/// <c>split</c> setting chooses 4 + 8 or 8 + 4 instead, which spares editors three nearly identical
/// block types.
/// </summary>
public static class AppBlockLayout
{
    public const string SplitAlias = "split";
    public const string CollapsibleAlias = "collapsible";

    /// <summary>The span of each area, in the order the data type defines the areas.</summary>
    public static IReadOnlyList<int> AreaSpans(BlockGridItem item)
    {
        var spans = item.Areas.Select(area => area.ColumnSpan).ToArray();
        if (item.Content.ContentType.Alias == "appColumns" && spans.Length == 2
            && Split(item.Settings?.Value<string>(SplitAlias), item.AreaGridColumns ?? 12) is { } split)
            return [split.Left, split.Right];
        return spans;
    }

    /// <summary>
    /// Reads a split such as <c>4/8</c>. A value that does not fill the block's columns exactly is
    /// ignored rather than trusted, so a hand-edited export can never push an area off the grid.
    /// </summary>
    public static (int Left, int Right)? Split(string? value, int columns = 12)
    {
        var parts = value?.Split('/', StringSplitOptions.TrimEntries);
        return parts is [var left, var right] && int.TryParse(left, out var l) && int.TryParse(right, out var r)
            && l > 0 && r > 0 && l + r == columns ? (l, r) : null;
    }

    /// <summary>
    /// The span of a block in a grid <paramref name="columns"/> wide. A field's width, when set, replaces the
    /// span the backoffice gives the block, and is capped at the grid, so a width chosen for a twelve-column
    /// fieldset still fits a field placed straight into a six-column area.
    /// </summary>
    public static int ItemSpan(BlockGridItem item, int columns) => ItemSpan(item.ColumnSpan, AppBlockField.Width(item.Content), columns);

    public static int ItemSpan(int columnSpan, int? width, int columns) => width is { } set ? Math.Clamp(set, 1, Math.Max(columns, 1)) : columnSpan;

    /// <summary>
    /// Whether a block takes a cell of its grid. CSS and JavaScript blocks render in the head and at the end of
    /// the body, and a hidden field is a hidden input, so a grid item for them would leave an empty cell and a gap.
    /// </summary>
    public static bool OccupiesCell(BlockGridItem item)
        => item.Content.ContentType.Alias is not (AppBlockSchema.CssAlias or AppBlockSchema.ScriptAlias) && !AppBlockField.IsHidden(item.Content);

    /// <summary>The modifier of the <c>ah-layout</c> hook, named for what an editor picked rather than the alias.</summary>
    public static string Modifier(string alias) => alias switch {
        "appFullWidth" => "full-width",
        "appColumns" => "two-columns",
        "appThreeColumns" => "three-columns",
        _ => alias,
    };
}
