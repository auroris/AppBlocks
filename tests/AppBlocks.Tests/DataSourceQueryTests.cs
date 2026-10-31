using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AppBlocks.DataSources;
using AppBlocks.Forms;

namespace AppBlocks.Tests;

public sealed class DataSourceQueryTests : IDisposable
{
    private readonly string file = Path.Combine(Directory.CreateTempSubdirectory("query-sources-").FullName, "rows.db");
    private readonly ServiceProvider services;
    private readonly DataSourceRuntime runtime;
    private static readonly DataSourceUser User = new("42", "owner-sid", "pilot", "pilot@example.invalid", ["Pilots"], false);

    public DataSourceQueryTests()
    {
        using var connection = new SqliteConnection($"Data Source={file};Pooling=False"); connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE CHOICES (ID INTEGER PRIMARY KEY, LABEL TEXT NOT NULL, OWNER TEXT NOT NULL); INSERT INTO CHOICES VALUES (1, 'One', 'owner-sid'), (2, 'Two', 'someone-else'), (3, 'Three', 'owner-sid');";
        command.ExecuteNonQuery();
        var configuration = new ConfigurationBuilder().Build(); var collection = new ServiceCollection();
        collection.AddLogging(); collection.AddSingleton<IConfiguration>(configuration);
        collection.AddTestConnections(connections => connections.AddConnection("QueryDb", _ => new SqliteConnection($"Data Source={file};Pooling=False")));
        collection.AddSingleton<DataSourceRuntime>(); services = collection.BuildServiceProvider(); runtime = services.GetRequiredService<DataSourceRuntime>();
    }

    private static DataSourceDefinition Definition => new() {
        Key = Guid.NewGuid(), Alias = "choices", Name = "Choices", Connection = "QueryDb", Source = "CHOICES",
        RowRestriction = "OWNER = :USER_SID", ReadGroups = ["Pilots"],
    };
    private static FormExecutionContext Context => new(new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "42")], IdentityConstants.ApplicationScheme)), Guid.NewGuid(), Guid.NewGuid(), "1");

    [Fact]
    public async Task Lists_and_paper_reports_read_many_rows_with_identity_restrictions_and_column_metadata()
    {
        var result = await new DataSourceQueries(runtime, new Users(User), []).QueryAsync(Definition, Context);
        Assert.Equal(2, result.Rows.Count);
        Assert.All(result.Rows, row => Assert.Equal("owner-sid", row["OWNER"]));
        Assert.Contains(result.Columns, column => column.Name == "ID" && column.Field == "id" && column.Kind == "number");
    }

    [Fact]
    public async Task Read_groups_and_custom_policies_are_checked_before_returning_any_rows()
    {
        var denied = new DataSourceQueries(runtime, new Users(User with { Groups = ["Visitors"] }), []);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => denied.QueryAsync(Definition, Context));
        var policy = new DenyQuery();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new DataSourceQueries(runtime, new Users(User), [policy]).QueryAsync(Definition, Context));
        Assert.Equal(DataSourceAction.Query, policy.Seen);
        // The query's own connection, open, so a policy can read other rows; a query has no transaction.
        Assert.Equal((ConnectionState.Open, false), policy.Connection);
    }

    [Fact]
    public async Task Query_parameters_are_bound_and_oversized_lists_do_not_silently_truncate()
    {
        var queries = new DataSourceQueries(runtime, new Users(User), []);
        await Assert.ThrowsAsync<DataSourceException>(() => queries.QueryAsync(Definition, Context, maximumRows: 1));
        var source = Definition with { Source = "SELECT ID, LABEL, OWNER FROM CHOICES WHERE LABEL = :PARAM.label" };
        var result = await queries.QueryAsync(source, Context with { Parameters = new Dictionary<string, string> { ["label"] = "One' OR 1=1 --" } });
        Assert.Empty(result.Rows);
        var found = await queries.QueryAsync(source, Context with { Parameters = new Dictionary<string, string> { ["label"] = "One" } });
        Assert.Equal("One", Assert.Single(found.Rows)["LABEL"]);
    }

    private sealed class Users(DataSourceUser? user) : IDataSourceUserResolver
    {
        public Task<DataSourceUser?> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct) => Task.FromResult(user);
    }
    private sealed class DenyQuery : IDataSourcePolicy
    {
        public DataSourceAction? Seen { get; private set; }
        public (ConnectionState? State, bool Transaction) Connection { get; private set; }
        public ValueTask<bool> IsAllowedAsync(DataSourceRequest request, CancellationToken ct)
        {
            Seen = request.Action;
            Connection = (request.Connection?.State, request.Transaction is not null);
            return ValueTask.FromResult(false);
        }
    }
    public void Dispose() => services.Dispose();
}
