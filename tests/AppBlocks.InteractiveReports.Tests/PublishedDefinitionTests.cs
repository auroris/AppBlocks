using InteractiveReport.AspNetCore;
using InteractiveReport.Core.Model;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AppBlocks.InteractiveReports.DataSources;
using AppBlocks.DataSources;
using AppBlocks.InteractiveReports.Reports;

namespace AppBlocks.InteractiveReports.Tests;

public sealed class PublishedDefinitionTests
{
    [Fact]
    public void Report_nodes_are_refused_when_they_would_fail_to_run_or_bind_values_from_content()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddInteractiveReports(configuration).AddConnection("sample", _ => new SqliteConnection("Data Source=:memory:"));
        // A configured report runs its {{RowRestriction}} through a callback the site registered for it.
        services.Configure<InteractiveReportOptions>(options => options.Reports["seeded"] = new ReportDefinition { Connection = "sample", Sql = "SELECT 1 AS ID WHERE {{RowRestriction}}" });
        services.AddSingleton<DataSourceRuntime>();
        using var provider = services.AddReportBlockSupport().BuildServiceProvider();
        // An inline report reads nothing from Umbraco, so the content cache, navigation and data source catalogue stay unset.
        var catalog = new PublishedReportCatalog(null!, null!, null!, provider.GetRequiredService<IOptionsMonitor<InteractiveReportOptions>>(), configuration,
            provider.GetRequiredService<DataSourceRuntime>(), provider.GetRequiredService<ReportDefinitionCompiler>(),
            new PublishedDefinitionWarnings(NullLogger<PublishedDefinitionWarnings>.Instance), NullLogger<PublishedReportCatalog>.Instance);
        (PublishedReport? Report, List<string> Errors) Read(string alias, string sql, params (string Property, string Value)[] more)
        {
            var values = new Dictionary<string, string> { ["alias"] = alias, ["connection"] = "sample", ["sql"] = sql };
            foreach (var (property, value) in more) values[property] = value;
            var errors = new List<string>();
            return (catalog.ReadReport(Guid.NewGuid(), alias, property => values.GetValueOrDefault(property), errors), errors);
        }

        var (plain, accepted) = Read("notes", "SELECT 1 AS ID", ("controls", "search Search sort"), ("defaultPageSize", "20"), ("maxPageSize", "200"),
            ("options", """{ "sourceName": "orders" }"""));
        Assert.Empty(accepted);
        Assert.Equal(new[] { "search", "sort" }, plain!.Definition.Features);
        Assert.Null(plain.Definition.ContextParams);
        Assert.Null(plain.Definition.SourceName); // Another report's identity would borrow its row-access decisions.

        // No callback decides this report's marker, so Interactive Reports would refuse it on every request.
        Assert.Contains("{{RowRestriction}}", Assert.Single(Read("notes", "SELECT 1 AS ID WHERE {{RowRestriction}}").Errors));
        Assert.Empty(Read(RecentPagesReport.Name, "SELECT 1 AS ID WHERE {{RowRestriction}}").Errors);
        Assert.Empty(Read("seeded", "SELECT 2 AS ID WHERE {{RowRestriction}}").Errors);

        Assert.Contains("'sortt'", Assert.Single(Read("notes", "SELECT 1 AS ID", ("controls", "search sortt")).Errors));
        Assert.Contains("'pivots'", Assert.Single(Read("notes", "SELECT 1 AS ID", ("options", """{ "features": ["pivots"] }""")).Errors));
        Assert.Contains("default page size (300)", Assert.Single(Read("notes", "SELECT 1 AS ID", ("defaultPageSize", "300"), ("maxPageSize", "200")).Errors));
        Assert.Contains("default page size (1500)", Assert.Single(Read("notes", "SELECT 1 AS ID", ("defaultPageSize", "1500")).Errors));
        Assert.Contains("positive", Assert.Single(Read("notes", "SELECT 1 AS ID", ("options", """{ "maxPageSize": 0 }""")).Errors));
        Assert.Contains("contextParams", Assert.Single(Read("notes", "SELECT 1 AS ID WHERE 1 = @me",
            ("options", """{ "contextParams": { "me": { "claim": "sub" } } }""")).Errors));
    }
}
