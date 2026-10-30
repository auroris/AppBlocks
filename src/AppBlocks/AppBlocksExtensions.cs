using AppBlocks.Connections;
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
using UmbracoAppHost.Schedules;
using UmbracoAppHost.Tabular;

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
        services.AddDataProtection();
        services.AddSingleton<ConnectionCatalog>();
        services.AddScoped<ConnectionTables>();
        services.AddScoped<Samples.FormInventory>();
        builder.AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, Setup.SchemaInstaller>();
        services.TryAddScoped<ApplicationAccessPolicy>();
        services.AddControllersWithViews().AddApplicationPart(typeof(AppBlocksExtensions).Assembly);
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
