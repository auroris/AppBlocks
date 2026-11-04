using AppBlocks.Setup;

namespace AppBlocks.InteractiveReports.Setup;

/// <summary>The migration plan of the Interactive Reports companion. Steps are added as in <see cref="AppBlocksMigrationPlan"/>.</summary>
public sealed class InteractiveReportsMigrationPlan() : AppBlocksPackagePlan("AppBlocks.InteractiveReports", "AppBlocks Interactive Reports")
{
    protected override void DefinePlan()
    {
        // The first state: definitions were tracked from here, with nothing to change yet.
        To("2026-11-02-tracked");
        To<InstallPackageDefinitions>("2026-11-02-package-install");
        // The report block's Create button and Open link settings.
        To<AddReportLinkVisibility>("2026-11-04-report-link-visibility");
    }
}
