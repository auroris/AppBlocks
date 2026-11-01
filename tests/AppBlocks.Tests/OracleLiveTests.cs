using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using AppBlocks.Connections;
using AppBlocks.DataSources;
using AppBlocks.Forms;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Oracle.ManagedDataAccess.Client;

namespace AppBlocks.Tests;

/// <summary>Opt-in tests against Oracle 11.2. Creates and removes uniquely named objects in the supplied test schema.</summary>
public sealed class OracleLiveTests(ITestOutputHelper output)
{
    [Theory(Explicit = true)]
    [InlineData("oracle11g")]
    [InlineData("oracle")]
    public async Task Connections_queries_and_form_writes_work_against_Oracle_11g(string providerName)
    {
        var supplied = Environment.GetEnvironmentVariable("APPBLOCKS_ORACLE_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(supplied), "Set APPBLOCKS_ORACLE_CONNECTION to a disposable Oracle 11.2 schema with CREATE TABLE, CREATE VIEW, CREATE SEQUENCE and a tablespace quota.");
        var connectionString = new OracleConnectionStringBuilder(supplied) { Pooling = false, ConnectionTimeout = 10 }.ConnectionString;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["ConnectionStrings:OracleLive"] = connectionString,
            ["ConnectionStrings:OracleLive_ProviderName"] = providerName,
        }).Build();
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<IConfiguration>(configuration);
        collection.AddTestConnections(_ => { });
        collection.AddSingleton<DataSourceRuntime>();
        using var services = collection.BuildServiceProvider();
        var catalog = services.GetRequiredService<ConnectionCatalog>();
        var runtime = services.GetRequiredService<DataSourceRuntime>();
        var connections = services.GetRequiredService<IApplicationConnectionFactory>();
        await catalog.TestAsync(new("OracleLive", providerName, null), TestContext.Current.CancellationToken);
        await using var database = connections.CreateConnection("OracleLive");
        await database.OpenAsync(TestContext.Current.CancellationToken);
        output.WriteLine($"Server {database.ServerVersion}; configured mode {providerName}; driver {database.GetType().Assembly.GetName().Version}");
        Assert.StartsWith("11.2.", database.ServerVersion);

        var suffix = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var table = "AB_ORA_T_" + suffix;
        var sequence = "AB_ORA_S_" + suffix;
        var view = "AB_ORA_V_" + suffix;
        var createdObjects = new List<(string Kind, string Name)>();
        try
        {
            await Execute(database, $"CREATE SEQUENCE {sequence} START WITH 1 INCREMENT BY 1 NOCACHE");
            createdObjects.Add(("SEQUENCE", sequence));
            await Execute(database, $"""
                CREATE TABLE {table} (
                    ID NUMBER(10) PRIMARY KEY,
                    TITLE VARCHAR2(100 CHAR) NOT NULL,
                    OWNER_NAME VARCHAR2(100 CHAR) NOT NULL,
                    AMOUNT NUMBER(12,2) DEFAULT 0 NOT NULL,
                    DONE NUMBER(1) DEFAULT 0 NOT NULL,
                    DUE_DATE DATE NULL,
                    VERSION NUMBER(10) DEFAULT 1 NOT NULL,
                    UPDATED_BY VARCHAR2(100 CHAR) NULL,
                    UPDATED_AT TIMESTAMP NULL,
                    TOTAL GENERATED ALWAYS AS (AMOUNT * 2) VIRTUAL)
                """);
            createdObjects.Add(("TABLE", table));
            await Execute(database, $"CREATE VIEW {view} AS SELECT ID, TITLE, OWNER_NAME FROM {table}");
            createdObjects.Add(("VIEW", view));

            var tables = await new ConnectionTables(catalog).ListAsync("OracleLive", default);
            Assert.Contains(tables, item => item.Name.EndsWith("." + table, StringComparison.Ordinal));
            Assert.Contains(tables, item => item.Name.EndsWith("." + view, StringComparison.Ordinal));
            var definition = Definition(new() {
                ["alias"] = "oracleLive", ["connection"] = "OracleLive", ["source"] = table, ["keyColumns"] = "ID",
                ["keySequences"] = JsonSerializer.Serialize(new Dictionary<string, string> { ["ID"] = sequence }),
                ["allowInsert"] = "1", ["allowUpdate"] = "1", ["allowDelete"] = "1", ["concurrency"] = "VERSION",
                ["rowRestriction"] = "OWNER_NAME = :USER_NAME", ["insertDefaults"] = "OWNER_NAME = :USER_NAME",
                ["mapping"] = """{"done":{"kind":"checkbox"},"dueDate":{"kind":"date"}}""",
                ["auditColumns"] = "updatedBy = UPDATED_BY\nupdatedAt = UPDATED_AT",
            });
            var validation = runtime.Validate(definition);
            Assert.True(validation.Count == 0, string.Join(Environment.NewLine, validation));
            Assert.Equal(DatabaseDialect.Oracle11g, connections.Dialect("OracleLive"));
            var user = new DataSourceUser("oracle-test", null, "owner", null, [], false);
            var users = new Users(user);
            var context = new FormExecutionContext(
                new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, user.Key)], IdentityConstants.ApplicationScheme)),
                Guid.NewGuid(), Guid.NewGuid(), "1", Guid.NewGuid().ToString());
            var source = new SqlDataSource(definition, runtime, users, []);
            Assert.Null(source.Problem);
            var fields = source.Fields.ToDictionary(field => field.Key);
            Assert.True(fields["id"].ReadOnly);
            Assert.True(fields["total"].ReadOnly);
            Assert.Equal(100, fields["title"].MaxLength);
            Assert.Equal("checkbox", fields["done"].Kind);
            Assert.Equal("date", fields["dueDate"].Kind);

            var created = await source.CreateAsync(JsonSerializer.SerializeToElement(new {
                title = "Oracle live test", amount = "12.34", done = true, dueDate = "2026-12-24",
            }), context, default);
            Assert.True(created.Status == 200, JsonSerializer.Serialize(created));
            Assert.Equal("1", created.Record!.Key);
            Assert.Equal("24.68", created.Record.Values["total"]);
            Assert.Equal(true, created.Record.Values["done"]);
            Assert.Equal("2026-12-24", created.Record.Values["dueDate"]);
            Assert.Equal("1", created.Record.Version);
            Assert.Equal(200, (await source.LoadAsync("1", context, default)).Status);

            var updated = await source.UpdateAsync("1", JsonSerializer.SerializeToElement(new {
                title = "Updated through AppBlocks", amount = "23.45", done = false,
            }), created.Record.Version!, context, default);
            Assert.True(updated.Status == 200, JsonSerializer.Serialize(updated));
            Assert.Equal("2", updated.Record!.Version);
            Assert.Equal("46.9", updated.Record.Values["total"]);
            Assert.Equal(false, updated.Record.Values["done"]);
            Assert.Equal("owner", await Scalar(database, $"SELECT UPDATED_BY FROM {table} WHERE ID = 1"));
            Assert.NotNull(await Scalar(database, $"SELECT UPDATED_AT FROM {table} WHERE ID = 1"));
            Assert.Equal(409, (await source.UpdateAsync("1", JsonSerializer.SerializeToElement(new { title = "Stale" }), "1", context, default)).Status);
            Assert.Equal(409, (await source.DeleteAsync("1", "1", context, default)).Status);

            await Execute(database, $"INSERT INTO {table} (ID, TITLE, OWNER_NAME) VALUES ({sequence}.NEXTVAL, 'Other owner', 'someone-else')");
            Assert.Equal(404, (await source.LoadAsync("2", context, default)).Status);
            var denied = await source.CreateAsync(JsonSerializer.SerializeToElement(new { title = "Denied", ownerName = "someone-else" }),
                context with { SubmissionKey = Guid.NewGuid().ToString() }, default);
            Assert.Equal(403, denied.Status);
            Assert.Equal(2m, Convert.ToDecimal(await Scalar(database, $"SELECT COUNT(*) FROM {table}")));

            var preview = new DataSourcePreview(runtime);
            var sample = await preview.TestAsync(definition, user, new Dictionary<string, JsonElement>(), default);
            Assert.Empty(sample.Errors);
            Assert.Equal("Updated through AppBlocks", sample.Row!["TITLE"]);
            var injection = await preview.TestAsync(definition, user,
                new Dictionary<string, JsonElement> { ["USER_NAME"] = JsonSerializer.SerializeToElement("' OR 1=1 --") }, default);
            Assert.Empty(injection.Errors);
            Assert.Null(injection.Row);

            var queries = new DataSourceQueries(runtime, users, []);
            var rows = await queries.QueryAsync(definition, context);
            Assert.Equal("Updated through AppBlocks", Assert.Single(rows.Rows)["TITLE"]);
            var queryDefinition = definition with {
                Key = Guid.NewGuid(), Source = $"SELECT ID, TITLE, OWNER_NAME FROM {view} WHERE TITLE = :PARAM.title",
                AllowInsert = false, AllowUpdate = false, AllowDelete = false, VersionColumn = null,
                KeySequences = new Dictionary<string, string>(), Mapping = [], InsertDefaults = [], AuditColumns = new(),
            };
            var queried = await queries.QueryAsync(queryDefinition, context with {
                Parameters = new Dictionary<string, string> { ["title"] = "Updated through AppBlocks" },
            });
            Assert.Single(queried.Rows);
            Assert.Empty((await queries.QueryAsync(queryDefinition, context with {
                Parameters = new Dictionary<string, string> { ["title"] = "' OR 1=1 --" },
            })).Rows);

            var valid = definition with { CustomStatements = new(Update: $"UPDATE {table} SET TITLE = :FIELD.title WHERE ID = :FIELD.id") };
            var customValidation = runtime.Validate(valid);
            Assert.True(customValidation.Count == 0, string.Join(Environment.NewLine, customValidation));
            var invalid = valid with { CustomStatements = new(Update: $"UPDATE {table} SET MISSING_COLUMN = 1 WHERE ID = :FIELD.id") };
            Assert.Contains(runtime.Validate(invalid), error => error.Contains("does not compile", StringComparison.Ordinal));
            Assert.Equal("Updated through AppBlocks", await Scalar(database, $"SELECT TITLE FROM {table} WHERE ID = 1"));

            Assert.Equal(204, (await source.DeleteAsync("1", "2", context, default)).Status);
            Assert.Equal(404, (await source.LoadAsync("1", context, default)).Status);
            Assert.Equal(1m, Convert.ToDecimal(await Scalar(database, $"SELECT COUNT(*) FROM {table}")));
            output.WriteLine("Verified table/view discovery, 11g metadata, sequence keys, typed CRUD, concurrency, row restrictions, rollback, previews, parameterized queries and compile-only validation.");
        }
        finally
        {
            var failures = new List<Exception>();
            foreach (var (kind, name) in createdObjects.AsEnumerable().Reverse())
            {
                try { await Execute(database, $"DROP {kind} {name}" + (kind == "TABLE" ? " PURGE" : "")); }
                catch (Exception error) { failures.Add(error); }
            }
            if (failures.Count > 0) throw new AggregateException("Could not remove all Oracle live-test objects.", failures);
        }
    }

    private static DataSourceDefinition Definition(Dictionary<string, object?> values)
    {
        var errors = new List<string>();
        var definition = DataSourceDefinition.Read(Guid.NewGuid(), "Oracle live test", name => values.GetValueOrDefault(name), _ => [], errors);
        Assert.Empty(errors);
        return Assert.IsType<DataSourceDefinition>(definition);
    }

    private sealed class Users(DataSourceUser user) : IDataSourceUserResolver
    {
        public Task<DataSourceUser?> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct) => Task.FromResult<DataSourceUser?>(user);
    }

    private static async Task Execute(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 30;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> Scalar(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 30;
        var result = await command.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }
}
