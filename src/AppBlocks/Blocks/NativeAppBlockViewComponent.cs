using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Models.Blocks;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Web;
using UmbracoAppHost.Authentication;

namespace UmbracoAppHost.Blocks;

/// <summary>Renders one native Block Grid/List item inside the site's own page and layout.</summary>
[ViewComponent(Name = "AppBlocksBlock")]
public sealed class NativeAppBlockViewComponent(AppPageAccess access, AppBindVariableResolver variables, IUmbracoContextAccessor contexts) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(BlockGridItem? block = null, BlockListItem? listItem = null, IPublishedContent? page = null)
    {
        if (page is null && contexts.TryGetUmbracoContext(out var context)) page = context.PublishedRequest?.PublishedContent;
        block ??= listItem is null ? null : AppBlockContent.AsGridItem(listItem);
        if (page is null || block is null) return Content("");
        HttpContext.Response.Headers.CacheControl = "no-store";
        var user = await access.UserAsync(page);
        if (user is not null) HttpContext.User = user;
        var model = new AppBlocksPage(page, user is null ? null : ApplicationAccessPolicy.UserKey(user),
            user is null ? null : await variables.ResolveAsync(user, HttpContext.Request.Query), new BlockGridModel([block], 12));
        return View("~/Views/Shared/Components/AppBlocks/Native.cshtml", model);
    }
}
