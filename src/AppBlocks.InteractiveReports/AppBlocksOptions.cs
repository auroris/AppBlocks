namespace AppBlocks.InteractiveReports;

public sealed class AppBlocksOptions
{
    public const string Section = "AppBlocks";
    /// <summary>User Group aliases explicitly granted report administration by the application.</summary>
    public string[] ReportAdministratorGroups { get; set; } = [];
    public bool IncludeBackOfficeReports { get; set; }
}
