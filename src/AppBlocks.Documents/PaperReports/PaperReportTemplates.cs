using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;

namespace UmbracoAppHost.PaperReports;

/// <summary>
/// Keeps the paper report type's allowed templates equal to the installed templates, so a host adds a paper report
/// template by registering it rather than by editing the type the library ships. Runs at startup, after uSync's first
/// boot import, whenever a template is saved or deleted, and whenever the type itself is saved, which a uSync import
/// of the library's definition does with the shorter list the export carries.
/// </summary>
public sealed class PaperReportTemplates(IRuntimeState runtime, IContentTypeService contentTypes, ITemplateService templates,
    ILogger<PaperReportTemplates> logger)
    : INotificationAsyncHandler<UmbracoApplicationStartedNotification>, INotificationAsyncHandler<TemplateSavedNotification>,
        INotificationAsyncHandler<TemplateDeletedNotification>, INotificationAsyncHandler<ContentTypeSavedNotification>
{
    public Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken cancellationToken) => SynchronizeAsync();
    public Task HandleAsync(TemplateSavedNotification notification, CancellationToken cancellationToken) => SynchronizeAsync();
    public Task HandleAsync(TemplateDeletedNotification notification, CancellationToken cancellationToken) => SynchronizeAsync();

    /// <summary>Its own save raises this too; the second pass finds the lists equal and stops.</summary>
    public Task HandleAsync(ContentTypeSavedNotification notification, CancellationToken cancellationToken)
        => notification.SavedEntities.Any(type => type.Alias == PaperReportDefinition.Alias) ? SynchronizeAsync() : Task.CompletedTask;

    private async Task SynchronizeAsync()
    {
        if (runtime.Level != RuntimeLevel.Run) return;
        try
        {
            // Before the first boot's import has created the type there is nothing to keep.
            if (contentTypes.Get(PaperReportDefinition.Alias) is not { } type) return;
            var installed = (await templates.GetAllAsync(Array.Empty<string>())).ToList();
            if (installed.Select(t => t.Id).ToHashSet().SetEquals(type.AllowedTemplates?.Select(t => t.Id) ?? [])) return;
            // Every installed template is allowed, so the default template stays allowed and stays the default.
            type.AllowedTemplates = installed;
            var result = await contentTypes.UpdateAsync(type, Constants.Security.SuperUserKey);
            if (result.Success) logger.LogInformation("The paper report type now allows the {Count} installed templates.", installed.Count);
            else logger.LogWarning("The paper report type's allowed templates could not be updated: {Status}.", result.Result);
        }
        // A failure here must neither stop the site nor fail the template save that raised it.
        catch (Exception error) { logger.LogError(error, "The paper report type's allowed templates could not be updated."); }
    }
}
