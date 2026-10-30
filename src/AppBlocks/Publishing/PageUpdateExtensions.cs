using System;
using Microsoft.AspNetCore.Http;

namespace UmbracoAppHost.Website
{
    /// <summary>
    /// Accumulates page freshness metadata during rendering and applies consistent HTTP cache
    /// validators after all participating components have registered their update dates.
    /// </summary>
    public static class PageUpdateExtensions
    {
        private const string PageUpdateDateKey = "PageUpdateDate";
        private const string DependencyUpdateDateKey = "PageDependencyUpdateDate";

        /// <summary>
        /// Registers the update date of something the page renders without being its content, such as an ancestor
        /// whose code and settings it inherits. The latest date moves the cache validators only; the visible
        /// "Date modified" keeps reporting when the page's own content changed.
        /// </summary>
        public static void RegisterDependencyUpdateDate(this HttpContext context, DateTime date)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var newUtc = date.ToUniversalTime();
            if (context.Items[DependencyUpdateDateKey] is not DateTime currentUtc || newUtc > currentUtc)
            {
                context.Items[DependencyUpdateDateKey] = newUtc;
            }
        }

        /// <summary>
        /// Registers a potential page update date. The latest date wins.
        /// </summary>
        public static void RegisterPageUpdateDate(this HttpContext context, DateTime date)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var currentUtc = GetCurrentUtc(context);
            var newUtc = date.ToUniversalTime();

            if (currentUtc == null || newUtc > currentUtc.Value)
            {
                context.Items[PageUpdateDateKey] = date;
            }
        }

        /// <summary>
        /// Registers a potential page update date. The latest date wins.
        /// </summary>
        public static void RegisterPageUpdateDate(this HttpContext context, DateTimeOffset dateOffset)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var currentUtc = GetCurrentUtc(context);
            var newUtc = dateOffset.UtcDateTime;

            if (currentUtc == null || newUtc > currentUtc.Value)
            {
                context.Items[PageUpdateDateKey] = dateOffset;
            }
        }

        /// <summary>
        /// Gets the resolved page update date, falling back to the provided date if none was registered.
        /// </summary>
        public static DateTime GetPageUpdateDate(this HttpContext context, DateTime fallbackDate)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            if (context.Items.TryGetValue(PageUpdateDateKey, out var val) && val != null)
            {
                if (val is DateTime dateTime)
                {
                    return dateTime;
                }
                if (val is DateTimeOffset dateTimeOffset)
                {
                    return dateTimeOffset.DateTime;
                }
            }
            return fallbackDate;
        }

        /// <summary>
        /// Set once when the application starts. Folded into the cache validator so that a
        /// deployment invalidates it.
        /// </summary>
        /// <remarks>
        /// Without this, the validator was derived purely from the content update date, so
        /// anything that changed the rendered output *without* an editor touching content was
        /// invisible to returning visitors: template edits, CSS/JS bundle changes, dictionary
        /// values, view component output. The response says
        /// "public, max-age=0, must-revalidate", so the client revalidates on every request and
        /// keeps its stored copy on a 304 -- meaning the staleness never expired on its own. A
        /// page last edited in June would keep serving June's HTML, complete with June's asset
        /// URLs, after any number of deployments.
        ///
        /// Using app start time is deliberately blunt: every restart invalidates every page
        /// validator, costing one revalidation per page per deploy. That is cheap, and it fails
        /// in the safe direction. A tighter option is Smidge's own cache-buster value, which
        /// tracks asset changes exactly, but it would not catch a Razor-only change.
        /// </remarks>
        private static readonly DateTime DeployStamp = DateTime.UtcNow;

        /// <summary>
        /// Sets HTTP cache validators from the page update date and handles conditional requests.
        /// The shared view result filter discards any buffered Razor output for a 304 response.
        /// </summary>
        public static bool ApplyCacheHeaders(this HttpContext context, DateTime pageUpdateDate)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            // A renderer can supply request-specific content. Content dates must not turn that response into
            // a shared representation or allow a 304 to reuse another sign-in state or set of field defaults.
            if (context.Response.GetTypedHeaders().CacheControl?.NoStore == true)
            {
                context.Response.Headers.Remove("ETag");
                context.Response.Headers.Remove("Last-Modified");
                return false;
            }

            var updateDate = pageUpdateDate.ToUniversalTime();

            // What the page inherits, such as an ancestor's CSS, is part of the representation too.
            if (context.Items[DependencyUpdateDateKey] is DateTime dependencyUtc && dependencyUtc > updateDate)
            {
                updateDate = dependencyUtc;
            }

            // The validator describes the *representation*, not the content, so it has to move
            // when the rendered output moves. Taking the later of the content date and the
            // deployment stamp keeps ETag and Last-Modified consistent -- otherwise the
            // If-Modified-Since branch below would still answer 304 and undo the fix.
            //
            // Note this only affects HTTP caching. The visible "Date modified" and the
            // dcterms.modified meta tag are rendered from pageUpdateDate directly and still
            // report when the content actually changed.
            if (DeployStamp > updateDate)
            {
                updateDate = DeployStamp;
            }

            // HTTP date headers have whole-second precision.
            updateDate = new DateTime(updateDate.Year, updateDate.Month, updateDate.Day, updateDate.Hour, updateDate.Minute, updateDate.Second, DateTimeKind.Utc);
            var etag = $"\"{updateDate.Ticks:x}\"";

            context.Response.Headers["ETag"] = etag;
            context.Response.Headers["Last-Modified"] = updateDate.ToString("R");
            context.Response.Headers["Cache-Control"] = "public, max-age=0, must-revalidate";

            var reqEtag = context.Request.Headers["If-None-Match"].ToString();
            var reqModSince = context.Request.Headers["If-Modified-Since"].ToString();

            bool notModified = false;

            if (!string.IsNullOrEmpty(reqEtag) && reqEtag.Contains(etag))
            {
                notModified = true;
            }
            else if (!string.IsNullOrEmpty(reqModSince) && DateTime.TryParse(reqModSince, out var modSinceDate))
            {
                if (updateDate <= modSinceDate.ToUniversalTime())
                {
                    notModified = true;
                }
            }

            if (notModified)
            {
                context.Response.StatusCode = 304;
                return true;
            }

            return false;
        }

        private const string PageHeadersKey = "PageHeaders";

        /// <summary>
        /// Registers a page header (such as a meta tag) safely, avoiding overwriting other headers regardless of collection type.
        /// </summary>
        public static void RegisterPageHeader(this HttpContext context, string header)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (string.IsNullOrEmpty(header)) return;

            var extraHeaders = new List<string>();
            if (context.Items.TryGetValue(PageHeadersKey, out var val) && val != null)
            {
                if (val is IEnumerable<string> existingHeaders)
                {
                    extraHeaders.AddRange(existingHeaders);
                }
                else if (val is string singleHeader)
                {
                    extraHeaders.Add(singleHeader);
                }
            }

            extraHeaders.Add(header);
            context.Items[PageHeadersKey] = extraHeaders;
        }

        private static DateTime? GetCurrentUtc(HttpContext context)
        {
            if (context.Items.TryGetValue(PageUpdateDateKey, out var val) && val != null)
            {
                if (val is DateTime dateTime)
                {
                    return dateTime.ToUniversalTime();
                }
                if (val is DateTimeOffset dateTimeOffset)
                {
                    return dateTimeOffset.UtcDateTime;
                }
            }
            return null;
        }
    }
}
