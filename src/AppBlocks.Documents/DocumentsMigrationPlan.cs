using AppBlocks.Setup;

namespace AppBlocks.Documents;

/// <summary>The migration plan of the Documents companion. Steps are added as in <see cref="AppBlocksMigrationPlan"/>.</summary>
public sealed class DocumentsMigrationPlan() : AppBlocksPackagePlan("AppBlocks.Documents", "AppBlocks.Documents")
{
    protected override void DefinePlan()
    {
        // The first state: definitions were tracked from here, with nothing to change yet.
        To("2026-11-02-tracked");
    }
}
