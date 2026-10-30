using InteractiveReport.AspNetCore;
using InteractiveReport.Client.FileDownload;
using InteractiveReport.Client.Json;
using AppBlocks.Connections;
using InteractiveReport.Core.Execution;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Web.Common.ApplicationBuilder;
using UmbracoAppHost.Authentication;
using UmbracoAppHost.Attachments;
using UmbracoAppHost.Blocks;
using UmbracoAppHost.DataSources;
using UmbracoAppHost.Forms;
using UmbracoAppHost.Reports;
using UmbracoAppHost.Schedules;
using UmbracoAppHost.Tabular;

namespace AppBlocks;

public sealed class AppBlocksComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder) => builder.AddAppBlocks();
}

public static class AppBlocksExtensions
{
    private sealed record Registration(InteractiveReportBuilder Reports);

    /// <summary>Automatically called by the composer. Hosts may call it again to register business databases.</summary>
    public static IUmbracoBuilder AddAppBlocks(this IUmbracoBuilder builder, Action<InteractiveReportBuilder>? configure = null)
    {
        var services = builder.Services;
        if (services.FirstOrDefault(d => d.ServiceType == typeof(Registration))?.ImplementationInstance is Registration existing)
        {
            configure?.Invoke(existing.Reports);
            return builder;
        }
        services.AddOptions<AppBlocksOptions>().Bind(builder.Config.GetSection(AppBlocksOptions.Section));
        services.AddDataProtection();
        services.AddSingleton<ConnectionCatalog>();
        services.AddScoped<ConnectionTables>();
        services.AddScoped<Samples.FormInventory>();
        builder.AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, Setup.SchemaInstaller>();
        services.TryAddScoped<ApplicationAccessPolicy>();
        services.TryAddScoped<IReportAccessPolicy, DefaultReportAccessPolicy>();
        services.AddControllersWithViews().AddApplicationPart(typeof(AppBlocksExtensions).Assembly);
        builder.AddInteractiveReportsSite(builder.Config, reports =>
        {
            reports.AddAttachmentsDatabase(builder.Config);
            configure?.Invoke(reports);
            services.AddSingleton(new Registration(reports));
        });
        services.PostConfigure<InteractiveReportOptions>(options =>
        {
            if (string.IsNullOrWhiteSpace(options.SavedReports.Connection) && string.IsNullOrWhiteSpace(options.SavedReports.DataSource))
                options.SavedReports.Connection = InteractiveReportsSite.UmbracoConnection;
        });
        Decorate<IReportConnectionFactory>(services, (sp, inner) => new ManagedConnectionFactory(inner, sp.GetRequiredService<ConnectionCatalog>()));
        var configuredStore = services.Last(d => d.ServiceType == typeof(InteractiveReport.Core.Definitions.IReportDefinitionStore));
        services.AddSingleton(sp => new ConfiguredReportStore((InteractiveReport.Core.Definitions.IReportDefinitionStore)Create(sp, configuredStore)));
        services.Replace(ServiceDescriptor.Scoped<InteractiveReport.Core.Definitions.IReportDefinitionStore, AppReportDefinitionStore>());
        services.AddScoped<AppBlockContent>();
        services.AddScoped<AppPageAccess>();
        Decorate<IPublicAccessChecker>(services, (sp, inner) => new AdministratorPublicAccessChecker(inner, sp.GetRequiredService<IHttpContextAccessor>()));
        services.AddScoped<FormResourceRegistry>();
        builder.AddDataSources();
        builder.AddAttachments(builder.Config);
        builder.AddSchedules();
        builder.AddTabular();
        services.AddScoped<DataSourcePreview>();
        services.AddSingleton<IAuthorizationHandler, AppPageAuthorizationHandler>();
        Decorate<IAuthorizationPolicyProvider>(services, (_, inner) => new AppPagePolicyProvider(inner));
        services.AddAntiforgery(options => options.HeaderName = "RequestVerificationToken");
        builder.AddNotificationHandler<ContentPublishingNotification, AppBlocksValidator>();
        services.AddScoped<AppPageAssets>();
        services.AddTransient<Microsoft.AspNetCore.Razor.TagHelpers.ITagHelperComponent, AppPageAssetsTagHelperComponent>();
        services.AddScoped<AppBindVariableResolver>();
        builder.AddNotificationAsyncHandler<RoutingRequestNotification, UmbracoAppHost.Website.ProtectedPageCacheHeaders>();
        services.Configure<UmbracoPipelineOptions>(options => options.AddFilter(new UmbracoPipelineFilter("AppBlocks")
        {
            PostPipeline = app => app.UseInteractiveReportsSite(),
            PostMapEndpoints = endpoints => endpoints.MapAppBlocks(),
        }));
        return builder;
    }

    public static void MapAppBlocks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapInteractiveReportJson(InteractiveReportsSite.ReportsRoute);
        endpoints.MapInteractiveReportFileDownload(InteractiveReportsSite.DownloadRoute);
        endpoints.MapInteractiveReportJson(InteractiveReportsSite.BackOfficeReportsRoute);
        endpoints.MapInteractiveReportFileDownload(InteractiveReportsSite.BackOfficeDownloadRoute);
        endpoints.MapApplicationForms();
        endpoints.MapAttachments();
        endpoints.MapTabular();
    }

    private static object Create(IServiceProvider provider, ServiceDescriptor descriptor) => descriptor.ImplementationInstance
        ?? descriptor.ImplementationFactory?.Invoke(provider)
        ?? ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);

    private static void Decorate<T>(IServiceCollection services, Func<IServiceProvider, T, T> factory) where T : class
    {
        var descriptor = services.Last(d => d.ServiceType == typeof(T));
        services.Replace(new ServiceDescriptor(typeof(T), sp => factory(sp, (T)Create(sp, descriptor)), descriptor.Lifetime));
    }
}
