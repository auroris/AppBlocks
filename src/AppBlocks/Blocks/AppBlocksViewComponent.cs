using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Antiforgery;
using AppBlocks.Authentication;
using Umbraco.Cms.Core.Models.Blocks;
using Umbraco.Cms.Core.Models.PublishedContent;
using AppBlocks.Forms;

namespace AppBlocks.Blocks;

/// <param name="Variables">The signed-in person, the time and the query string, which field defaults resolve against.</param>
public sealed record AppBlocksPage(IPublishedContent Page, string? MemberId, AppBindVariables? Variables = null, BlockGridModel? Blocks = null);

/// <param name="HeadingLevel">The level a titled block inside this one uses, so region titles follow the page's outline.</param>
/// <param name="Variables">What field defaults resolve against; see <see cref="AppBlocksPage"/>.</param>
public sealed record AppBlockView(IPublishedContent Page, BlockGridItem Block, FormDefinition? Form = null, string? MemberId = null, int HeadingLevel = 2,
    AppBindVariables? Variables = null);

/// <summary>
/// Blocks placed on one grid: the page itself or one area of a block. <paramref name="Columns"/> is the
/// width of that grid in columns, which for an area is the span its data type configures, as in Umbraco's
/// block grid; the spans editors choose in the backoffice therefore mean the same on the page.
/// </summary>
public sealed record AppBlockItems(IPublishedContent Page, IEnumerable<BlockGridItem> Items, int Columns,
    FormDefinition? Form = null, string? MemberId = null, int HeadingLevel = 2, AppBindVariables? Variables = null);

[ViewComponent(Name = "AppBlocks")]
public sealed class AppBlocksViewComponent(AppPageAccess access, AppBindVariableResolver variables, IAntiforgery antiforgery) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(IPublishedContent page)
    {
        if (await access.UserAsync(page.Key) is not { } member) {
            HttpContext.Response.StatusCode = 403;
            return Content("Sign in with a member account that has access to this page or a backoffice Administrator account.");
        }
        HttpContext.Response.Headers.CacheControl = "no-store";
        HttpContext.User = member;
        // All asynchronously loaded blocks must receive tokens for the same browser cookie.
        antiforgery.GetAndStoreTokens(HttpContext);
        // Field defaults are resolved here, once, from the person page access admitted, so every block sees the same values.
        return View("~/Views/Shared/Components/AppBlocks/Default.cshtml", new AppBlocksPage(page, ApplicationAccessPolicy.UserKey(member),
            await variables.ResolveAsync(member, HttpContext.Request.Query)));
    }
}

/// <summary>Ordinary page content is public unless Umbraco routing restricts the page through Public Access.</summary>
[ViewComponent(Name = "WebsiteBlocks")]
public sealed class WebsiteBlocksViewComponent(AppPageAccess access, AppBindVariableResolver variables, IEnumerable<IAppBlockPlugin> plugins,
    IAntiforgery antiforgery) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(IPublishedContent page)
    {
        var interactive = AppBlockContent.Flatten(AppBlockContent.Blocks(page)).Any(block => block.Content.ContentType.Alias is "appForm" or AppBlockSchema.AttachmentsAlias or AppBlockSchema.ScheduleAlias
            or AppBlockSchema.TabularAlias || plugins.Any(plugin => plugin.RequiresUser && plugin.Alias == block.Content.ContentType.Alias));
        if (!interactive) return View("~/Views/Shared/Components/AppBlocks/Default.cshtml", new AppBlocksPage(page, null));

        // Both the sign-in notice and the admitted person's defaults depend on this request, not just the content revision.
        HttpContext.Response.Headers.CacheControl = "no-store";
        var member = await access.UserAsync(page);
        if (member is null) return View("~/Views/Shared/Components/AppBlocks/Default.cshtml", new AppBlocksPage(page, null));
        HttpContext.User = member;
        antiforgery.GetAndStoreTokens(HttpContext);
        return View("~/Views/Shared/Components/AppBlocks/Default.cshtml", new AppBlocksPage(page, ApplicationAccessPolicy.UserKey(member),
            await variables.ResolveAsync(member, HttpContext.Request.Query)));
    }
}
