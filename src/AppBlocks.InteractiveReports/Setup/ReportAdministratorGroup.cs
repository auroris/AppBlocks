using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;

namespace AppBlocks.InteractiveReports.Setup;

/// <summary>Installs the report-administration group without changing existing groups or memberships.</summary>
public sealed class ReportAdministratorGroup(IUserGroupService groups, IShortStringHelper strings, IRuntimeState runtime)
    : INotificationAsyncHandler<UmbracoApplicationStartedNotification>
{
    public const string Alias = "interactiveReportsAdministrator";
    public const string Name = "Interactive Reports Administrator";

    public async Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken cancellationToken)
    {
        if (runtime.Level != RuntimeLevel.Run || await groups.GetAsync(Alias) is not null) return;
        cancellationToken.ThrowIfCancellationRequested();
        var group = new UserGroup(strings) { Alias = Alias, Name = Name, Icon = "icon-table" };
        // The administration workspace lives alongside AppBlocks in the Settings sidebar.
        group.AddAllowedSection(Constants.Applications.Settings);
        var created = await groups.CreateAsync(group, Constants.Security.SuperUserKey, []);
        if (!created.Success && await groups.GetAsync(Alias) is null)
            throw new InvalidOperationException($"Could not create the {Name} group: {created.Status}.");
    }
}
