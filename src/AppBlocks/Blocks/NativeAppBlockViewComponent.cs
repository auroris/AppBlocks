using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Antiforgery;
using Umbraco.Cms.Core.Models.Blocks;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Web;
using AppBlocks.Publishing;

namespace AppBlocks.Blocks;

/// <summary>Renders one native Block Grid/List item inside the site's own page and layout.</summary>
[ViewComponent(Name = "AppBlocksBlock")]
public sealed class NativeAppBlockViewComponent(AppPageAccess access, AppBindVariableResolver variables, IUmbracoContextAccessor contexts,
    IAntiforgery antiforgery, IEnumerable<IAppBlockPlugin> plugins) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(BlockGridItem? block = null, BlockListItem? listItem = null, IPublishedContent? page = null)
    {
        if (page is null && contexts.TryGetUmbracoContext(out var context)) page = context.PublishedRequest?.PublishedContent;
        block ??= listItem is null ? null : AppBlockContent.AsGridItem(listItem);
        if (page is null || block is null) return Content("");
        const string view = "~/Views/Shared/Components/AppBlocks/Native.cshtml";
        var grid = new BlockGridModel([block], 12);
        // As WebsiteBlocks decides for a whole page: a block of content alone renders the same for everyone, so it leaves caching to
        // Umbraco and the host, and issues no antiforgery token, which would mark the response uncacheable too.
        if (!AppBlockContent.IsInteractive([block], plugins)) return View(view, new AppBlocksPage(page, null, null, grid));
        HttpContext.KeepAppBlockPageOutOfCaches();
        var visitor = await access.VisitorAsync(page);
        if (visitor is not null) {
            HttpContext.User = visitor;
            // Establish one cookie in the page response before parallel block loads request their tokens.
            // Otherwise each first GET can issue a different cookie, invalidating the other blocks' tokens.
            antiforgery.GetAndStoreTokens(HttpContext);
        }
        var model = new AppBlocksPage(page, visitor is null ? null : AppBlockVisitor.Key(visitor),
            visitor is null ? null : await variables.ResolveAsync(visitor, HttpContext.Request.Query, page.Key), grid);
        return View(view, model);
    }
}
