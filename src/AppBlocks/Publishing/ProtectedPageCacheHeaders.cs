using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;

namespace UmbracoAppHost.Website;

/// <summary>Public Access responses depend on the caller, including a login or denied page at the protected URL.</summary>
public sealed class ProtectedPageCacheHeaders(IPublicAccessService access, IHttpContextAccessor http)
    : INotificationAsyncHandler<RoutingRequestNotification>
{
    private static readonly object ProtectedRequest = new();

    public Task HandleAsync(RoutingRequestNotification notification, CancellationToken cancellationToken)
    {
        if (http.HttpContext is not { } context) return Task.CompletedTask;
        // Native routing raises this notification before checking Public Access, then again when it
        // substitutes a login/denied page. Keep the original route's protection across that rewrite.
        if (notification.RequestBuilder.PublishedContent is { } page && access.IsProtected(page.Path).Success)
            context.Items[ProtectedRequest] = true;
        if (context.Items[ProtectedRequest] is not true) return Task.CompletedTask;

        context.Response.Headers.CacheControl = "no-cache, no-store";
        notification.RequestBuilder.SetNoCacheHeader(true);
        notification.RequestBuilder.SetCacheExtensions(["no-store"]);
        return Task.CompletedTask;
    }
}
