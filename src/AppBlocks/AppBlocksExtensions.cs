using AppBlocks.AntiSpam;
using AppBlocks.Connections;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Web.Common.ApplicationBuilder;
using Umbraco.Cms.Web.Website.Routing;
using AppBlocks.Authentication;
using AppBlocks.Attachments;
using AppBlocks.Blocks;
using AppBlocks.DataSources;
using AppBlocks.Forms;
using AppBlocks.Schedules;
using AppBlocks.Tabular;

namespace AppBlocks;

public sealed class AppBlocksComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder) => builder.AddAppBlocks();
}

public static class AppBlocksExtensions
{
    /// <summary>Automatically called by the composer. Hosts may call it again to register business databases.</summary>
    public static IUmbracoBuilder AddAppBlocks(this IUmbracoBuilder builder, Action<AppBlocksBuilder>? configure = null)
    {
        var services = builder.Services;
        if (services.FirstOrDefault(d => d.ServiceType == typeof(AppBlocksBuilder))?.ImplementationInstance is AppBlocksBuilder existing)
        {
            configure?.Invoke(existing);
            return builder;
        }
        var connections = new AppBlocksBuilder();
        services.AddSingleton(connections);
        connections.AddConnection(ConnectionCatalog.ApplicationConnection, provider => provider.GetRequiredService<ConnectionCatalog>().Openable(ConnectionCatalog.ApplicationConnection));
        connections.AddAttachmentsDatabase(builder.Config);
        configure?.Invoke(connections);
        services.AddSingleton<IApplicationConnectionFactory, ApplicationConnections>();
        services.AddSingleton(new Setup.AppBlocksSchema(typeof(AppBlocksExtensions).Assembly));
        services.AddScoped<Setup.SchemaProperties>();
        services.AddDataProtection();
        services.AddSingleton<ConnectionCatalog>();
        services.AddScoped<ConnectionTables>();
        services.AddScoped<Samples.FormInventory>();
        // Also resolvable on its own, so a site's import tool can install the definitions before it imports what uses them.
        services.AddTransient<Setup.SchemaInstaller>();
        builder.AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, Setup.SchemaInstaller>();
        builder.PackageMigrationPlans().Add<Setup.AppBlocksMigrationPlan>();
        services.TryAddScoped<ApplicationAccessPolicy>();
        services.AddControllersWithViews().AddApplicationPart(typeof(AppBlocksExtensions).Assembly);
        services.AddScoped<AppBlockContent>();
        services.AddScoped<AppPageAccess>();
        services.AddScoped<FormResourceRegistry>();
        builder.AddDataSources();
        builder.AddAttachments(builder.Config);
        builder.AddSchedules();
        builder.AddTabular();
        builder.AddAntiSpam();
        services.AddScoped<DataSourcePreview>();
        services.AddSingleton<IAuthorizationHandler, AppPageAuthorizationHandler>();
        Decorate<IAuthorizationPolicyProvider>(services, (_, inner) => new AppPagePolicyProvider(inner));
        services.AddAntiforgery(options => options.HeaderName = "RequestVerificationToken");
        builder.AddNotificationHandler<ContentPublishingNotification, AppBlocksValidator>();
        services.AddScoped<AppPageAssets>();
        services.AddTransient<Microsoft.AspNetCore.Razor.TagHelpers.ITagHelperComponent, AppPageAssetsTagHelperComponent>();
        services.AddScoped<AppBindVariableResolver>();
        builder.AddNotificationAsyncHandler<RoutingRequestNotification, AppBlocks.Publishing.ProtectedPageCacheHeaders>();
        // Umbraco's website routing registers the Public Access check; a backoffice User who may edit a page passes it.
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IPublicAccessRequestHandler)))
            Decorate<IPublicAccessRequestHandler>(services, (provider, inner) =>
                new AppBlocks.Publishing.EditorPublicAccessRequestHandler(inner, provider.GetRequiredService<IPublicAccessService>()));
        services.Configure<UmbracoPipelineOptions>(options => options.AddFilter(new UmbracoPipelineFilter("AppBlocks")
        {
            PostMapEndpoints = endpoints => endpoints.MapAppBlocks(),
        }));
        return builder;
    }

    public static void MapAppBlocks(this IEndpointRouteBuilder endpoints)
    {
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
