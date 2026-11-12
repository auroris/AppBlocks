using static AppBlocks.ServiceDecoration;
using AppBlocks.AntiSpam;
using AppBlocks.Api;
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
        services.AddSingleton<ConnectionCatalog>();
        services.AddScoped<ConnectionTables>();
        services.AddScoped<Samples.FormInventory>();
        // Also resolvable on its own, so a site's import tool can install the definitions before it imports what uses them.
        services.AddTransient<Setup.SchemaInstaller>();
        services.AddTransient<Setup.SchemaUsage>();
        // For packages that generate content nodes from code, such as their API endpoints and lists of values.
        services.AddTransient<Setup.OwnedNodeInstaller>();
        builder.PackageMigrationPlans().Add<Setup.AppBlocksMigrationPlan>();
        // The element types packages allow in a site's Block Grid data types, once the definitions are installed.
        builder.AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, Setup.BlockGridAllowances>();
        builder.AddNotificationAsyncHandler<Umbraco.Cms.Infrastructure.Migrations.Notifications.MigrationPlansExecutedNotification, Setup.BlockGridAllowances>();
        builder.AddNotificationAsyncHandler<ContentTypeSavedNotification, Setup.BlockGridAllowances>();
        services.TryAddScoped<ApplicationAccessPolicy>();
        services.AddControllersWithViews().AddApplicationPart(typeof(AppBlocksExtensions).Assembly);
        services.AddScoped<AppBlockContent>();
        // The texts the blocks show, with whatever sources a host registers (its Umbraco dictionary among them) answering first.
        services.AddScoped<AppBlocksText>();
        services.AddScoped<AppPageAccess>();
        services.AddScoped<FormResourceRegistry>();
        services.AddScoped<FormSubmissionService>();
        // The C# folder: compiled at run time only when the site brings a compiler, and read by the registries beside registered classes.
        services.AddOptions<Code.CodeOptions>().Bind(builder.Config.GetSection(Code.CodeOptions.Section));
        services.AddSingleton<Code.CodeCompilation>();
        services.AddHostedService(provider => provider.GetRequiredService<Code.CodeCompilation>());
        services.AddScoped<Code.CompiledTypes>();
        services.AddScoped<IDataSourcePolicy, Code.CompiledDataSourcePolicies>();
        builder.AddDataSources();
        builder.AddApiEndpoints();
        builder.AddAttachments(builder.Config);
        builder.AddSchedules();
        builder.AddTabular();
        builder.AddAntiSpam();
        services.AddScoped<DataSourcePreview>();
        services.AddSingleton<IAuthorizationHandler, AppPageAuthorizationHandler>();
        // The Member policy and antiforgery token endpoint a companion's own controllers use.
        AppBlocksAuthorization.Add(services);
        Decorate<IAuthorizationPolicyProvider>(services, (_, inner) => new AppPagePolicyProvider(inner));
        builder.AddNotificationAsyncHandler<ContentPublishingNotification, AppBlocksValidator>();
        // Bound at startup, so a value that is not AppBlocksPages, AllPages or Off is reported then rather than on the first page.
        services.AddOptions<Publishing.PageCacheOptions>().Bind(builder.Config.GetSection(Publishing.PageCacheOptions.Section)).ValidateOnStart();
        services.AddScoped<AppPageAssets>();
        services.AddTransient<Microsoft.AspNetCore.Razor.TagHelpers.ITagHelperComponent, AppPageAssetsTagHelperComponent>();
        services.AddScoped<AppBindVariableResolver>();
        builder.AddNotificationAsyncHandler<RoutingRequestNotification, AppBlocks.Publishing.PageCacheHeaders>();
        // A layout's ApplyCacheHeaders answers 304 after Razor has buffered output, which this filter discards; once, whoever else adds it.
        services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(options => { if (!options.Filters.OfType<Publishing.PageCacheResponseFilter>().Any()) options.Filters.Add(new Publishing.PageCacheResponseFilter()); });
        services.AddScoped<IAppBlocksTextSource, UmbracoDictionaryTextSource>();
        // Umbraco's website routing registers the Public Access check; whoever a host's IProtectedPageAdmission admits passes it, as on the blocks' endpoints.
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IPublicAccessRequestHandler)))
            Decorate<IPublicAccessRequestHandler>(services, (provider, inner) =>
                new AppBlocks.Publishing.ProtectedPageAdmissionRequestHandler(inner, provider.GetRequiredService<IPublicAccessService>()));
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
        endpoints.MapApiEndpoints();
        AppBlocksAuthorization.MapAntiforgeryToken(endpoints);
    }

}
