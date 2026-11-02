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
    }
}
