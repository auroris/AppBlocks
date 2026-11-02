using Umbraco.Cms.Core.Packaging;

namespace AppBlocks.Setup;

/// <summary>
/// The Umbraco package migration plan of AppBlocks or a companion: the ordered steps that carry a site's existing definitions
/// and content from one version of the package to the next. Umbraco runs the steps a site has not reached on startup, before
/// the application has started, and records the last one in its key-value store under the plan's name. A fresh site, and one
/// from before any step existed, runs every step, so each step does nothing when what it changes is not there: the
/// <see cref="SchemaInstaller"/> installs the current definitions right after.
/// </summary>
/// <param name="packageId">The package's id, also the plan's name: the manifest's <c>id</c>, so the backoffice pairs them.</param>
/// <param name="packageName">The package's name as its manifest gives it.</param>
public abstract class AppBlocksPackagePlan(string packageId, string packageName) : PackageMigrationPlan(packageId, packageName, packageId);

/// <summary>What a reinstall or an uninstall did. Notes are what a person should know, one sentence each.</summary>
public sealed record SchemaChange(int Created, int Updated, int Deleted, IReadOnlyList<string> Notes);

/// <summary>Where a package's definitions stand on this site.</summary>
/// <param name="Declared">The definitions in the package's schema.</param>
/// <param name="Installed">How many of them are on the site, by key.</param>
/// <param name="State">The last migration step the site reached, as Umbraco recorded it; null before the plan has run.</param>
/// <param name="FinalState">The last step the installed package has.</param>
/// <param name="Pending">Whether Umbraco has steps to run for this package.</param>
public sealed record DefinitionsPackage(string Id, string Name, string Version, int Declared, int Installed, string? State, string? FinalState, bool Pending);

/// <param name="Uninstalled">Whether the definitions were uninstalled and nothing has installed them since.</param>
public sealed record DefinitionsStatus(bool Uninstalled, IReadOnlyList<DefinitionsPackage> Packages);

/// <summary>An uninstall that would delete the site's content or properties, and so did nothing. Each problem says what to remove first.</summary>
public sealed class SchemaInUseException(IReadOnlyList<string> problems) : InvalidOperationException(string.Join(" ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}
