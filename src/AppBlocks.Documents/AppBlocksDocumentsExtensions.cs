using AppBlocks.Setup;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Web.Common.ApplicationBuilder;
using UmbracoAppHost.PaperReports;

namespace AppBlocks.Documents;

[ComposeAfter(typeof(AppBlocksComposer))]
public sealed class AppBlocksDocumentsComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder) => builder.AddAppBlocksDocuments();
}

public static class AppBlocksDocumentsExtensions
{
    private sealed class Registration;

    /// <summary>Registers Documents automatically via its composer; explicit calls are also safe.</summary>
    public static IUmbracoBuilder AddAppBlocksDocuments(this IUmbracoBuilder builder)
    {
        if (builder.Services.Any(service => service.ServiceType == typeof(Registration))) return builder;
        builder.AddAppBlocks();
        builder.Services.AddSingleton<Registration>();
        builder.Services.AddDataProtection();
        builder.Services.AddSingleton<DocumentSettingsStore>();
        builder.Services.AddSingleton<DocumentOutputService>();
        builder.Services.AddSingleton<IDocumentOutputService>(services => services.GetRequiredService<DocumentOutputService>());
        // No redirects (which could forward credentials), and no automatic retries of physical printing.
        builder.Services.AddHttpClient(DocumentOutputService.ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        builder.Services.AddSingleton(new AppBlocks.Management.AppBlocksDashboardPage("documents", "Documents",
            "/App_Plugins/AppBlocks.Documents/settings.js", "appblocks-documents-settings"));
        builder.Services.TryAddScoped<IDocumentPresentationProvider, DefaultDocumentPresentationProvider>();
        builder.Services.TryAddSingleton<IAppDocumentLinks, DocumentLinks>();
        builder.Services.AddSingleton(new AppBlocksSchema(typeof(AppBlocksDocumentsExtensions).Assembly));
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(AppBlocksDocumentsExtensions).Assembly);
        PaperReportExtensions.RegisterPaperReports(builder);
        builder.Services.Configure<UmbracoPipelineOptions>(options => options.AddFilter(new UmbracoPipelineFilter("AppBlocks.Documents")
        {
            PostMapEndpoints = endpoints => endpoints.MapPaperReports(),
        }));
        return builder;
    }
}
