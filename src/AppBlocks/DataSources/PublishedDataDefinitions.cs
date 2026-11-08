using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;

namespace AppBlocks.DataSources;

public sealed class PublishedDataValidator(PublishedDataCatalog catalog, BindVariableProviders? providers = null) : INotificationAsyncHandler<ContentPublishingNotification>
{
    public async Task HandleAsync(ContentPublishingNotification notification, CancellationToken cancellationToken)
    {
        await catalog.LoadAsync();
        var aliases = new HashSet<(string Type, string Alias)>();
        foreach (var node in notification.PublishedEntities.Where(n => n.ContentType.Alias is PublishedDataCatalog.ListAlias))
        {
            var errors = new List<string>();
            var list = catalog.ReadList(node.Key, node.Name ?? "List of values", alias => node.GetValue(alias), errors);
            if (list is not null && catalog.Lists.TryGetValue(list.Alias, out var existing) && existing.Key != node.Key)
                errors.Add("Another published list already uses this alias.");
            if (!aliases.Add((node.ContentType.Alias, (node.GetValue("alias")?.ToString() ?? "").Trim().ToUpperInvariant()))) errors.Add("Two definitions in this publish operation have the same alias.");
            if (errors.Count > 0) notification.CancelOperation(new EventMessage("Data definition", node.Name + ": " + string.Join(" ", errors), EventMessageType.Error));
            // A selected data source was checked when it was published; a list's own SQL is checked here.
            else if (list is not null && string.IsNullOrWhiteSpace(node.GetValue("dataSource")?.ToString())
                && providers?.UndeclaredWarning(list.DataSource.ApplicationNames()) is { } warning)
                notification.Messages.Add(new EventMessage("Data definition", node.Name + ": " + warning, EventMessageType.Warning));
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
        builder.AddNotificationAsyncHandler<ContentPublishingNotification, PublishedDataValidator>();
        return builder;
    }
}
