namespace AppBlocks.Authentication;

/// <summary>Who besides Umbraco's Administrators group counts as an administrator for AppBlocks' own backoffice screens.</summary>
public sealed class AdministratorOptions
{
    public const string Section = "AppBlocks";

    /// <summary>
    /// The aliases of further backoffice User groups whose members pass <see cref="AppBlocksAuthorization.AdministratorPolicy"/>, such as a
    /// site operations group given the Connections and Anti-spam screens without full Umbraco administration. Empty by default.
    /// </summary>
    public string[] AdministratorGroups { get; set; } = [];
}
