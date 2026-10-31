namespace AppBlocks.InteractiveReports;

public sealed class AppBlocksOptions
{
    public const string Section = "AppBlocks";
    public string[] ReportAdministratorGroups { get; set; } = ["admin"];
    public bool IncludeBackOfficeReports { get; set; }
}
