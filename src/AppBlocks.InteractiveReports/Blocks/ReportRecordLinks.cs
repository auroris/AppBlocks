namespace AppBlocks.InteractiveReports.Blocks;

/// <summary>
/// Which links to its form page a report block offers, as the block's Record links setting chooses: Open, a link on each row to
/// that record, and Create, a button above the report for a new one. Without a form page the block offers neither.
/// </summary>
public enum ReportRecordLinks { OpenAndCreate, OpenOnly, CreateOnly, None }

public static class ReportRecordLinkChoices
{
    /// <summary>The report block's property.</summary>
    public const string Property = "recordLinks";

    /// <summary>The stored choice. An empty value, as a block saved before the setting existed has, and one no choice names are Open and create.</summary>
    public static ReportRecordLinks Parse(string? value) => value?.Trim() switch
    {
        "Open only" => ReportRecordLinks.OpenOnly,
        "Create only" => ReportRecordLinks.CreateOnly,
        "None" => ReportRecordLinks.None,
        _ => ReportRecordLinks.OpenAndCreate,
    };

    public static bool Opens(this ReportRecordLinks links) => links is ReportRecordLinks.OpenAndCreate or ReportRecordLinks.OpenOnly;
    public static bool Creates(this ReportRecordLinks links) => links is ReportRecordLinks.OpenAndCreate or ReportRecordLinks.CreateOnly;
}
