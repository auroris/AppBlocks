using InteractiveReport.Core.Execution;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;

namespace UmbracoAppHost.DataSources;

public sealed class PublishedDataValidator(PublishedDataCatalog catalog) : INotificationHandler<ContentPublishingNotification>
{
    public void Handle(ContentPublishingNotification notification)
    {
        var aliases = new HashSet<(string Type, string Alias)>();
        foreach (var node in notification.PublishedEntities.Where(n => n.ContentType.Alias is PublishedDataCatalog.ReportAlias or PublishedDataCatalog.ListAlias))
        {
            var errors = new List<string>();
            if (node.ContentType.Alias == PublishedDataCatalog.ReportAlias)
            {
                if (catalog.ReadReport(node.Key, node.Name ?? "Report", alias => node.GetValue(alias), errors) is { } report
                    && catalog.ReportOwner(report.Alias) is { } owner && owner != node.Key)
                    errors.Add("Another published report already uses this alias.");
            }
            else if (catalog.ReadList(node.Key, node.Name ?? "List of values", alias => node.GetValue(alias), errors) is { } list
                && catalog.Lists.TryGetValue(list.Alias, out var existing) && existing.Key != node.Key)
                errors.Add("Another published list already uses this alias.");
            if (!aliases.Add((node.ContentType.Alias, (node.GetValue("alias")?.ToString() ?? "").Trim().ToUpperInvariant()))) errors.Add("Two definitions in this publish operation have the same alias.");
            if (errors.Count > 0) notification.CancelOperation(new EventMessage("Data definition", node.Name + ": " + string.Join(" ", errors), EventMessageType.Error));
        }
    }
}

public static class PublishedDataDefinitions
{
    public static IUmbracoBuilder AddPublishedDataDefinitions(this IUmbracoBuilder builder)
    {
        var services = builder.Services;
        services.AddScoped<PublishedDataCatalog>(); services.AddSingleton<PublishedDefinitionWarnings>();
        services.AddScoped<DataSourceQueries>(); services.AddScoped<ListOfValuesService>();
        services.AddScoped<DataSourceReportBindings>(); services.AddScoped<DataSourceReportAccess>();
        var original = services.LastOrDefault(d => d.ServiceType == typeof(IContextParameterResolver));
        if (original is not null)
        {
            services.Add(new ServiceDescriptor(typeof(OriginalContextParameters), provider => new OriginalContextParameters(
                original.ImplementationInstance as IContextParameterResolver
                ?? original.ImplementationFactory?.Invoke(provider) as IContextParameterResolver
                ?? (IContextParameterResolver)ActivatorUtilities.CreateInstance(provider, original.ImplementationType!)), original.Lifetime));
            services.Replace(new ServiceDescriptor(typeof(IContextParameterResolver), typeof(DataSourceContextParameters), original.Lifetime));
        }
        builder.AddNotificationHandler<ContentPublishingNotification, PublishedDataValidator>();
        return builder;
    }
}
