namespace AppBlocks.Setup;

/// <summary>
/// The migration plan of the AppBlocks package. Add a step for each change to an existing site's definitions or block values:
/// a class deriving from <c>AsyncMigrationBase</c>, taking the Umbraco services it needs in its constructor, added after the last
/// step as <c>To&lt;TheStep&gt;("yyyy-MM-dd-what-it-does")</c>. Steps are never reordered, renamed or removed once a site may have
/// run them. See docs/developing.md.
/// </summary>
public sealed class AppBlocksMigrationPlan() : AppBlocksPackagePlan("AppBlocks", "AppBlocks")
{
    protected override void DefinePlan()
    {
        // The first state: definitions were tracked from here, with nothing to change yet.
        To("2026-11-02-tracked");
        // The SQL data source's optional backing class.
        To<AddDataSourceBackingClass>("2026-11-02-data-source-backing-class");
        To<InstallPackageDefinitions>("2026-11-02-package-install");
        To<UseSingleBlockFormInterface>("2026-11-02-single-block-form-interface");
        // API endpoint nodes under Data sources.
        To<AddApiEndpoints>("2026-11-03-api-endpoints");
        // The data source's concurrency choices.
        To<AddDataSourceConcurrency>("2026-11-04-data-source-concurrency");
        // The same step again, for sites that ran the first under the earlier VersionColumn choice: it replaces that choice with
        // Checksum. On every other site it leaves the definition as the first run did.
        To<AddDataSourceConcurrency>("2026-11-04-checksum-concurrency");
        // The pickers' editor UI aliases, from AppHost.* to AppBlocks.*.
        To<RenameEditorUiAliases>("2026-11-08-appblocks-editor-ui-aliases");
    }
}
