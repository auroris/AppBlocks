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
        Access = DataSourceAccess.SpecificGroups,
    };
    private static FormExecutionContext Context => new(new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "42")], IdentityConstants.ApplicationScheme)), Guid.NewGuid(), Guid.NewGuid(), "1");

    [Fact]
    public async Task External_queries_keep_restrictions_and_request_values_bound_without_exposing_the_plan()
    {
        var definition = Definition with { Source = "SELECT ID, LABEL, OWNER FROM CHOICES WHERE LABEL = :PARAM.label" };
        var query = Assert.IsType<DataSourceReadQuery>(runtime.PrepareQuery(definition, "{{Access}}"));
        Assert.True(query.BindsUser);
        Assert.Equal(["ID", "LABEL", "OWNER"], query.Columns);
        Assert.Contains(query.FieldColumns, column => column.Column == "LABEL" && column.Field == "label" && column.Kind == "text");
        Assert.All(query.Preview().Parameters.Values, value => Assert.Null(value));

        await new DataSourceQueries(runtime, new Users(User), []).AuthorizeAsync(definition, Context, default);
        await using var connection = new SqliteConnection($"Data Source={file};Pooling=False");
        await connection.OpenAsync();
        async Task<int> Count(string label)
        {
            var bound = query.Bind(User, new Dictionary<string, string> { ["label"] = label });
            Assert.DoesNotContain("owner-sid", bound.Sql);
            Assert.Contains("{{Access}}", bound.Sql);
            await using var command = connection.CreateCommand();
            command.CommandText = bound.Sql.Replace("{{Access}}", "1=1");
            foreach (var parameter in bound.Parameters) command.Parameters.AddWithValue(parameter.Key, parameter.Value ?? DBNull.Value);
            await using var rows = await command.ExecuteReaderAsync();
            var count = 0;
            while (await rows.ReadAsync()) count++;
            return count;
        }
        Assert.Equal(1, await Count("One"));
        Assert.Equal(0, await Count("Two")); // Another owner's row stays excluded.
        Assert.Equal(0, await Count("One' OR 1=1 --"));
    }

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
        var backOffice = User with { IsBackOfficeUser = true, UserGroupAliases = ["admin"] };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new DataSourceQueries(runtime, new Users(backOffice), []).QueryAsync(Definition, Context));
        var policy = new DenyQuery();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new DataSourceQueries(runtime, new Users(User), [policy]).QueryAsync(Definition, Context));
        Assert.Equal(DataSourceAction.Query, policy.Seen);
        // The query's own connection, open, so a policy can read other rows; a query has no transaction.
        Assert.Equal((ConnectionState.Open, false), policy.Connection);
    }

    [Fact]
    public async Task Public_queries_share_access_rules_restrictions_and_anonymous_policy_context()
    {
        var source = Definition with { Access = DataSourceAccess.Public, RowRestriction = "ID <> 2" };
        var context = Context with { Member = new ClaimsPrincipal(new ClaimsIdentity()) };
        var queries = new DataSourceQueries(runtime, new Users(null), []);
        Assert.True(queries.AllowsAnonymousRead(source));
        Assert.Equal(2, (await queries.QueryAsync(source, context)).Rows.Count);
        await Assert.ThrowsAsync<DataSourceException>(() => queries.QueryAsync(source with { Access = null, ReadGroups = [] }, context));
        await Assert.ThrowsAsync<DataSourceException>(() => queries.QueryAsync(source with { Access = null }, context));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => queries.QueryAsync(source with { Access = DataSourceAccess.NotPublic }, context));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => queries.QueryAsync(source with { Access = DataSourceAccess.SpecificGroups }, context));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => queries.QueryAsync(source with { RowRestriction = ":USER_NAME IS NULL" }, context));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => queries.QueryAsync(source with { Source = "SELECT * FROM CHOICES WHERE :USER_KEY IS NULL" }, context));
        // A principal that asserted an account but could not resolve it cannot fall back to public access.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => queries.QueryAsync(source, Context));
        var policy = new DenyQuery();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new DataSourceQueries(runtime, new Users(null), [policy]).QueryAsync(source, context));
        Assert.Equal(DataSourceAction.Query, policy.Seen);
        Assert.True(policy.User!.IsAnonymous);
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

    [Fact]
    public async Task Application_variables_come_from_registered_providers_so_one_source_serves_every_tenant()
    {
        var context = Context;
        var provider = new Provider(context.PageKey, new Dictionary<string, object?> { ["tenant"] = "owner-sid", ["Count"] = 3, ["text"] = "3" });
        var queries = new DataSourceQueries(runtime, new Users(User), [], new BindVariableProviders([provider]));
        var source = Definition with { Source = "SELECT ID, LABEL, OWNER FROM CHOICES WHERE OWNER = :APP.tenant" };
        Assert.Equal(2, (await queries.QueryAsync(source, context)).Rows.Count);
        // The provider saw this operation's context; a name it did not answer binds NULL, which matches nothing; another page is another answer.
        Assert.Equal(context.PageKey, Assert.Single(provider.Seen).PageKey);
        Assert.Empty((await queries.QueryAsync(source with { Source = "SELECT ID, LABEL, OWNER FROM CHOICES WHERE OWNER = :APP.region" }, context)).Rows);
        Assert.Empty((await queries.QueryAsync(source, context with { PageKey = Guid.NewGuid() })).Rows);
        // A number binds as a number and text as text; row 3 is the only one of the owner\x27s with that id, since the definition restricts rows to the owner.
        foreach (var name in new[] { "count", "text" }) Assert.Single((await queries.QueryAsync(source with { Source = "SELECT ID, LABEL, OWNER FROM CHOICES WHERE ID = :APP." + name }, context)).Rows);
        // Without a provider, as in a host that registers none, the variables bind NULL rather than failing.
        Assert.Empty((await new DataSourceQueries(runtime, new Users(User), []).QueryAsync(source, context)).Rows);
    }

    private sealed class Provider(Guid page, IReadOnlyDictionary<string, object?> values) : IBindVariableProvider
    {
        public List<FormExecutionContext> Seen { get; } = [];
        public ValueTask<IReadOnlyDictionary<string, object?>?> ResolveAsync(FormExecutionContext context, CancellationToken ct)
        {
            if (context.PageKey == page) Seen.Add(context);
            return ValueTask.FromResult<IReadOnlyDictionary<string, object?>?>(context.PageKey == page ? values : null);
        }
    }
    private sealed class Users(DataSourceUser? user) : IDataSourceUserResolver
    {
        public Task<DataSourceUser?> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct) => Task.FromResult(user);
    }
    private sealed class DenyQuery : IDataSourcePolicy
    {
        public DataSourceUser? User { get; private set; }
        public DataSourceAction? Seen { get; private set; }
        public (ConnectionState? State, bool Transaction) Connection { get; private set; }
        public ValueTask<bool> IsAllowedAsync(DataSourceRequest request, CancellationToken ct)
        {
            Seen = request.Action;
            User = request.User;
            Connection = (request.Connection?.State, request.Transaction is not null);
            return ValueTask.FromResult(false);
        }
    }
    public void Dispose() => services.Dispose();
}
