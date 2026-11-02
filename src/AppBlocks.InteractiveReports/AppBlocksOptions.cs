namespace AppBlocks.InteractiveReports;

public sealed class AppBlocksOptions
{
    public const string Section = "AppBlocks";
    /// <summary>Additional User Group aliases granted report administration, alongside Administrators and Interactive Reports Administrator.</summary>
    public string[] ReportAdministratorGroups { get; set; } = [];
    public bool IncludeBackOfficeReports { get; set; }
}
