using System.Text.Json.Nodes;
using InteractiveReport.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using AppBlocks.Blocks;
using AppBlocks.Forms;
using AppBlocks.Tests;

namespace AppBlocks.InteractiveReports.Tests;

public sealed class WebPageBlocksTests
{
    [Fact]
    public async Task Website_content_can_stand_alone_and_mix_independent_forms_with_nested_reports()
    {
        using var provider = Services();
        using var scope = provider.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<AppBlocksValidator>();
        await validator.LoadAsync();
        Assert.Empty(validator.ValidateWebsite(null));
        Assert.Empty(validator.ValidateWebsite(BlockPages.Empty().ToJsonString()));
        Assert.Empty(validator.ValidateWebsite(BlockPages.Static("Public content").ToJsonString()));
        var mixed = ReportBlockSupport.InteractivePage();
        Assert.Empty(validator.ValidateWebsite(mixed.ToJsonString()));
        Assert.Contains(validator.Validate(mixed.ToJsonString()), error => error.Contains("Use one form per page"));

        // The legacy page's block-count rule is not imposed on general content pages.
        var contentPage = BlockPages.Empty();
        for (var i = 0; i < 501; i++) BlockPages.Layout(contentPage).Add(BlockPages.Add(contentPage, AppBlockSchema.HtmlAlias, ("html", "<p>Content</p>")));
        Assert.Empty(validator.ValidateWebsite(contentPage.ToJsonString()));
    }

    [Fact]
    public async Task Fields_require_a_form_and_forms_still_validate_their_resource_and_nesting()
    {
        using var provider = Services();
        using var scope = provider.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<AppBlocksValidator>();
        await validator.LoadAsync();
        var standalone = BlockPages.Empty();
        BlockPages.Layout(standalone).Add(BlockPages.Add(standalone, "appText", ("field", "customer")));
        Assert.Contains(validator.ValidateWebsite(standalone.ToJsonString()), error => error.Contains("inside a form"));

        var mixed = ReportBlockSupport.InteractivePage();
        var form = BlockPages.Blocks(mixed, "appForm").First();
        var configuration = BlockPages.Value(form, "formInterface")!["contentData"]![0]!;
        BlockPages.SetValue(configuration, "resource", "missing-resource");
        Assert.Contains(validator.ValidateWebsite(mixed.ToJsonString()), error => error.Contains("Select a registered C# form class"));

        var nested = BlockPages.OrdersForm();
        var report = BlockPages.Merge(nested, ReportBlockSupport.OrdersReport())[0];
        BlockPages.Layout(nested)[0]!["areas"]![0]!["items"]!.AsArray().Add(report);
        Assert.Contains(validator.ValidateWebsite(nested.ToJsonString()), error => error.Contains("cannot be nested inside a form"));
    }

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(configuration);
        var orders = ReportBlockSupport.OrdersDatabase();
        services.AddInteractiveReports(configuration).AddAppBlocksConnections(services, connections => connections.AddConnection("sample", _ => new SqliteConnection(orders)));
        services.AddSingleton<IWebHostEnvironment>(new TestEnvironment());
        services.AddScoped<IFormResource, SampleOrdersResource>();
        services.AddScoped<FormResourceRegistry>();
        services.AddScoped<AppBlocksValidator>();
        return services.AddReportBlockSupport().BuildServiceProvider();
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Website block tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
