using AppBlocks.Setup;

namespace AppBlocks.InteractiveReports.Setup;

/// <summary>The migration plan of the Interactive Reports companion. Steps are added as in <see cref="AppBlocksMigrationPlan"/>.</summary>
public sealed class InteractiveReportsMigrationPlan() : AppBlocksPackagePlan("AppBlocks.InteractiveReports", "AppBlocks Interactive Reports")
{
    /// <summary>The state of a site that ran the Record links step this plan once had in place of the link visibility step.</summary>
    internal const string RecordLinksState = "2026-11-04-report-record-links";

    protected override void DefinePlan()
    {
        // The first state: definitions were tracked from here, with nothing to change yet.
        To("2026-11-02-tracked");
        To<InstallPackageDefinitions>("2026-11-02-package-install");
        // The report block's Create button and Open link settings.
        To<AddReportLinkVisibility>("2026-11-04-report-link-visibility");
        // The starting view editor's name without the earlier host's prefix.
        To<RenameReportStateEditor>("2026-11-08-report-state-editor");
        // The report definition's Access choice and groups.
        To<AddReportDefinitionAccess>("2026-11-08-report-definition-access");

        // A site that ran the Record links step joins the plan at the link visibility step, which maps its choices.
        From(RecordLinksState).To<AddReportLinkVisibility>("2026-11-04-report-link-visibility");
    }
}
