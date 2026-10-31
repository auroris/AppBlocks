using AppBlocks.InteractiveReports.DataSources;
using AppBlocks.Schedules;

namespace AppBlocks.InteractiveReports;

public sealed class ReportScheduleSources(PublishedReportCatalog catalog) : IScheduleSourceProvider
{
    public bool TryResolve(string alias, out ScheduleSource? source)
    {
        source = null;
        if (!catalog.ReportNodes.TryGetValue(alias, out var node)) return catalog.IsUnavailableReport(alias);
        if (node.DataSource is { } definition) source = new(alias, definition, FromReport: true);
        return true;
    }
    public IReadOnlyList<string>? Columns(ScheduleSource source) => source.FromReport ? catalog.ReportColumns(source.Alias) : null;
}
