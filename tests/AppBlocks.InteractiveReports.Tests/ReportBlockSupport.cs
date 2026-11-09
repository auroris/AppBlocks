using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using AppBlocks.Blocks;
using AppBlocks.Connections;
using AppBlocks.DataSources;
using AppBlocks.InteractiveReports.DataSources;
using AppBlocks.Tests;
using InteractiveReport.AspNetCore;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AppBlocks.InteractiveReports.Tests;

/// <summary>Publishing's view of the report block without a site: the report engine, the block's plugin, and pages that hold reports.</summary>
internal static class ReportBlockSupport
{
    public static IServiceCollection AddReportBlockSupport(this IServiceCollection services)
    {
        services.TryAddSingleton<IApplicationConnectionFactory, ReportConnections>();
        services.TryAddSingleton<DataSourceRuntime>();
        services.TryAddScoped<IAppBlockPlugin>(sp =>
        {
            var compiler = sp.GetRequiredService<ReportDefinitionCompiler>();
            var configuration = sp.GetRequiredService<IConfiguration>();
            // A block's source names a listed connection; only what the fixture registered with AppBlocks is listed.
            var connections = new ConnectionCatalog(configuration, sp.GetRequiredService<IWebHostEnvironment>(), new EphemeralDataProtectionProvider(),
                sp.GetService<AppBlocksBuilder>(), sp);
            // A block's own source needs only the connections, the compiler and the runtime; the rest read published content.
            var catalog = new PublishedReportCatalog(null!, null!, null!, null!, sp.GetRequiredService<IOptionsMonitor<InteractiveReportOptions>>(),
                configuration, sp.GetRequiredService<DataSourceRuntime>(), compiler, null!, NullLogger<PublishedReportCatalog>.Instance, connections);
            return new InteractiveReportBlock(compiler, catalog, null!, connections);
        });
        return services;
    }

    /// <summary>Registers connections with AppBlocks as a host does; the report engine gets them as the companion passes them on.</summary>
    public static InteractiveReportBuilder AddAppBlocksConnections(this InteractiveReportBuilder reports, IServiceCollection services, Action<AppBlocksBuilder> configure)
    {
        var connections = new AppBlocksBuilder();
        configure(connections);
        services.AddSingleton(connections);
        return reports.AddConnectionsFrom(connections);
    }

    /// <summary>A temporary database with the orders table the report blocks of <see cref="OrdersReport"/> read, as a connection string.</summary>
    public static string OrdersDatabase()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("app-report-test-").FullName, "orders.db");
        var connectionString = $"Data Source={file};Pooling=False";
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE ORDERS (ORDER_ID INTEGER PRIMARY KEY, CUSTOMER TEXT NOT NULL, VERSION INTEGER NOT NULL DEFAULT 1);";
        command.ExecuteNonQuery();
        return connectionString;
    }

    /// <summary>A page holding one report over the orders table of connection <paramref name="connection"/>, which links to a form page and a paper report.</summary>
    public static JsonNode OrdersReport(string connection = "sample")
    {
        var page = BlockPages.Empty();
        BlockPages.Layout(page).Add(BlockPages.Add(page, "appReport",
            ("dataSource", JsonSerializer.Serialize(new { kind = "table", connection, source = "ORDERS" })), ("heading", "Orders"), ("pageSize", "25"),
            ("formPage", "umb://document/33796329b17db566849c03401489c175"), ("keyColumn", "ORDER_ID"), ("keyParameter", "id"),
            ("createLabel", "New order"), ("editLabel", "Edit order"), ("paperReport", "umb://document/02e367e257647de6456377cf40dff99d")));
        return page;
    }

    /// <summary>Public text beside a region holding a form and a report, and a second, independent form.</summary>
    public static JsonNode InteractivePage()
    {
        var content = BlockPages.Empty();
        var first = BlockPages.Merge(content, BlockPages.OrdersForm())[0];
        var second = BlockPages.OrdersForm();
        var text = second.ToJsonString();
        foreach (var key in second["contentData"]!.AsArray().Select(block => block!["key"]!.GetValue<string>())) text = text.Replace(key, Guid.NewGuid().ToString(), StringComparison.Ordinal);
        second = JsonNode.Parse(text)!;
        var configuration = BlockPages.Value(BlockPages.Blocks(second, "appForm").Single(), "formInterface")!["contentData"]![0]!;
        BlockPages.SetValue(configuration, "keyParameter", "otherOrder");
        var secondLayout = BlockPages.Merge(content, second)[0];
        var report = BlockPages.Merge(content, OrdersReport())[0];
        var publicContent = BlockPages.Add(content, AppBlockSchema.HtmlAlias, ("html", "<p>public-introduction-marker</p>"));
        var region = BlockPages.Wrap(content, AppBlockSchema.RegionAlias, [first, report]);
        BlockPages.Layout(content).Add(BlockPages.Wrap(content, "appColumns", [publicContent], [region, secondLayout]));
        return content;
    }

    private sealed class ReportConnections(IReportConnectionFactory reports, ReportDefinitionCompiler compiler) : IApplicationConnectionFactory
    {
        public DbConnection CreateConnection(string name) => reports.CreateConnection(name);
        public DatabaseDialect Dialect(string name) => Enum.Parse<DatabaseDialect>(compiler.Compile("data-source",
            new ReportDefinition { Connection = name, Sql = "SELECT 1" }).Dialect!.Value.ToString());
        public void SetDetectedDialect(string name, DatabaseDialect dialect) => reports.SetDetectedDialect(name, Enum.Parse<ReportDialect>(dialect.ToString()));
    }
}
