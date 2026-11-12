using System.Data.Common;
using System.Security.Claims;
using AppBlocks.Api;
using AppBlocks.Connections;
using AppBlocks.DataSources;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppBlocks.Tests;

public sealed class ApiEndpointRunnerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_declared_query_parameters_reach_the_handler_and_route_values_win(bool declareQuery)
    {
        var handler = new CaptureParameters();
        var runtime = new DataSourceRuntime(new Connections(), NullLogger<DataSourceRuntime>.Instance);
        var queries = new DataSourceQueries(runtime, new AnonymousUsers(), [], handlers: new DataSourceHandlers([handler]));
        var source = new DataSourceDefinition {
            Key = Guid.NewGuid(), Name = "Example", Alias = "example", Connection = "db", Source = "SELECT 1 AS Id",
            Access = DataSourceAccess.Public, Handler = handler.Alias,
        };
        var errors = new List<string>();
        var route = ApiRouteTemplate.Parse("example/:id", errors)!;
        var endpoint = new ApiEndpointDefinition(Guid.NewGuid(), "example", "Example", route, source, true,
            ApiEndpointShape.Collection, 10, [], declareQuery ? ["filter", "id"] : []);
        var runner = new ApiEndpointRunner(queries, NullLogger<ApiEndpointRunner>.Instance);
        var result = await runner.RunAsync(endpoint, new ClaimsPrincipal(new ClaimsIdentity()),
            new Dictionary<string, string> { ["id"] = "route-value" },
            new Dictionary<string, string> { ["FILTER"] = "allowed", ["ID"] = "query-value", ["undeclared"] = "refused" }, default);

        Assert.Equal(200, result.Status);
        Assert.NotNull(handler.Parameters);
        Assert.Equal("route-value", handler.Parameters["id"]);
        Assert.False(handler.Parameters.ContainsKey("undeclared"));
        Assert.Equal(declareQuery ? 2 : 1, handler.Parameters.Count);
        if (declareQuery) Assert.Equal("allowed", handler.Parameters["filter"]);
    }

    private sealed class CaptureParameters : IDataSourceHandler
    {
        public string Alias => "capture";
        public IReadOnlyDictionary<string, string>? Parameters { get; private set; }
        public ValueTask<DataSourceOutcome> QueryAsync(DataSourceOperation operation, CancellationToken ct)
        {
            Parameters = operation.Context.Parameters;
            return ValueTask.FromResult(DataSourceOutcome.Continue);
        }
    }

    private sealed class AnonymousUsers : IDataSourceUserResolver
    {
        public Task<DataSourceUser?> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct) => Task.FromResult<DataSourceUser?>(null);
    }

    private sealed class Connections : IApplicationConnectionFactory
    {
        public DbConnection CreateConnection(string name) => new SqliteConnection("Data Source=:memory:");
        public DatabaseDialect Dialect(string name) => DatabaseDialect.Sqlite;
        public void SetDetectedDialect(string name, DatabaseDialect dialect) { }
    }
}
