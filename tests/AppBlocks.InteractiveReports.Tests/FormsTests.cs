using System.Security.Claims;
using System.Text.Json.Nodes;
using InteractiveReport.AspNetCore;
using InteractiveReport.Core.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using AppBlocks.Blocks;
using AppBlocks.DataSources;
using AppBlocks.Forms;
using AppBlocks.Tests;

namespace AppBlocks.InteractiveReports.Tests;

public sealed class FormsTests
{
    [Fact]
    public async Task Publishing_rejects_duplicate_unknown_and_incompatible_bindings_and_bad_report_settings()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var environment = new TestEnvironment();
        var configuration = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(configuration);
        var orders = ReportBlockSupport.OrdersDatabase();
        services.AddInteractiveReports(configuration)
            .AddAppBlocksConnections(services, connections => connections.AddConnection("sample", _ => new SqliteConnection(orders)));
        services.Configure<InteractiveReportOptions>(o => o.Reports["orders"] = new ReportDefinition { Connection = "sample", Dialect = ReportDialect.Sqlite, Sql = "SELECT 1 AS ORDER_ID", DefaultPageSize = 25, MaxPageSize = 100 });
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddScoped<IFormResource, SampleOrdersResource>(); services.AddScoped<FormResourceRegistry>();
        // The form page binds the C# class; an Orders data source is published beside it.
        services.AddSingleton<DataSourceRuntime>();
        services.AddScoped<IFormResourceProvider>(sp => new FixedResources(
            new SqlDataSource(OrdersDataSource(), sp.GetRequiredService<DataSourceRuntime>(), new NoUsers(), [])));
        services.AddScoped<AppBlocksValidator>();
        using var provider = services.AddReportBlockSupport().BuildServiceProvider(); using var scope = provider.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<AppBlocksValidator>();
        await validator.LoadAsync();
        Assert.Empty(validator.Validate(BlockPages.OrdersForm().ToJsonString()));
        Assert.Empty(validator.Validate(ReportBlockSupport.OrdersReport().ToJsonString()));
        var form = BlockPages.OrdersForm();
        form["contentData"]![0]!["values"]![0]!["value"] = "region";
        Assert.Contains(validator.Validate(form.ToJsonString()), e => e.Contains("more than once"));
        form["contentData"]![0]!["values"]![0]!["value"] = "notAField";
        Assert.Contains(validator.Validate(form.ToJsonString()), e => e.Contains("not a field"));
        form["contentData"]![0]!["values"]![0]!["value"] = "amount";
        Assert.Contains(validator.Validate(form.ToJsonString()), e => e.Contains("does not match"));
        var report = ReportBlockSupport.OrdersReport();
        report["contentData"]![0]!["values"]!.AsArray().First(p => p!["alias"]!.GetValue<string>() == "pageSize")!["value"] = 5000;
        Assert.Contains(validator.Validate(report.ToJsonString()), e => e.Contains("cannot be larger than"));
        Assert.NotEmpty(validator.Validate("{bad"));
        // A form binds a registered C# class: neither an unknown name nor a published data source will do.
        foreach (var resource in new[] { "no-such-source", "orders" })
            Assert.Contains(validator.Validate(BlockPages.OrdersForm(resource).ToJsonString()),
                e => e == "Select a registered C# form class.");
    }

    private static DataSourceDefinition OrdersDataSource()
    {
        var values = new Dictionary<string, object?> {
            ["alias"] = "orders", ["access"] = "Public", ["connection"] = "sample", ["source"] = "ORDERS", ["keyColumns"] = "ORDER_ID", ["concurrency"] = "VERSION",
            ["allowInsert"] = "1", ["allowUpdate"] = "1", ["allowDelete"] = "1",
        };
        var errors = new List<string>();
        var definition = DataSourceDefinition.Read(Guid.NewGuid(), "Orders", alias => values.GetValueOrDefault(alias), _ => [], errors);
        Assert.True(definition is not null, string.Join(" ", errors));
        return definition;
    }

    private sealed class FixedResources(params IFormResource[] resources) : IFormResourceProvider
    {
        public IEnumerable<IFormResource> Resources => resources;
    }

    private sealed class NoUsers : IDataSourceUserResolver
    {
        public Task<DataSourceUser?> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct) => Task.FromResult<DataSourceUser?>(null);
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Form tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = Directory.CreateTempSubdirectory("app-form-test-").FullName;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
