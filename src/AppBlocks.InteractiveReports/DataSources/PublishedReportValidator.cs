using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;

namespace UmbracoAppHost.DataSources;

public sealed class PublishedReportValidator(PublishedReportCatalog catalog) : INotificationHandler<ContentPublishingNotification>
{
    public void Handle(ContentPublishingNotification notification)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in notification.PublishedEntities.Where(n => n.ContentType.Alias == PublishedReportCatalog.ReportAlias))
        {
            var errors = new List<string>();
            if (catalog.ReadReport(node.Key, node.Name ?? "Report", alias => node.GetValue(alias), errors) is { } report
                && catalog.ReportOwner(report.Alias) is { } owner && owner != node.Key)
                errors.Add("Another published report already uses this alias.");
            if (!aliases.Add((node.GetValue("alias")?.ToString() ?? "").Trim())) errors.Add("Two reports in this publish operation have the same alias.");
            if (errors.Count > 0) notification.CancelOperation(new EventMessage("Report definition", node.Name + ": " + string.Join(" ", errors), EventMessageType.Error));
        }
    }
}
