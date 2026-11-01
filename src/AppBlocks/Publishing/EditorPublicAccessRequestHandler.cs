using AppBlocks.Authentication;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Web.Common.Routing;
using Umbraco.Cms.Web.Website.Routing;

namespace AppBlocks.Publishing;

/// <summary>
/// Umbraco's Public Access routing, except that a backoffice User who may edit the page is shown
/// it instead of the rule's login page. Public Access keeps Members out, and the page's own editor
/// sees its content in the backoffice already (<see cref="ApplicationAccessPolicy.CanEditAsync"/>).
/// Any other backoffice User, and every Member, gets Umbraco's own decision.
/// </summary>
public sealed class EditorPublicAccessRequestHandler(IPublicAccessRequestHandler inner, IPublicAccessService publicAccess) : IPublicAccessRequestHandler
{
    public async Task<UmbracoRouteValues> RewriteForPublishedContentAccessAsync(HttpContext httpContext, UmbracoRouteValues routeValues)
    {
        if (routeValues.PublishedRequest.PublishedContent is { } page && publicAccess.IsProtected(page.Path).Success
            && await httpContext.RequestServices.GetRequiredService<ApplicationAccessPolicy>().BackOfficeEditorAsync(httpContext, page) is not null)
            return routeValues;
        return await inner.RewriteForPublishedContentAccessAsync(httpContext, routeValues);
    }
}
