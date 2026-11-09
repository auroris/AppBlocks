namespace AppBlocks;

/// <summary>
/// Where the blocks' own endpoints answer: <c>AppBlocks:ApiRoot</c>, by default <c>/api</c>. Forms, attachments, tabular
/// forms and the antiforgery token endpoint are mapped below it, and <see cref="Authentication.AppBlocksAuthorization.IsApiRequest"/>
/// treats a request under it as one that expects an API answer. The page's scripts learn it from the
/// <c>appblocks-api-root</c> meta tag the page assets write. API endpoints keep their own <c>AppBlocks:ApiBasePath</c>.
/// </summary>
public static class AppBlocksRoutes
{
    public const string Setting = "AppBlocks:ApiRoot";
    public const string DefaultRoot = "/api";

    /// <summary>The configured root, normalized to one leading slash and no trailing slash; <see cref="DefaultRoot"/> when unset or blank.</summary>
    public static string Root(IConfiguration? configuration)
    {
        var value = configuration?[Setting]?.Trim().Trim('/');
        return string.IsNullOrEmpty(value) ? DefaultRoot : "/" + value;
    }

    /// <summary>The root followed by <paramref name="path"/>, which may start with a slash or not.</summary>
    public static string Under(IConfiguration? configuration, string path) => Root(configuration) + "/" + path.TrimStart('/');
}
