namespace AppBlocks.Authentication;

/// <summary>Who besides Umbraco's Administrators group may use AppBlocks' administration, backing-class editor and database previews.</summary>
public sealed class AdministratorOptions
{
    public const string Section = "AppBlocks";

    /// <summary>
    /// The aliases of further backoffice User groups whose members pass <see cref="AppBlocksAuthorization.AdministratorPolicy"/>:
    /// AppBlocks' Settings screens, backing-class editor, and data source and API endpoint previews. Empty by default.
    /// </summary>
    public string[] AdministratorGroups { get; set; } = [];
}
