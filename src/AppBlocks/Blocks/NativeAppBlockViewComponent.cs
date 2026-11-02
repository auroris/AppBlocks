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
    IAntiforgery antiforgery) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(BlockGridItem? block = null, BlockListItem? listItem = null, IPublishedContent? page = null)
    {
        if (page is null && contexts.TryGetUmbracoContext(out var context)) page = context.PublishedRequest?.PublishedContent;
        block ??= listItem is null ? null : AppBlockContent.AsGridItem(listItem);
        if (page is null || block is null) return Content("");
        HttpContext.KeepOutOfCaches();
        var visitor = await access.VisitorAsync(page);
        if (visitor is not null) {
            HttpContext.User = visitor;
            // Establish one cookie in the page response before parallel block loads request their tokens.
            // Otherwise each first GET can issue a different cookie, invalidating the other blocks' tokens.
            antiforgery.GetAndStoreTokens(HttpContext);
        }
        var model = new AppBlocksPage(page, visitor is null ? null : AppBlockVisitor.Key(visitor),
            visitor is null ? null : await variables.ResolveAsync(visitor, HttpContext.Request.Query, page.Key), new BlockGridModel([block], 12));
        return View("~/Views/Shared/Components/AppBlocks/Native.cshtml", model);
    }
}
