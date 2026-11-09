using InteractiveReport.AspNetCore;
using Microsoft.Extensions.Options;
using IHostingEnvironment = Umbraco.Cms.Core.Hosting.IHostingEnvironment;

namespace AppBlocks.InteractiveReports.Reports;

/// <summary>
/// Keeps the URLs that report definitions emit inside <c>Umbraco:CMS:Hosting:ApplicationVirtualPath</c>.
/// </summary>
/// <remarks>
/// Umbraco's database stores media paths (<c>/media/...</c>) and the backoffice lives at
/// <c>/umbraco</c>, both without the virtual path, so a definition cannot know the prefix on its
/// own. Definitions are therefore written as if the site were at the root: the SQL writes
/// <see cref="SiteRootToken"/> where a URL it builds starts, and the edit pencil's template and
/// the create button's URL are root-absolute. At options build time the token becomes the
/// virtual path ("" at the root, "/intranet" under one) and root-absolute links get the same prefix.
/// Nothing changes when no virtual path is configured.
/// </remarks>
internal sealed class ReportLinksUnderVirtualPath(IHostingEnvironment hosting)
    : IPostConfigureOptions<InteractiveReportOptions>
{
    /// <summary>Written in report SQL where a URL needs the site's root; never reaches the database.</summary>
    public const string SiteRootToken = "{SiteRoot}";

    public void PostConfigure(string? name, InteractiveReportOptions options)
    {
        var virtualPath = hosting.ApplicationVirtualPath.Trim('/');
        var prefix = virtualPath.Length == 0 ? "" : "/" + virtualPath;

        foreach (var report in options.Reports.Values) Apply(report, prefix);
    }

    internal static void Apply(InteractiveReport.Core.Model.ReportDefinition report, string prefix)
    {
            // The token sits inside a SQL string literal; the prefix comes from configuration, not
            // from a user, but a quote in it must still not end the literal early.
            report.Sql = report.Sql.Replace(SiteRootToken, prefix.Replace("'", "''"), StringComparison.Ordinal);
            if (report.EditLink is { UrlTemplate: var template } edit && IsRootAbsolute(template))
                edit.UrlTemplate = prefix + template;
            if (report.CreateLink is { Url: { } url } create && IsRootAbsolute(url))
                create.Url = prefix + url;

        bool IsRootAbsolute(string url) =>
            prefix.Length > 0
            && url.StartsWith('/')
            && !url.StartsWith("//", StringComparison.Ordinal)
            && !url.StartsWith(prefix + "/", StringComparison.Ordinal);
    }
}
