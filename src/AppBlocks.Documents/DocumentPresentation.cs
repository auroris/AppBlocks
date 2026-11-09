using Umbraco.Cms.Core.Models.PublishedContent;
using AppBlocks.DocumentOutput;

namespace AppBlocks.Documents;

/// <param name="PrintStylesheet">A site path of a stylesheet placed first in every document, or null for none.</param>
public sealed record DocumentPresentation(string Theme = "default",
    string? PrintStylesheet = DocumentPresentation.PackageStylesheet)
{
    /// <summary>The package's own print stylesheet, which documents get unless the site or the template chooses otherwise.</summary>
    public const string PackageStylesheet = "/App_Plugins/AppBlocks.Documents/print.css";

    /// <summary>
    /// The stylesheet every document starts with when the site keeps the default presentation: a site path, or empty for none.
    /// Without the setting it is <see cref="PackageStylesheet"/>.
    /// </summary>
    public const string PrintStylesheetSetting = "AppBlocks:Documents:PrintStylesheet";
}

/// <summary>A host can supply its branding without making Documents depend on its site shells.</summary>
public interface IDocumentPresentationProvider
{
    Task<DocumentPresentation> ResolveAsync(IPublishedContent page, CancellationToken cancellationToken);
}

internal sealed class DefaultDocumentPresentationProvider(IConfiguration configuration) : IDocumentPresentationProvider
{
    public Task<DocumentPresentation> ResolveAsync(IPublishedContent page, CancellationToken cancellationToken)
        => Task.FromResult(configuration[DocumentPresentation.PrintStylesheetSetting] is { } configured
            ? new DocumentPresentation(PrintStylesheet: string.IsNullOrWhiteSpace(configured) ? null : configured.Trim())
            : new DocumentPresentation());
}

/// <summary>Stable URL contract shared by forms and optional report companions.</summary>
public sealed class DocumentLinks : IAppDocumentLinks
{
    public const string Route = "/api/paper-reports/{key:guid}";
    public string Url(Guid documentKey, PathString pathBase) => pathBase + "/api/paper-reports/" + documentKey;
}
