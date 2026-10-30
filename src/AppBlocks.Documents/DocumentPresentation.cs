using Umbraco.Cms.Core.Models.PublishedContent;

namespace AppBlocks.Documents;

public sealed record DocumentPresentation(string Theme = "default",
    string? PrintStylesheet = "/App_Plugins/AppBlocks.Documents/print.css");

/// <summary>A host can supply its branding without making Documents depend on its site shells.</summary>
public interface IDocumentPresentationProvider
{
    Task<DocumentPresentation> ResolveAsync(IPublishedContent page, CancellationToken cancellationToken);
}

internal sealed class DefaultDocumentPresentationProvider : IDocumentPresentationProvider
{
    public Task<DocumentPresentation> ResolveAsync(IPublishedContent page, CancellationToken cancellationToken)
        => Task.FromResult(new DocumentPresentation());
}

/// <summary>Stable URL contract shared by forms and optional report companions.</summary>
public sealed class DocumentLinks : IAppDocumentLinks
{
    public const string Route = "/api/paper-reports/{key:guid}";
    public string Url(Guid documentKey, PathString pathBase) => pathBase + "/api/paper-reports/" + documentKey;
}
