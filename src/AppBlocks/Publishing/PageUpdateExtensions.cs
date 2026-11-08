using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Net.Http.Headers;

namespace AppBlocks.Publishing;

/// <summary>
/// Accumulates page freshness metadata during rendering and applies consistent HTTP cache validators after all participating
/// components have registered their update dates. Dates are compared in UTC; the page's date is reported as it was registered, since
/// a layout shows it as the page's "Date modified".
/// </summary>
public static class PageUpdateExtensions
{
    private const string PageUpdateDateKey = "PageUpdateDate";
    private const string DependencyUpdateDateKey = "PageDependencyUpdateDate";
    private const string PageHeadersKey = "PageHeaders";

    /// <summary>A registered page date: the UTC moment it is compared by, and the value <see cref="GetPageUpdateDate"/> reports.</summary>
    private sealed record PageDate(DateTime Utc, DateTime Reported);

    /// <summary>
    /// The moment the application started, folded into the cache validators so that a deployment moves them. A template, an asset
    /// bundle, a dictionary value or a view component can change what a page renders while its content stays the same, and a browser
    /// that revalidates every request (<c>public, max-age=0, must-revalidate</c>) would otherwise keep the old page after any number of
    /// deployments. Every restart therefore costs one revalidation per page. The visible "Date modified" and <c>dcterms.modified</c> are
    /// rendered from the page's own date and still report when its content changed.
    /// </summary>
    private static readonly DateTime DeployStamp = DateTime.UtcNow;

    /// <summary>
    /// Registers the update date of something the page renders without being its content, such as an ancestor whose code and settings
    /// it inherits. The latest date moves the cache validators only; the visible "Date modified" keeps reporting when the page's own
    /// content changed.
    /// </summary>
    public static void RegisterDependencyUpdateDate(this HttpContext context, DateTime date)
    {
        ArgumentNullException.ThrowIfNull(context);
        var utc = date.ToUniversalTime();
        if (context.Items[DependencyUpdateDateKey] is not DateTime current || utc > current) context.Items[DependencyUpdateDateKey] = utc;
    }

    /// <summary>Registers a potential page update date, reported as given. The latest date wins.</summary>
    public static void RegisterPageUpdateDate(this HttpContext context, DateTime date)
    {
        ArgumentNullException.ThrowIfNull(context);
        Register(context, new(date.ToUniversalTime(), date));
    }

    /// <summary>Registers a potential page update date, reported as its UTC time. The latest date wins.</summary>
    public static void RegisterPageUpdateDate(this HttpContext context, DateTimeOffset dateOffset)
    {
        ArgumentNullException.ThrowIfNull(context);
        Register(context, new(dateOffset.UtcDateTime, dateOffset.UtcDateTime));
    }

    /// <summary>The latest registered page update date, as it was registered, or <paramref name="fallbackDate"/> when none was registered.</summary>
    public static DateTime GetPageUpdateDate(this HttpContext context, DateTime fallbackDate)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items[PageUpdateDateKey] is PageDate registered ? registered.Reported : fallbackDate;
    }

    /// <summary>
    /// Marks a response that depends on who asked: <c>no-store</c> for browsers and proxies, and left out of the
    /// site's own ASP.NET Core output cache, which goes by its policies and does not read that header.
    /// </summary>
    public static void KeepOutOfCaches(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.Headers.CacheControl = "no-store";
        if (context.Features.Get<IOutputCacheFeature>() is { } outputCache) outputCache.Context.AllowCacheStorage = false;
    }

    /// <summary>Applies the host's page cache setting when a template renders an AppBlocks component.</summary>
    public static void KeepAppBlockPageOutOfCaches(this HttpContext context)
    {
        if (context.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<PageCacheOptions>>().Value.NoStorePages != NoStorePages.Off)
            context.KeepOutOfCaches();
    }

    /// <summary>
    /// Sets HTTP cache validators from the page update date and handles conditional requests.
    /// The shared view result filter discards any buffered Razor output for a 304 response.
    /// </summary>
    public static bool ApplyCacheHeaders(this HttpContext context, DateTime pageUpdateDate)
    {
        ArgumentNullException.ThrowIfNull(context);

        // A renderer can supply request-specific content. Content dates must not turn that response into
        // a shared representation or allow a 304 to reuse another sign-in state or set of field defaults.
        if (context.Response.GetTypedHeaders().CacheControl?.NoStore == true)
        {
            context.Response.Headers.Remove("ETag");
            context.Response.Headers.Remove("Last-Modified");
            return false;
        }

        // The validator describes the representation, so it moves with whatever the page inherits, such as an ancestor's CSS, and with
        // each deployment. Taking the latest of the three keeps ETag and Last-Modified consistent, so If-Modified-Since cannot answer 304
        // for a page a deployment changed.
        var updateDate = pageUpdateDate.ToUniversalTime();
        if (context.Items[DependencyUpdateDateKey] is DateTime dependency && dependency > updateDate) updateDate = dependency;
        if (DeployStamp > updateDate) updateDate = DeployStamp;

        // HTTP date headers have whole-second precision.
        updateDate = new DateTime(updateDate.Year, updateDate.Month, updateDate.Day, updateDate.Hour, updateDate.Minute, updateDate.Second, DateTimeKind.Utc);
        var etag = $"\"{updateDate.Ticks:x}\"";
        context.Response.Headers.ETag = etag;
        context.Response.Headers.LastModified = updateDate.ToString("R");
        context.Response.Headers.CacheControl = "public, max-age=0, must-revalidate";

        // If-None-Match compares whole tags, weakly; when it is sent, If-Modified-Since is ignored (RFC 9110 13.1.3).
        var request = context.Request.GetTypedHeaders();
        var current = new EntityTagHeaderValue(etag);
        var notModified = request.IfNoneMatch is { Count: > 0 } ifNoneMatch
            ? ifNoneMatch.Any(tag => tag.Equals(EntityTagHeaderValue.Any) || tag.Compare(current, useStrongComparison: false))
            : request.IfModifiedSince is { } modifiedSince && updateDate <= modifiedSince.UtcDateTime;
        if (!notModified) return false;
        context.Response.StatusCode = 304;
        return true;
    }

    /// <summary>Registers a page header, such as a meta tag, beside any others already registered, whatever collection holds them.</summary>
    public static void RegisterPageHeader(this HttpContext context, string header)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrEmpty(header)) return;
        List<string> headers = context.Items[PageHeadersKey] switch {
            IEnumerable<string> existing => [.. existing],
            string single => [single],
            _ => [],
        };
        headers.Add(header);
        context.Items[PageHeadersKey] = headers;
    }

    /// <summary>Keeps <paramref name="date"/> as the page's update date when its UTC moment is later than the one already registered.</summary>
    private static void Register(HttpContext context, PageDate date)
    {
        if (context.Items[PageUpdateDateKey] is not PageDate current || date.Utc > current.Utc) context.Items[PageUpdateDateKey] = date;
    }
}
