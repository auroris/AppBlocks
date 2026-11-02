using AppBlocks.Authentication;
using AppBlocks.Blocks;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Web.Common.Routing;
using Umbraco.Cms.Web.Website.Routing;

namespace AppBlocks.Publishing;

/// <summary>
/// Umbraco's Public Access routing, except that someone a host's <see cref="IProtectedPageAdmission"/> admits is shown the
/// page instead of the rule's login page. It is the same question the page's block endpoints ask, so the page and its
/// blocks agree. With no rule registered, every request gets Umbraco's own decision.
/// </summary>
public sealed class ProtectedPageAdmissionRequestHandler(IPublicAccessRequestHandler inner, IPublicAccessService publicAccess) : IPublicAccessRequestHandler
{
    public async Task<UmbracoRouteValues> RewriteForPublishedContentAccessAsync(HttpContext httpContext, UmbracoRouteValues routeValues)
    {
        if (routeValues.PublishedRequest.PublishedContent is { } page && publicAccess.IsProtected(page.Path).Success
            && await httpContext.RequestServices.GetRequiredService<AppPageAccess>().AdmittedAsync(httpContext, page) is not null)
            return routeValues;
        return await inner.RewriteForPublishedContentAccessAsync(httpContext, routeValues);
    }
}
