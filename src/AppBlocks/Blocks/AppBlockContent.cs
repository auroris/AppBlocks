using Umbraco.Cms.Core.Models.Blocks;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace AppBlocks.Blocks;

public sealed class AppBlockContent(IUmbracoContextFactory contexts, IHttpContextAccessor http)
{
    public const string CultureParameter = "appCulture";

    public static string? RequestCulture(HttpContext? context) => context?.Request.Query[CultureParameter] is { Count: 1 } value && !string.IsNullOrWhiteSpace(value[0]) ? value[0] : null;

    public async Task<IPublishedContent?> PageAsync(Guid key)
    {
        using var reference = contexts.EnsureUmbracoContext();
        var page = reference.UmbracoContext.Content is { } cache ? await cache.GetByIdAsync(key, false) : null;
        return page is not null && AppBlockSchema.HasBlocks(page.ContentType) ? page : null;
    }

    public static BlockGridModel Blocks(IPublishedContent page, string? culture = null) =>
        culture is not null && page.Cultures.Count > 0 && !page.Cultures.ContainsKey(culture) ? BlockGridModel.Empty
            : new(NestedBlocks(page, culture).ToList(), 12);

    /// <summary>
    /// Whether the page has a block whose output depends on who asks: a form, attachments, a schedule, a tabular form, or a
    /// plugin block that requires a user. Such a page is rendered for this request and kept out of caches; a page of content
    /// blocks alone is cached as Umbraco and the host decide.
    /// </summary>
    public static bool IsInteractive(IPublishedContent page, IEnumerable<IAppBlockPlugin> plugins, string? culture = null)
        => AppBlockSchema.HasBlocks(page.ContentType) && Flatten(Blocks(page, culture), culture).Any(block =>
            block.Content.ContentType.Alias is "appForm" or AppBlockSchema.AttachmentsAlias or AppBlockSchema.ScheduleAlias or AppBlockSchema.TabularAlias
            || plugins.Any(plugin => plugin.RequiresUser && plugin.Alias == block.Content.ContentType.Alias));

    public static BlockGridItem AsGridItem(BlockListItem item) => new(item.ContentKey, item.Content, item.SettingsKey, item.Settings)
        { ColumnSpan = 12, RowSpan = 1, Areas = [] };

    public static IEnumerable<BlockGridItem> NestedBlocks(IPublishedElement element, string? culture = null)
    {
        foreach (var property in element.ContentType.PropertyTypes)
        {
            if (property.EditorAlias == "Umbraco.BlockGrid" && element.Value<BlockGridModel>(property.Alias, culture: culture) is { } grid)
                foreach (var item in grid) yield return item;
            else if (property.EditorAlias == "Umbraco.BlockList" && element.Value<BlockListModel>(property.Alias, culture: culture) is { } list)
                foreach (var item in list) yield return AsGridItem(item);
            else if (property.EditorAlias == "Umbraco.SingleBlock" && element.Value<BlockListItem>(property.Alias, culture: culture) is { } single)
                yield return AsGridItem(single);
        }
    }

    public static IEnumerable<BlockGridItem> Flatten(IEnumerable<BlockGridItem> items, string? culture = null)
    {
        foreach (var item in items) {
            yield return item;
            foreach (var child in Flatten(item.Areas.SelectMany(area => area).Concat(NestedBlocks(item.Content, culture)), culture)) yield return child;
        }
    }
    public static string ReportName(Guid page, Guid block) => $"page-{page:N}-{block:N}";
    public static bool ParseReportName(string name, out Guid page, out Guid block)
    {
        page = block = Guid.Empty;
        var parts = name.Split('-');
        return parts.Length == 3 && parts[0] == "page" && Guid.TryParseExact(parts[1], "N", out page) && Guid.TryParseExact(parts[2], "N", out block);
    }
    public async Task<(IPublishedContent Page, BlockGridItem Block, string? Culture)?> ReportAsync(string name)
    {
        if (!ParseReportName(name, out var pageKey, out var blockKey) || await PageAsync(pageKey) is not { } page) return null;
        var culture = RequestCulture(http.HttpContext);
        var block = Flatten(Blocks(page, culture), culture).FirstOrDefault(b => b.ContentKey == blockKey && b.Content.ContentType.Alias == "appReport");
        return block is null ? null : (page, block, culture);
    }
}
