using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services.Navigation;
using AppBlocks.Publishing;

namespace AppBlocks.Blocks;

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

    /// <summary>The ancestors' property <see cref="RegisterAncestors"/> reads CSS from unless told another.</summary>
    public const string DefaultCssAlias = "css";

    /// <summary>The ancestors' property <see cref="RegisterAncestors"/> reads JavaScript from unless told another.</summary>
    public const string DefaultScriptAlias = "javascript";

    /// <summary>
    /// Ancestor code runs root to leaf, before block assets. The page's own date is its "Date modified"; ancestor
    /// edits only invalidate the page's cache.
    /// </summary>
    public void RegisterPage(IPublishedContent page, IDocumentNavigationQueryService navigation,
        IPublishedContentStatusFilteringService published, HttpContext context, string cssAlias = DefaultCssAlias, string scriptAlias = DefaultScriptAlias)
    {
        if (pageRegistered) return;
        context.RegisterPageUpdateDate(page.UpdateDate);
        RegisterAncestors(page.AncestorsOrSelf(navigation, published), context, cssAlias, scriptAlias);
    }

    /// <summary>
    /// The CSS and JavaScript each node carries in its <paramref name="cssAlias"/> and <paramref name="scriptAlias"/> properties, root to
    /// leaf, ahead of the blocks' own. The aliases are the site's document types' to choose; a node without them adds nothing.
    /// </summary>
    public void RegisterAncestors(IEnumerable<IPublishedContent> ancestorsAndSelf, HttpContext context, string cssAlias = DefaultCssAlias, string scriptAlias = DefaultScriptAlias)
    {
        if (pageRegistered) return;
        pageRegistered = true;
        var pageStyles = new List<string>();
        var pageScripts = new List<string>();
        foreach (var node in ancestorsAndSelf.OrderBy(node => node.Level))
        {
            context.RegisterDependencyUpdateDate(node.UpdateDate);
            Add(pageStyles, node.Value<string>(cssAlias));
            Add(pageScripts, node.Value<string>(scriptAlias));
        }
        foreach (var style in styles) Add(pageStyles, style);
        foreach (var script in scripts) Add(pageScripts, script);
        styles.Clear(); styles.AddRange(pageStyles);
        scripts.Clear(); scripts.AddRange(pageScripts);
    }

    /// <summary>The meta tag scripts learn the blocks' API root from.</summary>
    public const string ApiRootMetaName = "appblocks-api-root";

    /// <summary>The attribute of the JSON script element that carries the site's text overrides to the page's scripts.</summary>
    public const string StringsAttribute = "data-appblocks-strings";

    /// <summary>
    /// The stylesheet links, then the styles, for the end of the head. With <paramref name="apiRoot"/>, the path base and API root
    /// (<see cref="AppBlocksRoutes.Root"/>), an <c>appblocks-api-root</c> meta tag comes first, so the runtime asks the endpoints where the site maps them.
    /// </summary>
    public IHtmlContent Head(string? nonce, string? apiRoot = null)
    {
        var html = new HtmlContentBuilder();
        if (apiRoot is not null)
        {
            var meta = new TagBuilder("meta") { TagRenderMode = TagRenderMode.SelfClosing };
            meta.Attributes["name"] = ApiRootMetaName;
            meta.Attributes["content"] = apiRoot;
            html.AppendLine(meta);
        }
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

    /// <summary>
    /// The scripts, for the end of the body. Non-empty <paramref name="strings"/>, the site's text overrides by id
    /// (<see cref="AppBlocksText.Overrides"/>), come first as a JSON script element marked <c>data-appblocks-strings</c>, which the
    /// runtime reads in place of its own English.
    /// </summary>
    public IHtmlContent Body(string? nonce, IReadOnlyDictionary<string, string>? strings = null)
    {
        var html = new HtmlContentBuilder();
        if (strings is { Count: > 0 })
        {
            var data = new TagBuilder("script");
            data.Attributes["type"] = "application/json";
            data.Attributes[StringsAttribute] = "";
            // The default encoder writes < > & and quotes as \u escapes, so no text can end the element.
            data.InnerHtml.AppendHtml(JsonSerializer.Serialize(strings));
            html.AppendLine(data);
        }
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
        if (string.Equals(context.TagName, "head", StringComparison.OrdinalIgnoreCase))
            output.PostContent.AppendHtml(assets.Head(nonce, request.Request.PathBase + AppBlocksRoutes.Root(request.RequestServices.GetService<IConfiguration>())));
        else if (string.Equals(context.TagName, "body", StringComparison.OrdinalIgnoreCase))
            output.PostContent.AppendHtml(assets.Body(nonce, request.RequestServices.GetService<AppBlocksText>()?.Overrides()));
    }
}
