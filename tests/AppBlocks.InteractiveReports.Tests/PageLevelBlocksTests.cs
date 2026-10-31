using System.Text.Json.Nodes;
using InteractiveReport.AspNetCore;
using InteractiveReport.Core.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using AppBlocks.Blocks;
using AppBlocks.Forms;
using AppBlocks.Tests;

namespace AppBlocks.InteractiveReports.Tests;

/// <summary>
/// What a Block Application Page accepts at its top level: besides forms, reports, schedules and tabular forms, the
/// layouts and content blocks that website pages take, with their areas' contents, so a page of reports can carry
/// its own help text and script. Fields and actions still belong to a form, and a page still holds one form at most.
/// </summary>
public sealed class PageLevelBlocksTests
{
    private const string ReportSource = """{"kind":"sql","connection":"sample","source":"SELECT 1 AS ORDER_ID"}""";

    [Fact]
    public void Layouts_and_content_blocks_stand_at_the_page_level()
    {
        using var provider = Services();
        using var scope = provider.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<AppBlocksValidator>();
        var columns = Guid.NewGuid(); var text = Guid.NewGuid(); var report = Guid.NewGuid(); var html = Guid.NewGuid();
        var page = Page(
            [Block("appColumns", columns), Block("appRichText", text, ("text", "<p>Reports of the day.</p>")), Block("appReport", report, ("dataSource", ReportSource)), Block("appHtml", html, ("html", "<script src=\"/inn/page-links.js\"></script>"))],
            [Item(columns, Area(Item(text), Item(report))), Item(html)]);
        Assert.Empty(validator.Validate(page));
        Assert.Empty(validator.ValidateWebsite(page));
    }

    [Fact]
    public void Fields_at_the_page_level_still_belong_to_a_form()
    {
        using var provider = Services();
        using var scope = provider.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<AppBlocksValidator>();
        var columns = Guid.NewGuid(); var field = Guid.NewGuid(); var actions = Guid.NewGuid();
        var page = Page([Block("appColumns", columns), Block("appText", field, ("field", "customer")), Block("appActions", actions)], [Item(columns, Area(Item(field))), Item(actions)]);
        var errors = validator.Validate(page);
        Assert.Contains(errors, error => error == "Fields and form actions must be placed inside a form.");
    }

    [Fact]
    public void A_report_page_with_no_form_needs_no_form()
    {
        using var provider = Services();
        using var scope = provider.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<AppBlocksValidator>();
        var first = Guid.NewGuid(); var second = Guid.NewGuid(); var columns = Guid.NewGuid();
        var page = Page([Block("appColumns", columns), Block("appReport", first, ("dataSource", ReportSource)), Block("appReport", second, ("dataSource", ReportSource))],
            [Item(columns, Area(Item(first)), Area(Item(second)))]);
        Assert.Empty(validator.Validate(page));
    }

    private static string Page(JsonNode[] contentData, JsonNode[] layout) => new JsonObject {
        ["contentData"] = new JsonArray(contentData),
        ["expose"] = new JsonArray(contentData.Select(block => (JsonNode)new JsonObject { ["contentKey"] = block!["key"]!.GetValue<string>(), ["culture"] = null, ["segment"] = null }).ToArray()),
        ["settingsData"] = new JsonArray(),
        ["layout"] = new JsonObject { ["Umbraco.BlockGrid"] = new JsonArray(layout) },
    }.ToJsonString();

    private static JsonNode Block(string alias, Guid key, params (string Alias, string Value)[] values) => new JsonObject {
        ["contentTypeKey"] = AppBlockSchema.Key(alias).ToString(), ["key"] = key.ToString(),
        ["values"] = new JsonArray(values.Select(v => (JsonNode)new JsonObject { ["alias"] = v.Alias, ["culture"] = null, ["editorAlias"] = null, ["segment"] = null, ["value"] = v.Value }).ToArray()),
    };

    private static JsonNode Item(Guid key, params JsonNode[] areas) => new JsonObject {
        ["areas"] = new JsonArray(areas), ["columnSpan"] = 12, ["contentKey"] = key.ToString(), ["rowSpan"] = 1, ["settingsKey"] = null,
    };

    private static JsonNode Area(params JsonNode[] items) => new JsonObject { ["key"] = Guid.NewGuid().ToString(), ["items"] = new JsonArray(items) };

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddInteractiveReports(configuration).AddAppBlocksConnections(services, connections => connections.AddConnection("sample", _ => new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:")));
        services.Configure<InteractiveReportOptions>(options => options.Reports["orders"] = new ReportDefinition {
            Connection = "sample", Dialect = ReportDialect.Sqlite, Sql = "SELECT 1 AS ORDER_ID", DefaultPageSize = 25, MaxPageSize = 100,
        });
        services.AddSingleton<IWebHostEnvironment>(new TestEnvironment());
        services.AddScoped<IFormResource, SampleOrdersResource>();
        services.AddScoped<FormResourceRegistry>();
        services.AddScoped<AppBlocksValidator>();
        return services.AddReportBlockSupport().BuildServiceProvider();
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Page level tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = Directory.CreateTempSubdirectory("app-page-level-test-").FullName;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
