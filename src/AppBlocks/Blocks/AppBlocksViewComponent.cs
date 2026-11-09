using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Antiforgery;
using AppBlocks.Authentication;
using Umbraco.Cms.Core.Models.Blocks;
using Umbraco.Cms.Core.Models.PublishedContent;
using AppBlocks.Forms;
using AppBlocks.Publishing;

namespace AppBlocks.Blocks;

/// <param name="MemberId">The signed-in person's key; null for anyone not signed in, admitted or not.</param>
/// <param name="Variables">The signed-in person, the time and the query string, which field defaults resolve against. Null when page access refused the request; an admitted anonymous visitor has variables with no person in them.</param>
public sealed record AppBlocksPage(IPublishedContent Page, string? MemberId, AppBindVariables? Variables = null, BlockGridModel? Blocks = null);

/// <param name="HeadingLevel">The level a titled block inside this one uses, so region titles follow the page's outline.</param>
/// <param name="Variables">What field defaults resolve against; see <see cref="AppBlocksPage"/>.</param>
public sealed record AppBlockView(IPublishedContent Page, BlockGridItem Block, FormDefinition? Form = null, string? MemberId = null, int HeadingLevel = 2,
    AppBindVariables? Variables = null)
{
    /// <summary>The page's access admitted this request: someone signed in, or an anonymous visitor to an open page.</summary>
    public bool Admitted => MemberId is not null || Variables is not null;

    /// <summary>Admitted with no account, so the block's class or engine decides what they get.</summary>
    public bool Anonymous => MemberId is null && Variables is not null;
}

/// <summary>
/// Blocks placed on one grid: the page itself or one area of a block. <paramref name="Columns"/> is the
/// width of that grid in columns, which for an area is the span its data type configures, as in Umbraco's
/// block grid; the spans editors choose in the backoffice therefore mean the same on the page.
/// </summary>
public sealed record AppBlockItems(IPublishedContent Page, IEnumerable<BlockGridItem> Items, int Columns,
    FormDefinition? Form = null, string? MemberId = null, int HeadingLevel = 2, AppBindVariables? Variables = null);

[ViewComponent(Name = "AppBlocks")]
public sealed class AppBlocksViewComponent(AppPageAccess access, AppBindVariableResolver variables, IAntiforgery antiforgery, AppBlocksText text) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(IPublishedContent page)
    {
        if (await access.VisitorAsync(page.Key) is not { } visitor) {
            HttpContext.Response.StatusCode = 403;
            return Content(text["signInToUsePage", "Sign in with an Umbraco Member or backoffice User account permitted to use this page."]);
        }
        HttpContext.KeepAppBlockPageOutOfCaches();
        HttpContext.User = visitor;
        // All asynchronously loaded blocks must receive tokens for the same browser cookie.
        antiforgery.GetAndStoreTokens(HttpContext);
        // Field defaults are resolved here, once, from the person page access admitted, so every block sees the same values.
        return View("~/Views/Shared/Components/AppBlocks/Default.cshtml", new AppBlocksPage(page, AppBlockVisitor.Key(visitor),
            await variables.ResolveAsync(visitor, HttpContext.Request.Query, page.Key)));
    }
}

internal static class AppBlockVisitor
{
    /// <summary>The key blocks know a signed-in person by; null for an anonymous visitor, who has none.</summary>
    public static string? Key(System.Security.Claims.ClaimsPrincipal visitor) => visitor.Identity?.IsAuthenticated == true ? ApplicationAccessPolicy.UserKey(visitor) : null;
}

/// <summary>Ordinary page content is public unless Umbraco routing restricts the page through Public Access.</summary>
[ViewComponent(Name = "WebsiteBlocks")]
public sealed class WebsiteBlocksViewComponent(AppPageAccess access, AppBindVariableResolver variables, IEnumerable<IAppBlockPlugin> plugins,
    IAntiforgery antiforgery) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(IPublishedContent page)
    {
        if (!AppBlockContent.IsInteractive(page, plugins)) return View("~/Views/Shared/Components/AppBlocks/Default.cshtml", new AppBlocksPage(page, null));

        // Both the sign-in notice and the admitted person's defaults depend on this request, not just the content revision.
        HttpContext.KeepAppBlockPageOutOfCaches();
        var visitor = await access.VisitorAsync(page);
        if (visitor is null) return View("~/Views/Shared/Components/AppBlocks/Default.cshtml", new AppBlocksPage(page, null));
        HttpContext.User = visitor;
        antiforgery.GetAndStoreTokens(HttpContext);
        return View("~/Views/Shared/Components/AppBlocks/Default.cshtml", new AppBlocksPage(page, AppBlockVisitor.Key(visitor),
            await variables.ResolveAsync(visitor, HttpContext.Request.Query, page.Key)));
    }
}
