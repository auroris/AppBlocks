using AppBlocks.Blocks;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;

namespace AppBlocks.Publishing;

/// <summary>Applies the host's page cache setting, including when Public Access substitutes a login or denied page.</summary>
public sealed class PageCacheHeaders(IOptions<PageCacheOptions> settings, IHttpContextAccessor http, IEnumerable<IAppBlockPlugin> plugins)
    : INotificationAsyncHandler<RoutingRequestNotification>
{
    private static readonly object NoStoreRequest = new();

    public Task HandleAsync(RoutingRequestNotification notification, CancellationToken cancellationToken)
    {
        var options = settings.Value;
        if (options.Is(PageCacheOptions.Off) || http.HttpContext is not { } context) return Task.CompletedTask;
        // Keep the original route's decision across Public Access's login/denied-page rewrite.
        if (notification.RequestBuilder.PublishedContent is { } page && (options.Is(PageCacheOptions.AllPages)
            || AppBlockContent.Flatten(AppBlockContent.Blocks(page, AppBlockContent.RequestCulture(context)), AppBlockContent.RequestCulture(context))
                .Any(block => AppBlockSchema.IsAppBlock(block.Content.ContentType.Alias)
                    || plugins.Any(plugin => plugin.Alias == block.Content.ContentType.Alias))))
            context.Items[NoStoreRequest] = true;
        if (context.Items[NoStoreRequest] is not true) return Task.CompletedTask;

        context.KeepOutOfCaches();
        context.Response.Headers.CacheControl = "no-cache, no-store";
        notification.RequestBuilder.SetNoCacheHeader(true);
        notification.RequestBuilder.SetCacheExtensions(["no-store"]);
        return Task.CompletedTask;
    }
}
