using System.Xml.Linq;
using Microsoft.Extensions.FileProviders;
using AppBlocks.Documents.PaperReports;
using AppBlocks.DocumentOutput;

namespace AppBlocks.Documents.Tests;

public sealed class PackageTests
{
    [Fact]
    public void Engine_is_independent_of_AppHost_and_InteractiveReports_and_schema_preserves_existing_content()
    {
        var assembly = typeof(AppBlocksDocumentsExtensions).Assembly;
        Assert.Equal("AppBlocks.Documents", typeof(PaperReportRenderer).Assembly.GetName().Name);
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), name => name.Name == "UmbracoAppHost"
            || name.Name!.StartsWith("InteractiveReport", StringComparison.Ordinal)
            || name.Name == "AppBlocks.InteractiveReports");
        using var stream = new ManifestEmbeddedFileProvider(assembly).GetFileInfo("Schema/ContentTypes/paperreport.config").CreateReadStream();
        var xml = XElement.Load(stream);
        Assert.Equal("paperReport", xml.Attribute("Alias")?.Value);
        Assert.Equal("d37dd9c4-23b9-4299-ae83-5bf6a943a842", xml.Attribute("Key")?.Value);
        Assert.Empty(xml.Element("Info")!.Element("AllowedTemplates")!.Elements());
        Assert.Equal("True", xml.Element("Info")!.Element("AllowAtRoot")!.Value);
    }

    [Fact]
    public void Document_links_preserve_the_virtual_path_and_legacy_endpoint()
    {
        IAppDocumentLinks links = new DocumentLinks();
        var key = Guid.NewGuid();
        Assert.Equal($"/wing/api/paper-reports/{key}", links.Url(key, "/wing"));
        Assert.Equal("AppBlocks", typeof(IAppDocumentLinks).Assembly.GetName().Name);
    }
}
