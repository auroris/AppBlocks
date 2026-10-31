using InteractiveReport.Core.Model;
using AppBlocks.InteractiveReports;

namespace AppBlocks.InteractiveReports.Tests;

public sealed class PaperReportTests
{
    [Fact]
    public void Print_action_uses_an_unused_synthetic_id_and_retains_existing_columns()
    {
        var report = new ReportDefinition { DefaultState = new() { ActiveTable = "orders", Tables = new() { ["orders"] = new() {
            From = "definition", Composables = [new() { Kind = "compute", Computed = [new() { Id = "ir1", Expr = "1" }] }, new() { Kind = "select", Columns = ["CUSTOMER"] }],
        } } } };
        InteractiveReportPrintLinks.AddPrintAction(report, "ORDER_ID");
        var operations = report.DefaultState.Tables["orders"].Composables!;
        Assert.Equal(new[] { "CUSTOMER", "ir2" }, operations.Single(c => c.Kind == "select").Columns);
        Assert.Equal("ORDER_ID", operations.Single(c => c.Kind == "formats").Formats!["ir2"].KeyColumn);
        Assert.Equal("app-print", operations.Single(c => c.Kind == "formats").Formats!["ir2"].Command);
    }

}
