using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;

namespace AppBlocks.Documents.PaperReports;

/// <summary>
/// Adds every installed template to the paper report type's allowed templates, so a host adds a paper report template by
/// registering it rather than by editing the type the library ships. Runs at startup, whenever a template is saved or deleted,
/// and whenever the type itself is saved, including its initial schema installation. It only adds: a template the type already
/// allows stays, and so does the default. A host that chooses the type's templates itself turns this off with
/// <see cref="AllowAllTemplatesSetting"/>.
/// </summary>
public sealed class PaperReportTemplates(IRuntimeState runtime, IContentTypeService contentTypes, ITemplateService templates,
    IConfiguration configuration, ILogger<PaperReportTemplates> logger)
    : INotificationAsyncHandler<UmbracoApplicationStartedNotification>, INotificationAsyncHandler<TemplateSavedNotification>,
        INotificationAsyncHandler<TemplateDeletedNotification>, INotificationAsyncHandler<ContentTypeSavedNotification>
{
    /// <summary>Whether every installed template is added to the paper report type; true unless the setting is false.</summary>
    public const string AllowAllTemplatesSetting = "AppBlocks:Documents:AllowAllTemplates";

    public Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken cancellationToken) => SynchronizeAsync();
    public Task HandleAsync(TemplateSavedNotification notification, CancellationToken cancellationToken) => SynchronizeAsync();
    public Task HandleAsync(TemplateDeletedNotification notification, CancellationToken cancellationToken) => SynchronizeAsync();

    /// <summary>Its own save raises this too; the second pass finds nothing to add and stops.</summary>
    public Task HandleAsync(ContentTypeSavedNotification notification, CancellationToken cancellationToken)
        => notification.SavedEntities.Any(type => type.Alias == PaperReportDefinition.Alias) ? SynchronizeAsync() : Task.CompletedTask;

    internal static bool AllowsAllTemplates(IConfiguration configuration) => configuration.GetValue(AllowAllTemplatesSetting, true);

    /// <summary>
    /// The type's allowed templates with the installed ones it lacks added after them, or null when it lacks none. A template the
    /// type allows that is no longer installed is left out.
    /// </summary>
    internal static IReadOnlyList<ITemplate>? WithAllInstalled(IEnumerable<ITemplate> allowed, IReadOnlyCollection<ITemplate> installed)
    {
        var current = allowed.Where(template => installed.Any(i => i.Id == template.Id)).ToList();
        var missing = installed.Where(template => current.All(c => c.Id != template.Id)).ToList();
        return missing.Count == 0 ? null : [.. current, .. missing];
    }

    private async Task SynchronizeAsync()
    {
        if (runtime.Level != RuntimeLevel.Run || !AllowsAllTemplates(configuration)) return;
        try
        {
            // Before the first boot's schema installation has created the type there is nothing to keep.
            if (contentTypes.Get(PaperReportDefinition.Alias) is not { } type) return;
            var installed = (await templates.GetAllAsync(Array.Empty<string>())).ToList();
            if (WithAllInstalled(type.AllowedTemplates ?? [], installed) is not { } allowed) return;
            type.AllowedTemplates = allowed;
            var result = await contentTypes.UpdateAsync(type, Constants.Security.SuperUserKey);
            if (result.Success) logger.LogInformation("The paper report type now allows the {Count} installed templates.", installed.Count);
            else logger.LogWarning("The paper report type's allowed templates could not be updated: {Status}.", result.Result);
        }
        // A failure here must neither stop the site nor fail the template save that raised it.
        catch (Exception error) { logger.LogError(error, "The paper report type's allowed templates could not be updated."); }
    }
}
