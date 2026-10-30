using AppBlocks.Connections;
using AppBlocks.Setup;
using AppBlocks.InteractiveReports.Reports;
using InteractiveReport.AspNetCore;
using InteractiveReport.Client.FileDownload;
using InteractiveReport.Client.Json;
using InteractiveReport.Core.Definitions;
using InteractiveReport.Core.Execution;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Web.Common.ApplicationBuilder;
using UmbracoAppHost.Blocks;
using UmbracoAppHost.DataSources;
using UmbracoAppHost.Reports;

namespace AppBlocks.InteractiveReports;

[ComposeAfter(typeof(AppBlocksComposer))]
public sealed class InteractiveReportsComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder) => builder.AddAppBlocksInteractiveReports();
}

public static class InteractiveReportsExtensions
{
    private sealed record Registration(InteractiveReportBuilder Reports);

    /// <summary>Adds the optional reports companion. Its composer calls this automatically when installed.</summary>
    public static IUmbracoBuilder AddAppBlocksInteractiveReports(this IUmbracoBuilder builder, Action<InteractiveReportBuilder>? configure = null)
    {
        builder.AddAppBlocks();
        var services = builder.Services;
        if (services.FirstOrDefault(d => d.ServiceType == typeof(Registration))?.ImplementationInstance is Registration existing)
        {
            configure?.Invoke(existing.Reports);
            return builder;
        }

        services.AddOptions<AppBlocksOptions>().Bind(builder.Config.GetSection(AppBlocksOptions.Section));
        services.TryAddScoped<IReportAccessPolicy, DefaultReportAccessPolicy>();
        services.AddControllersWithViews().AddApplicationPart(typeof(InteractiveReportsExtensions).Assembly);
        services.AddSingleton(new AppBlocksSchema(typeof(InteractiveReportsExtensions).Assembly));
        services.AddScoped<IAppBlocksSchemaMigration, ReportBlockMigration>();
        services.AddScoped<IAppBlockPlugin, InteractiveReportBlock>();
        services.AddScoped<IAppBlocksCatalogueContributor, ReportCatalogueContributor>();
        services.AddSingleton<ReportPrintPreviews>();
        services.AddSingleton<ReportSourcePreviews>();

        builder.AddInteractiveReportsSite(builder.Config, reports =>
        {
            var connections = (AppBlocksBuilder)services.Single(d => d.ServiceType == typeof(AppBlocksBuilder)).ImplementationInstance!;
            void Register(string name)
            {
                if (connections.Dialects.TryGetValue(name, out var dialect))
                    reports.AddConnection(name, connections.Connections[name], ReportConnectionBridge.ToReportDialect(dialect));
                else reports.AddConnection(name, connections.Connections[name]);
            }
            foreach (var name in connections.ConnectionNames) Register(name);
            // Hosts can add connections after composers have run.
            connections.ConnectionAdded += Register;
            configure?.Invoke(reports);
            services.AddSingleton(new Registration(reports));
        });
        services.PostConfigure<InteractiveReportOptions>(options =>
        {
            if (string.IsNullOrWhiteSpace(options.SavedReports.Connection) && string.IsNullOrWhiteSpace(options.SavedReports.DataSource))
                options.SavedReports.Connection = ConnectionCatalog.ApplicationConnection;
        });
        Decorate<IReportConnectionFactory>(services, (sp, inner) => new ManagedConnectionFactory(inner, sp.GetRequiredService<ConnectionCatalog>()));
        // Legacy report registrations remain available to forms without making the base depend on reporting.
        Decorate<IApplicationConnectionFactory>(services, (sp, inner) => new ReportConnectionBridge(inner,
            sp.GetRequiredService<AppBlocksBuilder>(), sp.GetRequiredService<ConnectionCatalog>(),
            sp.GetRequiredService<IReportConnectionFactory>(), sp.GetRequiredService<ReportDefinitionCompiler>()));
        var configuredStore = services.Last(d => d.ServiceType == typeof(IReportDefinitionStore));
        services.AddSingleton(sp => new ConfiguredReportStore((IReportDefinitionStore)Create(sp, configuredStore)));
        services.Replace(ServiceDescriptor.Scoped<IReportDefinitionStore, AppReportDefinitionStore>());
        services.AddScoped<PublishedReportCatalog>();
        services.AddScoped<UmbracoAppHost.Schedules.IScheduleSourceProvider, ReportScheduleSources>();
        services.AddScoped<DataSourceReportBindings>();
        services.AddScoped<DataSourceReportAccess>();
        var original = services.Last(d => d.ServiceType == typeof(IContextParameterResolver));
        services.Add(new ServiceDescriptor(typeof(OriginalContextParameters), sp => new OriginalContextParameters(
            (IContextParameterResolver)Create(sp, original)), original.Lifetime));
        services.Replace(new ServiceDescriptor(typeof(IContextParameterResolver), typeof(DataSourceContextParameters), original.Lifetime));
        builder.AddNotificationHandler<ContentPublishingNotification, PublishedReportValidator>();
        services.Configure<UmbracoPipelineOptions>(options => options.AddFilter(new UmbracoPipelineFilter("AppBlocks.InteractiveReports")
        {
            PostPipeline = app => app.UseInteractiveReportsSite(),
            PostMapEndpoints = endpoints => endpoints.MapAppBlocksInteractiveReports(),
        }));
        return builder;
    }

    public static void MapAppBlocksInteractiveReports(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapInteractiveReportJson(InteractiveReportsSite.ReportsRoute);
        endpoints.MapReportPrinting(InteractiveReportsSite.ReportsRoute);
        endpoints.MapInteractiveReportFileDownload(InteractiveReportsSite.DownloadRoute);
        endpoints.MapInteractiveReportJson(InteractiveReportsSite.BackOfficeReportsRoute);
        endpoints.MapInteractiveReportFileDownload(InteractiveReportsSite.BackOfficeDownloadRoute);
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
