using System.Text.RegularExpressions;
using InteractiveReport.Core.Model;

namespace UmbracoAppHost.PaperReports;

public static class PaperReportLinks
{
    public static Guid? PickedKey(string value)
    {
        if (Guid.TryParse(value, out var key)) return key;
        const string prefix = "umb://document/";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Guid.TryParse(value[prefix.Length..], out key) ? key : null;
    }

    /// <summary>Adds a normal report action column, using the adapter's supported command/key contract.</summary>
    public static void AddPrintAction(ReportDefinition report, string keyColumn)
    {
        report.DefaultState ??= new ReportState { ActiveTable = "base", Tables = new() { ["base"] = new() { From = "definition", Composables = [] } } };
        var state = report.DefaultState;
        if (state.Tables is null || !state.Tables.TryGetValue(state.ActiveTable ?? "base", out var table)) return;
        // Summary/chart rows have no individual record key. Their toolbar still prints the paper report.
        if (table.Composables?.Any(c => c.Kind is "group" or "pivot" or "chart") == true) return;
        table.Composables ??= [];
        var used = state.Tables.Values.SelectMany(t => t.Composables ?? [])
            .SelectMany(c => (c.Computed?.Select(x => x.Id) ?? []).Concat(c.Values?.Select(x => x.Id) ?? []))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var number = 1;
        while (used.Contains("ir" + number)) number++;
        var id = "ir" + number;
        table.Composables.Add(new() { Kind = "compute", Computed = [new() { Id = id, Label = "Print", Expr = "'Print'" }] });
        table.Composables.Add(new() { Kind = "formats", Formats = new() { [id] = new() { DisplayAs = "action", Command = "app-print", KeyColumn = keyColumn } } });
        foreach (var selection in table.Composables.Where(c => c.Kind == "select" && c.Columns is not null)) selection.Columns!.Add(id);
    }
}
