using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services.Navigation;
using UmbracoAppHost.Website;

namespace UmbracoAppHost.Blocks;

/// <summary>
/// What the blocks of one page contribute outside the place they sit: stylesheets for the head and scripts for
/// the end of the body. CSS and JavaScript blocks register here while the page renders, and
/// <see cref="AppPageAssetsTagHelperComponent"/> writes them into the layout afterwards. That order holds
/// because Razor renders a page's body before its layout, so the head is written after every block has run.
/// </summary>
/// <remarks>
/// Each stylesheet and script is written once, in the order its first block appears, however many blocks
/// carry it: a region copied with its CSS block, or two forms sharing a helper, do not repeat it. The code is
/// written as the editor wrote it, because back-office access is the trust boundary; the only change is that
/// a closing <c>&lt;/style</c> or <c>&lt;/script</c> inside it is escaped, so a string that happens to contain
/// one cannot end the element early.
/// </remarks>
public sealed partial class AppPageAssets
{
    /// <summary>
    /// The <see cref="HttpContext.Items"/> entry holding the request's Content Security Policy nonce. A site that
    /// sends a nonce-based policy stores the nonce here from its middleware, and every style and script the blocks
    /// emit carries it; without one they carry none.
    /// </summary>
    public const string CspNonceItem = "CspNonce";

    private readonly List<string> stylesheets = [];
    private readonly List<string> styles = [];
    private readonly List<string> scripts = [];
    private bool pageRegistered;

    public IReadOnlyList<string> Stylesheets => stylesheets;
    public IReadOnlyList<string> Styles => styles;
    public IReadOnlyList<string> Scripts => scripts;
    public bool IsEmpty => stylesheets.Count == 0 && styles.Count == 0 && scripts.Count == 0;

    /// <summary>Links a stylesheet in the head, ahead of the inline styles so a page's own CSS wins a tie with it.</summary>
    public void AddStylesheet(string href) => Add(stylesheets, href);

    /// <summary>Adds a CSS block's code to the head. Blank code adds nothing.</summary>
    public void AddStyle(string? css) => Add(styles, css);

    /// <summary>Adds a JavaScript block's code to the end of the body. Blank code adds nothing.</summary>
    public void AddScript(string? script) => Add(scripts, script);

    /// <summary>
    /// Ancestor code runs root to leaf, before block assets. The page's own date is its "Date modified"; ancestor
    /// edits only invalidate the page's cache.
    /// </summary>
    public void RegisterPage(IPublishedContent page, IDocumentNavigationQueryService navigation,
        IPublishedContentStatusFilteringService published, HttpContext context)
    {
        if (pageRegistered) return;
        context.RegisterPageUpdateDate(page.UpdateDate);
        RegisterAncestors(page.AncestorsOrSelf(navigation, published), context);
    }

    public void RegisterAncestors(IEnumerable<IPublishedContent> ancestorsAndSelf, HttpContext context)
    {
        if (pageRegistered) return;
        pageRegistered = true;
        var pageStyles = new List<string>();
        var pageScripts = new List<string>();
        foreach (var node in ancestorsAndSelf.OrderBy(node => node.Level))
        {
            context.RegisterDependencyUpdateDate(node.UpdateDate);
            Add(pageStyles, node.Value<string>("css"));
            Add(pageScripts, node.Value<string>("javascript"));
        }
        foreach (var style in styles) Add(pageStyles, style);
        foreach (var script in scripts) Add(pageScripts, script);
        styles.Clear(); styles.AddRange(pageStyles);
        scripts.Clear(); scripts.AddRange(pageScripts);
    }

    /// <summary>The stylesheet links, then the styles, for the end of the head.</summary>
    public IHtmlContent Head(string? nonce)
    {
        var html = new HtmlContentBuilder();
        foreach (var href in stylesheets)
        {
            var link = new TagBuilder("link") { TagRenderMode = TagRenderMode.SelfClosing };
            link.Attributes["rel"] = "stylesheet";
            link.Attributes["href"] = href;
            if (nonce is not null) link.Attributes["nonce"] = nonce;
            html.AppendLine(link);
        }
        foreach (var css in styles) html.AppendLine(Element("style", css, nonce));
        return html;
    }

    /// <summary>The scripts, for the end of the body.</summary>
    public IHtmlContent Body(string? nonce)
    {
        var html = new HtmlContentBuilder();
        foreach (var script in scripts) html.AppendLine(Element("script", script, nonce));
        return html;
    }

    /// <summary>The request's Content Security Policy nonce (<see cref="CspNonceItem"/>), or null when the site sets none.</summary>
    public static string? Nonce(HttpContext? context)
        => context?.Items[CspNonceItem] is string { Length: > 0 } nonce ? nonce : null;

    private static void Add(List<string> list, string? value)
    {
        // Compared without the whitespace around it, so a trailing newline does not make a copy look different.
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || list.Contains(text, StringComparer.Ordinal)) return;
        list.Add(text);
    }

    private static TagBuilder Element(string name, string code, string? nonce)
    {
        var element = new TagBuilder(name);
        if (nonce is not null) element.Attributes["nonce"] = nonce;
        var closing = name == "style" ? ClosingStyle() : ClosingScript();
        // "<\/" is the same text inside a CSS or JavaScript string, but no longer ends the element.
        element.InnerHtml.AppendHtml("\n" + closing.Replace(code, match => "<\\/" + match.Value[2..]) + "\n");
        return element;
    }

    [GeneratedRegex("</style", RegexOptions.IgnoreCase)]
    private static partial Regex ClosingStyle();

    [GeneratedRegex("</script", RegexOptions.IgnoreCase)]
    private static partial Regex ClosingScript();
}

/// <summary>
/// Writes <see cref="AppPageAssets"/> into whatever layout renders the page: the stylesheets at the end of its
/// head, the scripts at the end of its body. ASP.NET Core runs tag helper components on the head and body of
/// every Razor view, so both shells, the website's layouts, a host's own copy of a layout and any later theme
/// get the assets without a line in their templates. The assets are looked up from the request rather than
/// injected, so every other view, such as the backoffice's, pays only for the lookup.
/// </summary>
public sealed class AppPageAssetsTagHelperComponent(IHttpContextAccessor http) : TagHelperComponent
{
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        if (http.HttpContext is not { } request || request.RequestServices.GetService<AppPageAssets>() is not { IsEmpty: false } assets) return;
        var nonce = AppPageAssets.Nonce(request);
        if (string.Equals(context.TagName, "head", StringComparison.OrdinalIgnoreCase)) output.PostContent.AppendHtml(assets.Head(nonce));
        else if (string.Equals(context.TagName, "body", StringComparison.OrdinalIgnoreCase)) output.PostContent.AppendHtml(assets.Body(nonce));
    }
}
