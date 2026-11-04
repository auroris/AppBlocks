using System.Data;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AppBlocks.DataSources;
using AppBlocks.Forms;

namespace AppBlocks.Tests;

/// <summary>The data source engine on SQLite, without Umbraco: every operation, the bind variables and publish validation.</summary>
public sealed class DataSourceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 14, 30, 0, TimeSpan.Zero);
    private static readonly DataSourceUser Owner = new("1001", "S-1-5-21-1", "jsmith", "jsmith@example.invalid", ["Planners"], false);
    private static readonly DataSourceUser Other = new("1002", "S-1-5-21-2", "kjones", "kjones@example.invalid", ["Crew"], false);
    private static readonly DataSourceUser Administrator = new("backoffice:admin", null, "admin", "admin@example.invalid", ["Administrators"], true) { UserGroupAliases = ["admin"] };
    private static readonly DataSourceUser Editor = new("backoffice:editor", null, "editor", "editor@example.invalid", ["Planners"], true) { UserGroupAliases = ["editor"] };
    private readonly string file = Path.Combine(Directory.CreateTempSubdirectory("data-source-test-").FullName, "tasks.db");
    private readonly ServiceProvider services;
    private readonly DataSourceRuntime runtime;

    public DataSourceTests()
    {
        Execute("""
            CREATE TABLE PROJECTS (PROJECT_ID INTEGER PRIMARY KEY, NAME TEXT NOT NULL);
            CREATE TABLE TASKS (
                TASK_ID INTEGER PRIMARY KEY,
                TITLE VARCHAR(100) NOT NULL,
                OWNER_SID TEXT NOT NULL,
                PRIORITY INTEGER NOT NULL DEFAULT 3,
                DUE_DATE TEXT NULL,
                DONE BOOLEAN NOT NULL DEFAULT 0,
                PROJECT_ID INTEGER NULL REFERENCES PROJECTS (PROJECT_ID),
                TAG TEXT NULL,
                VERSION INTEGER NOT NULL DEFAULT 1,
                CREATED_BY TEXT NULL, CREATED_AT TEXT NULL, UPDATED_BY TEXT NULL, UPDATED_AT TEXT NULL);
            CREATE TABLE TASK_LOG (ID INTEGER PRIMARY KEY, ENTRY TEXT NOT NULL);
            CREATE TABLE PAIRS (A INTEGER NOT NULL, B TEXT NOT NULL, LABEL TEXT NULL, PRIMARY KEY (A, B));
            CREATE TABLE LOOSE (ID INTEGER NOT NULL, LABEL TEXT NULL);
            CREATE VIEW OPEN_TASKS AS SELECT TASK_ID, TITLE FROM TASKS WHERE DONE = 0;
            INSERT INTO PROJECTS (PROJECT_ID, NAME) VALUES (1, 'Hangar'), (2, 'Runway');
            INSERT INTO TASKS (TITLE, OWNER_SID, PRIORITY, DUE_DATE, PROJECT_ID, TAG) VALUES
                ('Paint the hangar', 'S-1-5-21-1', 2, '2026-11-01', 1, 'Planners'),
                ('Sweep the runway', 'S-1-5-21-2', 1, '2026-11-02', 2, 'Crew'),
                ('Order cones', 'S-1-5-21-1', 3, NULL, NULL, 'Stores');
            INSERT INTO PAIRS (A, B, LABEL) VALUES (1, 'x', 'One x'), (1, 'y', 'One y');
            INSERT INTO LOOSE (ID, LABEL) VALUES (1, 'a'), (1, 'b');
            """);
        var configuration = new ConfigurationBuilder().Build();
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<IConfiguration>(configuration);
        collection.AddTestConnections(connections => connections.AddConnection("TasksDb", _ => new SqliteConnection($"Data Source={file};Pooling=False")));
        collection.AddSingleton<TimeProvider>(new FixedTime(Now));
        collection.AddSingleton<DataSourceRuntime>();
        services = collection.BuildServiceProvider();
        runtime = services.GetRequiredService<DataSourceRuntime>();
    }

    public void Dispose() => services.Dispose();

    [Fact]
    public async Task Authoring_test_reads_the_current_draft_with_parameterized_samples_and_never_writes()
    {
        var definition = Definition(Tasks(("rowRestriction", "OWNER_SID = :USER_SID")));
        var preview = new DataSourcePreview(runtime);
        var result = await preview.TestAsync(definition, Owner,
            new Dictionary<string, JsonElement> { ["USER_SID"] = JsonSerializer.SerializeToElement(Other.Sid) }, default);
        Assert.Empty(result.Errors);
        Assert.Contains(result.Columns, column => column.Column == "TITLE" && column.Field == "title" && column.Kind == "text");
        Assert.Equal("Sweep the runway", result.Row!["TITLE"]);
        var injection = await preview.TestAsync(definition, Owner,
            new Dictionary<string, JsonElement> { [":USER_SID"] = JsonSerializer.SerializeToElement("' OR 1=1 --") }, default);
        Assert.Empty(injection.Errors);
        Assert.Null(injection.Row);
        Assert.Equal(3L, Scalar("SELECT COUNT(*) FROM TASKS"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM TASK_LOG"));
    }

    [Fact]
    public async Task Authoring_test_reports_invalid_bind_values_and_draft_schema_errors()
    {
        var preview = new DataSourcePreview(runtime);
        var invalidSample = await preview.TestAsync(Definition(Tasks()), Owner,
            new Dictionary<string, JsonElement> { ["USER_GROUPS"] = JsonSerializer.SerializeToElement("Planners") }, default);
        Assert.Contains(invalidSample.Errors, error => error.Contains("array of group names"));
        var invalidColumn = await preview.TestAsync(Definition(Tasks(("mapping", "{\"title\":\"GONE\"}"))), Owner,
            new Dictionary<string, JsonElement>(), default);
        Assert.Contains(invalidColumn.Errors, error => error.Contains("GONE"));
        Assert.Null(invalidColumn.Row);
    }

    /// <summary>The tasks data source as an editor would write it on the node; settings replace single properties.</summary>
    private static Dictionary<string, object?> Tasks(params (string Alias, object? Value)[] settings)
    {
        var values = new Dictionary<string, object?> {
            ["access"] = "Public",
            ["alias"] = "tasks", ["connection"] = "TasksDb", ["source"] = "TASKS", ["keyColumns"] = "TASK_ID",
            ["allowInsert"] = "1", ["allowUpdate"] = "1", ["allowDelete"] = "1", ["concurrencyMode"] = DataSourceConcurrency.Checksum,
            ["mapping"] = """{ "dueDate": { "kind": "date" }, "done": { "kind": "checkbox" }, "createdAt": { "kind": "datetime" }, "updatedAt": { "kind": "datetime" } }""",
            ["auditColumns"] = "createdBy = CREATED_BY\ncreatedAt = CREATED_AT\nupdatedBy = UPDATED_BY\nupdatedAt = UPDATED_AT",
            ["insertDefaults"] = "OWNER_SID = :USER_SID\nPRIORITY = 2\nDUE_DATE = :NOW\nTAG = :PARAM.tag",
        };
        foreach (var (alias, value) in settings) values[alias] = value;
        return values;
    }

    private static DataSourceDefinition Definition(Dictionary<string, object?> values)
    {
        var errors = new List<string>();
        var definition = DataSourceDefinition.Read(Guid.NewGuid(), "Tasks", alias => values.GetValueOrDefault(alias), picked => picked.Split(','), errors);
        Assert.True(definition is not null, string.Join(" ", errors));
        return definition;
    }

    private SqlDataSource Source(Dictionary<string, object?> values, params IDataSourcePolicy[] policies)
        => new(Definition(values), runtime, new TestUsers(), policies);

    private SqlDataSource Working(Dictionary<string, object?> values, params IDataSourcePolicy[] policies)
    {
        var source = Source(values, policies);
        Assert.True(source.Problem is null, source.Problem);
        return source;
    }

    private static FormExecutionContext Context(DataSourceUser user, Dictionary<string, string>? parameters = null) => new(
        new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, user.Key)], IdentityConstants.ApplicationScheme)),
        Guid.NewGuid(), Guid.NewGuid(), "1", Guid.NewGuid().ToString(), parameters);

    private static FormExecutionContext AnonymousContext => Context(Owner) with { Member = new ClaimsPrincipal(new ClaimsIdentity()) };

    [Fact]
    public async Task Public_sources_allow_anonymous_reads_but_default_to_refusing_row_or_attachment_writes()
    {
        var seen = new List<DataSourceRequest>();
        var source = Working(Tasks(("access", "Public"), ("rowRestriction", "PRIORITY = 2")), new Policy(request => { seen.Add(request); return true; }));
        var context = AnonymousContext;
        var initial = await source.InitializeAsync(context, default);
        Assert.Equal(200, initial.Status);
        Assert.False(initial.Record!.Capabilities.Create);
        var loaded = await source.LoadAsync("1", context, default);
        Assert.Equal(200, loaded.Status);
        Assert.Equal("Paint the hangar", loaded.Record!.Values["title"]);
        Assert.False(loaded.Record.Capabilities.Update);
        Assert.False(loaded.Record.Capabilities.Delete);
        Assert.Equal(404, (await source.LoadAsync("2", context, default)).Status);
        var rows = await source.ListRowsAsync(new("projectId", "1"), context, 100, default);
        Assert.Equal(200, rows.Status);
        Assert.Single(rows.Rows!);
        Assert.False(rows.NewRow!.Capabilities.Create);
        Assert.All(seen, request => Assert.True(request.User.IsAnonymous));
        Assert.Contains(seen, request => request.Action == DataSourceAction.Query);
        Assert.Equal(403, (await source.CreateAsync(Json(new { title = "Anonymous" }), context, default)).Status);
        Assert.Equal(403, (await source.UpdateAsync("1", Json(new { title = "Anonymous" }), loaded.Record.Version!, context, default)).Status);
        Assert.Equal(403, (await source.DeleteAsync("1", loaded.Record.Version!, context, default)).Status);
        Assert.Equal(403, (await source.SaveRowsAsync(new("projectId", "1"), [new(RowOperation.Delete, "1", loaded.Record.Version, Json(new { }))], context, 100, default)).Status);
        Assert.False(await source.MayWriteAsync(context.Member, default));
        Assert.False(await ((AppBlocks.Attachments.IAttachmentOwner)source).MayWriteAttachmentsAsync(loaded.Record, context, default));
        Assert.Equal(3, Count());
    }

    [Fact]
    public async Task Anonymous_write_opt_in_reaches_CRUD_batches_and_policies_and_changes_the_contract()
    {
        var definition = Definition(Tasks(("insertDefaults", "OWNER_SID = visitor")));
        var optedIn = definition with { AllowAnonymousWrites = true };
        Assert.NotEqual(definition.ComputeFingerprint(), optedIn.ComputeFingerprint());
        Assert.True(Definition(Tasks(("allowAnonymousWrites", true), ("insertDefaults", "OWNER_SID = visitor"))).AllowAnonymousWrites);
        var seen = new List<DataSourceRequest>();
        var source = new SqlDataSource(optedIn, runtime, new TestUsers(), [new Policy(request => { seen.Add(request); return true; })]);
        var context = AnonymousContext;
        Assert.True((await source.InitializeAsync(context, default)).Record!.Capabilities.Create);
        Assert.True(await source.MayWriteAsync(context.Member, default));
        var created = await source.CreateAsync(Json(new { title = "Contact message" }), context, default);
        Assert.Equal(200, created.Status);
        Assert.Equal(4, Count());
        // Retrying the same submission still creates only one row.
        Assert.Equal(200, (await source.CreateAsync(Json(new { title = "Contact message" }), context, default)).Status);
        Assert.Equal(4, Count());
        var record = created.Record!;
        Assert.True(record.Capabilities.Update && record.Capabilities.Delete);
        Assert.True(await ((AppBlocks.Attachments.IAttachmentOwner)source).MayWriteAttachmentsAsync(record, context, default));
        var updated = await source.UpdateAsync(record.Key!, Json(new { title = "Edited" }), record.Version!, context, default);
        Assert.Equal(200, updated.Status);
        Assert.Equal(409, (await source.UpdateAsync(record.Key!, Json(new { title = "Stale" }), record.Version!, context, default)).Status);
        Assert.Equal(204, (await source.DeleteAsync(record.Key!, updated.Record!.Version!, context, default)).Status);
        var batch = await source.SaveRowsAsync(new("projectId", "1"), [new(RowOperation.Create, null, null, Json(new { title = "New line" }))],
            context with { SubmissionKey = Guid.NewGuid().ToString() }, 100, default);
        Assert.Equal(200, batch.Status);
        Assert.All(seen, request => Assert.True(request.User.IsAnonymous));
        Assert.Contains(seen, request => request.Action == DataSourceAction.Create && request.Transaction is not null);
        var denied = new SqlDataSource(optedIn, runtime, new TestUsers(), [new Policy(request => request.Action != DataSourceAction.Create)]);
        Assert.Equal(403, (await denied.CreateAsync(Json(new { title = "Refused" }), AnonymousContext, default)).Status);
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM TASKS WHERE TITLE = 'Refused'"));
    }

    [Theory]
    [InlineData("rowRestriction", "OWNER_SID = :USER_SID OR :USER_SID IS NULL")]
    [InlineData("customStatements", "-- insert\nINSERT INTO TASKS (TITLE, OWNER_SID) VALUES (:FIELD.title, :USER_KEY) RETURNING TASK_ID")]
    [InlineData("allowInsert", "0")]
    public async Task Anonymous_write_opt_in_preserves_other_access_and_identity_requirements(string setting, string value)
    {
        var source = Working(Tasks(("allowAnonymousWrites", true), ("insertDefaults", "OWNER_SID = visitor"), (setting, value)));
        Assert.Equal(403, (await source.CreateAsync(Json(new { title = "Refused" }), AnonymousContext, default)).Status);
        Assert.Equal(3, Count());
    }

    /// <summary>A combination the engine would refuse at run time is refused when the source is published, where the editor can act on it.</summary>
    [Theory]
    [InlineData("access", "Not public", "needs Public access")]
    [InlineData("writeGroups", "Planners", "cannot be combined with write groups")]
    [InlineData("insertDefaults", "OWNER_SID = :USER_SID", "insert default for OWNER_SID, which uses :USER_SID")]
    public void Anonymous_write_opt_in_is_refused_at_publish_when_the_source_needs_an_account(string setting, string value, string problem)
    {
        var values = Tasks(("allowAnonymousWrites", true), ("insertDefaults", "OWNER_SID = visitor"), (setting, value));
        var errors = new List<string>();
        Assert.Null(DataSourceDefinition.Read(Guid.NewGuid(), "Tasks", alias => values.GetValueOrDefault(alias), picked => picked.Split(','), errors));
        Assert.Contains(errors, error => error.Contains(problem));
        // Without the opt-in the same source publishes.
        values.Remove("allowAnonymousWrites");
        Assert.NotNull(Definition(values));
    }

    [Fact]
    public async Task Anonymous_write_opt_in_does_not_turn_a_failed_account_into_a_visitor()
    {
        var source = Working(Tasks(("allowAnonymousWrites", true), ("insertDefaults", "OWNER_SID = visitor")));
        var failed = Context(Owner with { Key = "disabled" });
        Assert.Equal(403, (await source.CreateAsync(Json(new { title = "Refused" }), failed, default)).Status);
        Assert.Equal(3, Count());
    }

    [Theory]
    [InlineData("Not public")]
    [InlineData("Specific groups")]
    public async Task Nonpublic_sources_refuse_anonymous_reads(string? access)
    {
        var source = Working(Tasks(("access", access), ("readGroups", access == "Specific groups" ? "Planners" : "")));
        Assert.False(source.AllowsAnonymousRead);
        Assert.Equal(403, (await source.InitializeAsync(AnonymousContext, default)).Status);
        Assert.Equal(403, (await source.LoadAsync("1", AnonymousContext, default)).Status);
        Assert.Equal(403, (await source.ListRowsAsync(new("projectId", "1"), AnonymousContext, 100, default)).Status);
    }

    [Fact]
    public async Task Public_reads_still_check_policies_identity_binds_and_unresolved_accounts()
    {
        var denied = Working(Tasks(("access", "Public")), new Policy(request => !request.User.IsAnonymous));
        Assert.Equal(403, (await denied.InitializeAsync(AnonymousContext, default)).Status);
        Assert.Equal(403, (await denied.LoadAsync("1", AnonymousContext, default)).Status);
        Assert.Equal(403, (await denied.ListRowsAsync(new("projectId", "1"), AnonymousContext, 100, default)).Status);
        foreach (var settings in new[] {
            Tasks(("access", "Public"), ("rowRestriction", "OWNER_SID = :USER_SID OR :USER_SID IS NULL")),
            Tasks(("access", "Public"), ("source", "SELECT * FROM TASKS WHERE OWNER_SID = :USER_SID OR :USER_SID IS NULL")),
        }) {
            var restricted = Working(settings);
            Assert.False(restricted.AllowsAnonymousRead);
            Assert.Equal(403, (await restricted.InitializeAsync(AnonymousContext, default)).Status);
            Assert.Equal(403, (await restricted.LoadAsync("1", AnonymousContext, default)).Status);
            Assert.Equal(200, (await restricted.LoadAsync("1", Context(Owner), default)).Status);
        }
        var source = Working(Tasks(("access", "Public")));
        var unresolved = Context(Owner with { Key = "missing-account" });
        Assert.Equal(403, (await source.InitializeAsync(unresolved, default)).Status);
        Assert.Equal(403, (await source.LoadAsync("1", unresolved, default)).Status);
    }

    [Fact]
    public async Task Explicit_access_modes_control_groups_and_invalidate_the_contract()
    {
        var grouped = Definition(Tasks(("access", "Specific groups"), ("readGroups", "Planners")));
        Assert.Equal(DataSourceAccess.SpecificGroups, grouped.Access);
        Assert.Equal(DataSourceAccess.Public, Definition(Tasks()).Access);
        Assert.False(DataSourceQueries.CanRead(grouped, Other));
        Assert.True(DataSourceQueries.CanRead(grouped with { WriteGroups = ["Crew"] }, Other));
        var restricted = Working(Tasks(("access", "Specific groups"), ("readGroups", "Planners")));
        Assert.Equal(200, (await restricted.LoadAsync("1", Context(Owner), default)).Status);
        Assert.Equal(403, (await restricted.LoadAsync("1", Context(Other), default)).Status);
        Assert.Equal(403, (await restricted.LoadAsync("1", Context(Editor), default)).Status);
        var authenticated = grouped with { Access = DataSourceAccess.NotPublic };
        Assert.True(DataSourceQueries.CanRead(authenticated, Other));
        Assert.True(DataSourceQueries.CanRead(authenticated, Administrator));
        var publicSource = grouped with { Access = DataSourceAccess.Public };
        Assert.True(DataSourceQueries.CanRead(publicSource, DataSourceUser.Anonymous));
        Assert.NotEqual(grouped.ComputeFingerprint(), publicSource.ComputeFingerprint());
        Assert.NotEqual(new SqlDataSource(grouped, runtime, new TestUsers(), []).ContractVersion,
            new SqlDataSource(publicSource, runtime, new TestUsers(), []).ContractVersion);
        Assert.False(DataSourceQueries.CanRead(grouped with { Access = (DataSourceAccess)99 }, Owner));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "Planners")]
    [InlineData("Specific groups", "")]
    [InlineData("unexpected", "Planners")]
    public void Publishing_rejects_missing_or_unknown_access_and_groups_without_a_read_group(string? access, string groups)
    {
        var values = Tasks(("access", access), ("readGroups", groups));
        var errors = new List<string>();
        Assert.Null(DataSourceDefinition.Read(Guid.NewGuid(), "Tasks", alias => values.GetValueOrDefault(alias), picked => picked.Split(','), errors));
        Assert.NotEmpty(errors);
    }

    [Theory]
    [InlineData(null)]
    [InlineData((DataSourceAccess)99)]
    public async Task Invalid_access_is_a_runtime_configuration_error_even_with_read_groups(DataSourceAccess? access)
    {
        var definition = Definition(Tasks()) with { Access = access, ReadGroups = ["Planners"] };
        Assert.False(DataSourceQueries.CanRead(definition, Owner));
        Assert.False(DataSourceQueries.CanRead(definition, DataSourceUser.Anonymous));
        // Access validation happens before opening or even resolving a database connection.
        Assert.Equal("Select an access value: Public, Not public or Specific groups.",
            Assert.Single(runtime.Validate(definition with { Connection = "NotRegistered" })));
        var source = new SqlDataSource(definition, runtime, new TestUsers(), []);
        Assert.Contains("Select an access value", source.Problem);
        Assert.False(source.AllowsAnonymousRead);
        Assert.Equal(503, (await source.LoadAsync("1", Context(Owner), default)).Status);
        Assert.Equal(503, (await source.InitializeAsync(AnonymousContext, default)).Status);
        await Assert.ThrowsAsync<DataSourceException>(() => new DataSourceQueries(runtime, new TestUsers(), []).QueryAsync(definition, Context(Owner)));
    }

    private sealed class TestUsers : IDataSourceUserResolver
    {
        public Task<DataSourceUser?> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct) => Task.FromResult(
            new[] { Owner, Other, Administrator, Editor }.FirstOrDefault(u => u.Key == principal.FindFirstValue(ClaimTypes.NameIdentifier)));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    [Theory]
    [InlineData("members", true, false, false)]
    [InlineData("users", false, true, true)]
    [InlineData("administrators", false, false, true)]
    public async Task Application_policy_chooses_members_users_or_a_user_group(string rule, bool memberAllowed, bool editorAllowed, bool adminAllowed)
    {
        var policy = new Policy(request => rule switch {
            "members" => request.User.IsMember,
            "users" => request.User.IsBackOfficeUser,
            _ => request.User.IsInUserGroup("admin"),
        });
        var source = Working(Tasks(), policy);
        foreach (var (account, allowed) in new[] { (Owner, memberAllowed), (Editor, editorAllowed), (Administrator, adminAllowed) }) {
            Assert.Equal(allowed ? 200 : 403, (await source.LoadAsync("1", Context(account), default)).Status);
            var queries = new DataSourceQueries(runtime, new TestUsers(), [policy]);
            if (allowed) Assert.NotEmpty((await queries.QueryAsync(Definition(Tasks()), Context(account))).Rows);
            else await Assert.ThrowsAsync<UnauthorizedAccessException>(() => queries.QueryAsync(Definition(Tasks()), Context(account)));
        }
    }

    [Fact]
    public async Task Application_values_are_resolved_once_per_operation_and_once_per_batch_of_rows()
    {
        var provider = new CountingProvider("editor");
        var source = new SqlDataSource(Definition(Tasks(("insertDefaults", "OWNER_SID = :APP.owner"))), runtime, new TestUsers(), [], new BindVariableProviders([provider]));
        var context = Context(Editor) with { SubmissionKey = Guid.NewGuid().ToString() };
        var created = await source.CreateAsync(Json(new { title = "From a provider", projectId = "1" }), context, default);
        Assert.Equal(200, created.Status);
        Assert.Equal(1, provider.Calls);
        Assert.Equal("editor", Scalar($"SELECT OWNER_SID FROM TASKS WHERE TASK_ID = {created.Record!.Key}"));
        // Three rows in one save ask once, so a time- or database-dependent answer cannot differ between the rows.
        var batch = await source.SaveRowsAsync(new DataSourceScope("projectId", "1"), [
            new(RowOperation.Create, null, null, Json(new { title = "First of three" })), new(RowOperation.Create, null, null, Json(new { title = "Second" })),
            new(RowOperation.Update, created.Record.Key, created.Record.Version, Json(new { title = "Third, changed" }))],
            context with { SubmissionKey = Guid.NewGuid().ToString() }, 100, default);
        Assert.Equal(200, batch.Status);
        Assert.Equal(2, provider.Calls);
    }

    private sealed class CountingProvider(string owner) : IBindVariableProvider
    {
        public int Calls { get; private set; }
        public ValueTask<IReadOnlyDictionary<string, object?>?> ResolveAsync(FormExecutionContext context, CancellationToken ct)
        {
            Calls++;
            return ValueTask.FromResult<IReadOnlyDictionary<string, object?>?>(new Dictionary<string, object?> { ["owner"] = owner });
        }
    }

    private sealed class Policy(Func<DataSourceRequest, bool> allow) : IDataSourcePolicy
    {
        public List<DataSourceRequest> Requests { get; } = [];
        public ValueTask<bool> IsAllowedAsync(DataSourceRequest request, CancellationToken ct) { Requests.Add(request); return ValueTask.FromResult(allow(request)); }
    }

    [Fact]
    public async Task Policies_receive_edit_permission_for_the_host_page_on_reads_writes_and_row_batches()
    {
        var pageKey = Guid.NewGuid();
        var context = Context(Editor) with { PageKey = pageKey };
        var users = new PageUsers(pageKey);
        var policy = new Policy(request => request.User.CanEditPage);
        var definition = Definition(Tasks(("insertDefaults", "OWNER_SID = 'editor'")));
        var source = new SqlDataSource(definition, runtime, users, [policy]);
        var queries = new DataSourceQueries(runtime, users, [policy]);
        Assert.Equal(200, (await source.InitializeAsync(context, default)).Status);
        var created = await source.CreateAsync(Json(new { title = "Page editor", projectId = "1" }), context, default);
        Assert.Equal(200, created.Status);
        var key = created.Record!.Key!;
        Assert.Equal(200, (await source.LoadAsync(key, context, default)).Status);
        var updated = await source.UpdateAsync(key, Json(new { title = "Updated" }), created.Record.Version, context, default);
        Assert.Equal(200, updated.Status);
        Assert.NotEmpty((await queries.QueryAsync(definition, context)).Rows);
        var scope = new DataSourceScope("projectId", "1");
        Assert.Equal(200, (await source.ListRowsAsync(scope, context, 100, default)).Status);
        Assert.Equal(200, (await source.SaveRowsAsync(scope, [new(RowOperation.Update, key, updated.Record!.Version, Json(new { title = "Batch updated" }))],
            context with { SubmissionKey = Guid.NewGuid().ToString() }, 100, default)).Status);
        var saved = await source.LoadAsync(key, context, default);
        Assert.Equal(204, (await source.DeleteAsync(key, saved.Record!.Version, context, default)).Status);
        Assert.All(policy.Requests, request => {
            Assert.Equal(pageKey, request.Context.PageKey);
            Assert.True(request.User.CanEditPage);
        });
        Assert.All(Enum.GetValues<DataSourceAction>(), action => Assert.Contains(policy.Requests, request => request.Action == action));

        // The same account on another page gets refused by the rule; no permission leaks between pages.
        var otherPage = context with { PageKey = Guid.NewGuid() };
        var original = (await source.LoadAsync("1", context, default)).Record!;
        Assert.Equal(403, (await source.InitializeAsync(otherPage, default)).Status);
        Assert.Equal(403, (await source.LoadAsync("1", otherPage, default)).Status);
        Assert.Equal(403, (await source.CreateAsync(Json(new { title = "Refused" }), otherPage with { SubmissionKey = Guid.NewGuid().ToString() }, default)).Status);
        Assert.Equal(403, (await source.UpdateAsync("1", Json(new { title = "Refused" }), original.Version, otherPage, default)).Status);
        Assert.Equal(403, (await source.DeleteAsync("1", original.Version, otherPage, default)).Status);
        Assert.Equal(403, (await source.ListRowsAsync(scope, otherPage, 100, default)).Status);
        Assert.Equal(403, (await source.SaveRowsAsync(scope, [new(RowOperation.Update, "1", original.Version, Json(new { title = "Refused" }))], otherPage, 100, default)).Status);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => queries.QueryAsync(definition, otherPage));
        Assert.Equal("Paint the hangar", Scalar("SELECT TITLE FROM TASKS WHERE TASK_ID = 1"));
    }

    private sealed class PageUsers(Guid editablePage) : IDataSourceUserResolver
    {
        public Task<DataSourceUser?> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct) => throw new InvalidOperationException("The calling page is required.");
        public Task<DataSourceUser?> ResolveAsync(FormExecutionContext context, CancellationToken ct)
            => Task.FromResult<DataSourceUser?>(Editor with { CanEditPage = context.PageKey == editablePage });
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private void Execute(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={file};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={file};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    private long Count(string where = "1 = 1") => (long)Scalar("SELECT COUNT(*) FROM TASKS WHERE " + where)!;

    [Fact]
    public void Fields_come_from_the_schema_with_the_mapping_applied()
    {
        var source = Working(Tasks());
        var fields = source.Fields.ToDictionary(f => f.Key);
        Assert.Equal(["taskId", "title", "ownerSid", "priority", "dueDate", "done", "projectId", "tag", "version", "createdBy", "createdAt", "updatedBy", "updatedAt"],
            source.Fields.Select(f => f.Key));
        Assert.True(fields["taskId"] is { ReadOnly: true, HasServerDefault: true, Kind: "number" });
        Assert.True(fields["title"] is { Required: true, ReadOnly: false, MaxLength: 100, Kind: "text", Label: "Title" });
        Assert.True(fields["ownerSid"] is { Required: true, HasServerDefault: true });
        Assert.True(fields["priority"] is { Kind: "number", Required: true, HasServerDefault: true });
        Assert.True(fields["dueDate"] is { Kind: "date", Required: false, Label: "Due date" });
        Assert.True(fields["done"] is { Kind: "checkbox", HasServerDefault: true });
        Assert.True(fields["tag"] is { Kind: "text", MaxLength: FieldKinds.DefaultTextLength });
        Assert.True(fields["version"] is { ReadOnly: false, Required: true, HasServerDefault: true });
        Assert.True(fields["createdBy"] is { ReadOnly: true } && fields["updatedAt"] is { ReadOnly: true, Kind: "datetime" });
        Assert.StartsWith("ds-", source.ContractVersion);
        Assert.Equal(source.ContractVersion, new SqlDataSource(source.Definition, runtime, new TestUsers(), []).ContractVersion);
        Assert.NotEqual(source.ContractVersion, new SqlDataSource(source.Definition with { AllowDelete = false }, runtime, new TestUsers(), []).ContractVersion);

        // Neither inserts nor updates: every field is read only, so no page has to make one editable.
        Assert.All(Working(Tasks(("allowInsert", "0"), ("allowUpdate", "0"), ("allowDelete", "0"))).Fields, f => Assert.True(f.ReadOnly));
        Assert.Equal("client", Working(Tasks(("mapping", """{ "client": "TITLE" }"""))).Fields[1].Key);
    }

    [Fact]
    public async Task Initialize_starts_a_new_record_with_insert_defaults_and_bind_variables()
    {
        Assert.Contains("engine fills", Source(Tasks(("insertDefaults", "CREATED_BY = someone"))).Problem);
        var source = Working(Tasks(("insertDefaults", "OWNER_SID = :USER_SID\nPRIORITY = 2\nDUE_DATE = :NOW\nTAG = :PARAM.tag\nTITLE = 'New task, for :USER_NAME'")));
        var result = await source.InitializeAsync(Context(Owner, new() { ["TAG"] = "From the URL" }), default);
        Assert.Equal(200, result.Status);
        var values = result.Record!.Values;
        Assert.Null(result.Record.Key);
        Assert.Equal("S-1-5-21-1", values["ownerSid"]);
        Assert.Equal("2", values["priority"]);
        Assert.Equal("2026-10-23", values["dueDate"]);
        Assert.Equal("From the URL", values["tag"]);
        Assert.Equal("New task, for :USER_NAME", values["title"]); // A literal is text, never SQL or a variable.
        Assert.Null(values["projectId"]);
        Assert.True(result.Record.Capabilities is { Create: true, Update: false, Delete: false });
        var anonymous = await source.InitializeAsync(AnonymousContext, default);
        Assert.Equal(200, anonymous.Status);
        Assert.Null(anonymous.Record!.Values["ownerSid"]);
        Assert.False(anonymous.Record.Capabilities.Create);
        Assert.False((await Working(Tasks(("allowInsert", "0"))).InitializeAsync(Context(Owner), default)).Record!.Capabilities.Create);
    }

    [Fact]
    public async Task Records_round_trip_with_checksum_audit_columns_and_a_generated_key()
    {
        var source = Working(Tasks());
        var created = await source.CreateAsync(Json(new { title = "Check the lights", done = true, projectId = "2" }), Context(Owner, new() { ["tag"] = "West" }), default);
        Assert.Equal(200, created.Status);
        var key = created.Record!.Key!;
        Assert.Equal("4", key);
        Assert.Equal(32, created.Record.Version!.Length);
        Assert.Equal(new object?[] { "Check the lights", "S-1-5-21-1", 2L, "2026-10-23", 1L, 2L, "West", "jsmith", "jsmith", "2026-10-23 14:30:00", 1L },
            new[] { "TITLE", "OWNER_SID", "PRIORITY", "DUE_DATE", "DONE", "PROJECT_ID", "TAG", "CREATED_BY", "UPDATED_BY", "CREATED_AT", "VERSION" }
                .Select(c => Scalar($"SELECT {c} FROM TASKS WHERE TASK_ID = 4")));
        Assert.Equal(true, created.Record.Values["done"]);
        // Zone-less date-times are the server's local time, sent to the browser with the server's offset.
        Assert.StartsWith("2026-10-23T14:30:00", (string?)created.Record.Values["createdAt"]);

        var loaded = await source.LoadAsync(key, Context(Owner), default);
        Assert.Equal(created.Record.Values, loaded.Record!.Values);
        Assert.True(loaded.Record.Capabilities is { Update: true, Delete: true });

        var update = Json(new { title = "Check the lights again", dueDate = "2026-12-24", done = false });
        var updated = await source.UpdateAsync(key, update, loaded.Record.Version!, Context(Administrator), default);
        Assert.Equal(200, updated.Status);
        Assert.NotEqual(loaded.Record.Version, updated.Record!.Version);
        Assert.Equal("Check the lights again", updated.Record.Values["title"]);
        Assert.Equal(new object?[] { "2026-12-24", 0L, "admin", "jsmith", 1L },
            new[] { "DUE_DATE", "DONE", "UPDATED_BY", "CREATED_BY", "VERSION" }.Select(c => Scalar($"SELECT {c} FROM TASKS WHERE TASK_ID = 4")));

        // A save that changes nothing writes nothing: no new version and no audit change.
        var same = await source.UpdateAsync(key, update, updated.Record.Version!, Context(Owner), default);
        Assert.Equal(200, same.Status);
        Assert.Equal(updated.Record.Version, same.Record!.Version);
        Assert.Equal("admin", Scalar("SELECT UPDATED_BY FROM TASKS WHERE TASK_ID = 4"));

        Assert.Equal(204, (await source.DeleteAsync(key, updated.Record.Version, Context(Owner), default)).Status);
        Assert.Equal(404, (await source.LoadAsync(key, Context(Owner), default)).Status);
        Assert.Equal(404, (await source.DeleteAsync(key, updated.Record.Version, Context(Owner), default)).Status);
        Assert.Equal(404, (await source.UpdateAsync(key, update, updated.Record.Version, Context(Owner), default)).Status);
    }

    [Fact]
    public async Task Stale_versions_and_changed_rows_conflict()
    {
        var source = Working(Tasks());
        var loaded = (await source.LoadAsync("1", Context(Owner), default)).Record!;
        Assert.Equal(200, (await source.UpdateAsync("1", Json(new { title = "First save" }), loaded.Version!, Context(Owner), default)).Status);
        Assert.Equal(409, (await source.UpdateAsync("1", Json(new { title = "Second, stale save" }), loaded.Version!, Context(Owner), default)).Status);
        Assert.Equal(409, (await source.DeleteAsync("1", loaded.Version!, Context(Owner), default)).Status);
        Assert.Equal(409, (await source.UpdateAsync("1", Json(new { title = "Not a version" }), "x", Context(Owner), default)).Status);
        Assert.Equal("First save", Scalar("SELECT TITLE FROM TASKS WHERE TASK_ID = 1"));

        // Checksum is an explicit opt-in that needs no version column.
        var checksum = Working(Tasks(("concurrencyMode", DataSourceConcurrency.Checksum)));
        var row = (await checksum.LoadAsync("2", Context(Owner), default)).Record!;
        Assert.Equal(32, row.Version!.Length);
        Execute("UPDATE TASKS SET PRIORITY = 9 WHERE TASK_ID = 2");
        Assert.Equal(409, (await checksum.UpdateAsync("2", Json(new { title = "Lost update" }), row.Version, Context(Owner), default)).Status);
        Assert.Equal(409, (await checksum.UpdateAsync("2", Json(new { title = "Missing token" }), null, Context(Owner), default)).Status);
        Assert.Equal(409, (await checksum.DeleteAsync("2", null, Context(Owner), default)).Status);
        Assert.Equal(409, (await checksum.DeleteAsync("2", row.Version, Context(Owner), default)).Status);
        Assert.Equal(409, (await checksum.SaveRowsAsync(new("projectId", "2"),
            [new(RowOperation.Update, "2", null, Json(new { title = "Missing batch token" }))], Context(Owner), 100, default)).Status);
        var fresh = (await checksum.LoadAsync("2", Context(Owner), default)).Record!;
        Assert.NotEqual(row.Version, fresh.Version);
        var saved = await checksum.UpdateAsync("2", Json(new { title = "Current update" }), fresh.Version!, Context(Owner), default);
        Assert.Equal(200, saved.Status);
        Assert.NotEqual(fresh.Version, saved.Record!.Version);
        Assert.Equal(204, (await checksum.DeleteAsync("2", saved.Record.Version!, Context(Owner), default)).Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Last_save_wins_without_concurrency_for_records_and_batches(string? concurrency)
    {
        var settings = Tasks(("concurrencyMode", concurrency));
        if (concurrency is null) settings.Remove("concurrencyMode");
        var source = Working(settings);
        var loaded = (await source.LoadAsync("1", Context(Owner), default)).Record!;
        Assert.Null(loaded.Version);
        Assert.Equal(200, (await source.UpdateAsync("1", Json(new { title = "First save" }), null, Context(Owner), default)).Status);
        var last = await source.UpdateAsync("1", Json(new { title = "Last save" }), loaded.Version, Context(Owner), default);
        Assert.Equal(200, last.Status);
        Assert.Null(last.Record!.Version);
        Assert.Equal("Last save", Scalar("SELECT TITLE FROM TASKS WHERE TASK_ID = 1"));
        Assert.Equal(1L, Scalar("SELECT VERSION FROM TASKS WHERE TASK_ID = 1")); // A column's existence does not enable it.

        var batch = await source.SaveRowsAsync(new("projectId", "1"),
            [new(RowOperation.Update, "1", null, Json(new { title = "Batch save" }))], Context(Owner), 100, default);
        Assert.Equal(200, batch.Status);
        Assert.Equal("Batch save", Scalar("SELECT TITLE FROM TASKS WHERE TASK_ID = 1"));
        Assert.Equal(200, (await source.SaveRowsAsync(new("projectId", "1"),
            [new(RowOperation.Delete, "1", null, Json(new { }))], Context(Owner), 100, default)).Status);
        Assert.Equal(404, (await source.UpdateAsync("1", Json(new { title = "Deleted" }), null, Context(Owner), default)).Status);
        Assert.Equal(204, (await source.DeleteAsync("2", null, Context(Owner), default)).Status);
    }

    [Fact]
    public void Concurrency_modes_are_typed_and_invalid_choices_are_rejected()
    {
        foreach (var mode in Enum.GetValues<DataSourceConcurrency>())
            Assert.Equal(mode, Definition(Tasks(("concurrencyMode", mode))).Concurrency);
        Assert.Equal(DataSourceConcurrency.LastSaveWins, Definition(Tasks(("concurrencyMode", ""))).Concurrency);
        Assert.Equal(DataSourceConcurrency.Checksum, Definition(Tasks()).Concurrency);
        foreach (var invalid in new[] { "Typo", "VERSION", "VersionColumn", "2", "999" })
        {
            var settings = Tasks(("concurrencyMode", invalid));
            var errors = new List<string>();
            Assert.Null(DataSourceDefinition.Read(Guid.NewGuid(), "Tasks", alias => settings.GetValueOrDefault(alias), _ => [], errors));
            Assert.Contains(errors, e => e.Contains("concurrency mode"));
        }
    }

    [Fact]
    public async Task Row_restriction_hides_rows_and_refuses_writes_that_would_leave_it()
    {
        var source = Working(Tasks(("rowRestriction", "OWNER_SID = :USER_SID")));
        Assert.Equal(200, (await source.LoadAsync("1", Context(Owner), default)).Status);
        Assert.Equal(404, (await source.LoadAsync("2", Context(Owner), default)).Status);
        var others = (await source.LoadAsync("2", Context(Other), default)).Record!;
        Assert.Equal(404, (await source.UpdateAsync("2", Json(new { title = "Not mine" }), others.Version!, Context(Owner), default)).Status);
        Assert.Equal(404, (await source.DeleteAsync("2", others.Version!, Context(Owner), default)).Status);
        Assert.Equal("Sweep the runway", Scalar("SELECT TITLE FROM TASKS WHERE TASK_ID = 2"));

        // A write may not move a row out of the caller's rows: neither an insert for someone else, nor an update.
        var before = Count();
        Assert.Equal(403, (await source.CreateAsync(Json(new { title = "For someone else", ownerSid = "S-1-5-21-2" }), Context(Owner), default)).Status);
        Assert.Equal(before, Count());
        var mine = (await source.LoadAsync("3", Context(Owner), default)).Record!;
        Assert.Equal(403, (await source.UpdateAsync("3", Json(new { ownerSid = "S-1-5-21-2" }), mine.Version!, Context(Owner), default)).Status);
        Assert.Equal("S-1-5-21-1", Scalar("SELECT OWNER_SID FROM TASKS WHERE TASK_ID = 3"));
        Assert.Equal(200, (await source.CreateAsync(Json(new { title = "Mine" }), Context(Owner), default)).Status);

        // An account without a SID matches no row: NULL is never equal to anything.
        Assert.Equal(404, (await source.LoadAsync("1", Context(Administrator), default)).Status);
    }

    [Fact]
    public async Task Hidden_fields_are_written_and_read_only_fields_never()
    {
        var source = Working(Tasks(("mapping", """{ "dueDate": { "kind": "date" }, "tag": { "readOnly": true }, "version": { "readOnly": true } }""")));
        // projectId stands for a hidden field: the form never shows it, but it is bound and submitted, so it is written.
        var created = await source.CreateAsync(Json(new { title = "Hidden foreign key", projectId = "1" }), Context(Owner), default);
        Assert.Equal(1L, Scalar($"SELECT PROJECT_ID FROM TASKS WHERE TASK_ID = {created.Record!.Key}"));
        var updated = await source.UpdateAsync(created.Record.Key!, Json(new { projectId = "2" }), created.Record.Version!, Context(Owner), default);
        Assert.Equal(200, updated.Status);
        Assert.Equal(2L, Scalar($"SELECT PROJECT_ID FROM TASKS WHERE TASK_ID = {created.Record.Key}"));

        // Read-only fields are bound and shown but refused when submitted, whatever makes them read only.
        foreach (var field in new[] { "taskId", "version", "createdBy", "updatedAt", "tag" })
        {
            var refused = await source.UpdateAsync(created.Record.Key!, Json(new Dictionary<string, object> { [field] = "1" }), updated.Record!.Version!, Context(Owner), default);
            Assert.Equal(422, refused.Status);
            Assert.True(refused.Errors!.ContainsKey(field));
        }
        Assert.Equal(422, (await source.CreateAsync(Json(new { title = "x", version = "7" }), Context(Owner), default)).Status);
        Assert.Equal(422, (await source.CreateAsync(Json(new { title = "x", unknown = "7" }), Context(Owner), default)).Status);
        Assert.Equal(new object?[] { 1L, "jsmith", null }, new[] { "VERSION", "UPDATED_BY", "TAG" }.Select(c => Scalar($"SELECT {c} FROM TASKS WHERE TASK_ID = {created.Record.Key}")));
    }

    [Fact]
    public async Task An_update_writes_only_what_changed_and_is_not_blocked_by_stored_values_it_keeps()
    {
        // Legacy data the field rules would refuse: a blank title in a NOT NULL column and a tag longer than its field allows.
        Execute($"UPDATE TASKS SET TITLE = '', TAG = '{new string('x', 5000)}' WHERE TASK_ID = 3");
        var source = Working(Tasks());
        var loaded = (await source.LoadAsync("3", Context(Owner), default)).Record!;
        // The form endpoint sends every writable field back as loaded, with the one the user changed.
        var merged = loaded.Values.Where(v => source.Fields.Any(f => f.Key == v.Key && !f.ReadOnly)).ToDictionary(v => v.Key, v => v.Value);
        merged["priority"] = "1";
        var saved = await source.UpdateAsync("3", Json(merged), loaded.Version!, Context(Owner), default);
        Assert.Equal(200, saved.Status);
        Assert.Equal(new object?[] { "", 5000L, 1L, 1L }, new[] { "TITLE", "LENGTH(TAG)", "PRIORITY", "VERSION" }.Select(c => Scalar($"SELECT {c} FROM TASKS WHERE TASK_ID = 3")));
        merged["title"] = "  ";
        Assert.Equal(422, (await source.UpdateAsync("3", Json(merged), saved.Record!.Version!, Context(Owner), default)).Status);
    }

    [Fact]
    public async Task A_data_source_whose_database_cannot_be_reached_reports_its_problem()
    {
        var missing = Path.Combine(Path.GetDirectoryName(file)!, "no-such-folder", "missing.db");
        var configuration = new ConfigurationBuilder().Build();
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<IConfiguration>(configuration);
        collection.AddTestConnections(connections => connections.AddConnection("MissingDb", _ => new SqliteConnection($"Data Source={missing};Mode=ReadOnly")));
        collection.AddSingleton<DataSourceRuntime>();
        using var provider = collection.BuildServiceProvider();
        var source = new SqlDataSource(Definition(Tasks(("connection", "MissingDb"))), provider.GetRequiredService<DataSourceRuntime>(), new TestUsers(), []);
        Assert.Contains("could not be read through connection MissingDb", source.Problem);
        Assert.Empty(source.Fields);
        Assert.Equal(503, (await source.InitializeAsync(Context(Owner), default)).Status);
        Assert.Equal(503, (await source.LoadAsync("1", Context(Owner), default)).Status);
    }

    [Fact]
    public void A_connection_whose_driver_is_missing_says_so_rather_than_that_it_is_not_registered()
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        collection.AddTestConnections(connections => connections.AddConnection("OracleDb",
            _ => throw new AppBlocks.Connections.DatabaseDrivers.MissingDriverException("No Oracle driver is installed. Add the Oracle.ManagedDataAccess.Core package.")));
        collection.AddSingleton<DataSourceRuntime>();
        using var provider = collection.BuildServiceProvider();
        var runtime = provider.GetRequiredService<DataSourceRuntime>();

        var missingDriver = runtime.Validate(Definition(Tasks(("connection", "OracleDb"))));
        Assert.Contains(missingDriver, e => e.Contains("No Oracle driver is installed"));
        Assert.DoesNotContain(missingDriver, e => e.Contains("not registered"));
        Assert.Contains(runtime.Validate(Definition(Tasks(("connection", "NoSuchDb")))), e => e.Contains("is not registered"));
    }

    [Fact]
    public void A_renamed_column_of_the_written_table_is_read_only_unless_the_mapping_opts_in()
    {
        Execute("CREATE TABLE PEOPLE (ID INTEGER PRIMARY KEY, NAME TEXT NOT NULL, PARENT_ID INTEGER NULL)");
        Dictionary<string, object?> People(string source, string mapping = "") => new() {
            ["alias"] = "people", ["access"] = "Public", ["connection"] = "TasksDb", ["source"] = source, ["keyColumns"] = "ID", ["allowUpdate"] = "1", ["mapping"] = mapping,
        };
        // A self-join reports the parent's NAME as the same table column as the row's own NAME.
        var selfJoin = Working(People("SELECT c.ID, c.PARENT_ID, p.NAME AS PARENT_NAME FROM PEOPLE c LEFT JOIN PEOPLE p ON p.ID = c.PARENT_ID"));
        Assert.True(selfJoin.Fields.Single(f => f.Key == "parentName").ReadOnly);
        Assert.False(selfJoin.Fields.Single(f => f.Key == "parentId").ReadOnly);
        Assert.True(Working(People("SELECT ID, NAME AS FULL_NAME FROM PEOPLE")).Fields.Single(f => f.Key == "fullName").ReadOnly);
        Assert.False(Working(People("SELECT ID, NAME AS FULL_NAME FROM PEOPLE", """{ "fullName": { "readOnly": false } }""")).Fields.Single(f => f.Key == "fullName").ReadOnly);
    }

    [Fact]
    public async Task Values_are_checked_by_the_engine_whatever_the_form_did()
    {
        var source = Working(Tasks(("mapping", """{ "dueDate": { "kind": "date" }, "tag": { "choices": ["A", "B"] }, "priority": { "min": 1, "max": 5 } }""")));
        async Task<string> Refusal(object values)
        {
            var result = await source.CreateAsync(Json(values), Context(Owner), default);
            Assert.Equal(422, result.Status);
            return string.Join(" ", result.Errors!.SelectMany(e => e.Value.Select(v => e.Key + ": " + v)));
        }
        Assert.Contains("title: A value is required", await Refusal(new { priority = "2" }));
        Assert.Contains("title: A value is required", await Refusal(new { title = "   " }));
        Assert.Contains("title: Use at most 100", await Refusal(new { title = new string('x', 101) }));
        Assert.Contains("priority: The number is outside", await Refusal(new { title = "x", priority = "6" }));
        Assert.Contains("priority: Enter a valid value", await Refusal(new { title = "x", priority = "two" }));
        Assert.Contains("tag: Select an available choice", await Refusal(new { title = "x", tag = "C" }));
        Assert.Contains("dueDate: Enter a valid value", await Refusal(new { title = "x", dueDate = "23/10/2026" }));
        Assert.Contains("A submission identifier", (await source.CreateAsync(Json(new { title = "x" }), Context(Owner) with { SubmissionKey = null }, default)).Errors![""][0]);
    }

    [Fact]
    public async Task Policies_can_refuse_any_operation()
    {
        // Only the owner of a task may delete it, and nobody may read task 3.
        var policy = new Policy(request => request.Action switch {
            DataSourceAction.Delete => (string?)request.Current!["ownerSid"] == request.User.Sid,
            DataSourceAction.Load => request.Key != "3",
            _ => true,
        });
        var source = Working(Tasks(), policy);
        Assert.Equal(403, (await source.LoadAsync("3", Context(Owner), default)).Status);
        var task = (await source.LoadAsync("1", Context(Other), default)).Record!;
        Assert.Equal(403, (await source.DeleteAsync("1", task.Version!, Context(Other), default)).Status);
        Assert.Equal(1, Count("TASK_ID = 1"));
        Assert.Equal(204, (await source.DeleteAsync("1", task.Version!, Context(Owner), default)).Status);
        var update = policy.Requests.Where(r => r.Action == DataSourceAction.Delete).ToArray();
        Assert.Equal("S-1-5-21-2", update[0].User.Sid);
        Assert.Equal("1", update[0].Key);

        var proposals = new Policy(request => request.Action != DataSourceAction.Update || (string?)request.Proposed!["title"] != "Forbidden");
        source = Working(Tasks(), proposals);
        var second = (await source.LoadAsync("2", Context(Owner), default)).Record!;
        Assert.Equal(403, (await source.UpdateAsync("2", Json(new { title = "Forbidden" }), second.Version!, Context(Owner), default)).Status);
        Assert.Equal("Sweep the runway", Scalar("SELECT TITLE FROM TASKS WHERE TASK_ID = 2"));
        Assert.Equal("Sweep the runway", (string?)proposals.Requests.Last().Current!["title"]);
        Assert.Equal(403, (await Working(Tasks(), new Policy(_ => false)).InitializeAsync(Context(Owner), default)).Status);
    }

    [Fact]
    public async Task Policies_read_other_rows_through_the_engines_connection_and_transaction()
    {
        var seen = new List<(DataSourceAction Action, ConnectionState? State, bool Transaction)>();
        long owned = 0;
        object? stored = null;
        Exception? blocked = null;
        // At most three tasks per owner: a rule over other rows, read in the snapshot the write will commit.
        var policy = new Policy(request => {
            seen.Add((request.Action, request.Connection?.State, request.Transaction is not null));
            if (request.Action is not (DataSourceAction.Create or DataSourceAction.Update)) return true;
            Assert.Same(request.Connection, request.Transaction!.Connection);
            owned = (long)Read(request, "SELECT COUNT(*) FROM TASKS WHERE OWNER_SID = @owner", ("@owner", request.User.Sid))!;
            if (request.Action == DataSourceAction.Create) return owned < 3;
            stored = Read(request, "SELECT TITLE FROM TASKS WHERE TASK_ID = @key", ("@key", request.Key));
            // The engine's write transaction holds the database, so another connection cannot write meanwhile.
            using var other = new SqliteConnection($"Data Source={file};Pooling=False");
            other.Open();
            using var write = other.CreateCommand();
            write.CommandText = "UPDATE TASKS SET PRIORITY = PRIORITY";
            write.CommandTimeout = 1;
            blocked = Record.Exception(() => write.ExecuteNonQuery());
            return true;
        });
        var source = Working(Tasks(), policy);

        Assert.Equal(200, (await source.InitializeAsync(Context(Owner), default)).Status);
        var task = (await source.LoadAsync("1", Context(Owner), default)).Record!;
        var created = await source.CreateAsync(Json(new { title = "Third of mine" }), Context(Owner), default);
        Assert.Equal(200, created.Status);
        Assert.Equal(2, owned);
        Assert.Equal(403, (await source.CreateAsync(Json(new { title = "Fourth of mine" }), Context(Owner), default)).Status);
        Assert.Equal(3, owned);
        Assert.Equal(3, Count("OWNER_SID = 'S-1-5-21-1'"));
        Assert.Equal(0, Count("TITLE = 'Fourth of mine'"));

        Assert.Equal(200, (await source.UpdateAsync("1", Json(new { title = "Paint it again" }), task.Version!, Context(Owner), default)).Status);
        Assert.Equal("Paint the hangar", stored);
        Assert.Equal(5, Assert.IsType<SqliteException>(blocked).SqliteErrorCode); // SQLITE_BUSY
        Assert.Equal("Paint it again", Scalar("SELECT TITLE FROM TASKS WHERE TASK_ID = 1"));
        Assert.Equal(204, (await source.DeleteAsync(created.Record!.Key!, created.Record.Version!, Context(Owner), default)).Status);

        Assert.Equal([
            (DataSourceAction.Initialize, null, false), (DataSourceAction.Load, ConnectionState.Open, false),
            (DataSourceAction.Create, ConnectionState.Open, true), (DataSourceAction.Create, ConnectionState.Open, true),
            (DataSourceAction.Update, ConnectionState.Open, true), (DataSourceAction.Delete, ConnectionState.Open, true),
        ], seen);
    }

    /// <summary>What a policy does to read other rows: its own command on the engine's connection, in the engine's transaction.</summary>
    private static object? Read(DataSourceRequest request, string sql, params (string Name, object? Value)[] values)
    {
        using var command = request.Connection!.CreateCommand();
        command.Transaction = request.Transaction;
        command.CommandText = sql;
        foreach (var (name, value) in values)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        return command.ExecuteScalar();
    }

    [Fact]
    public async Task Groups_and_allow_flags_limit_who_may_do_what()
    {
        var readers = Working(Tasks(("access", "Specific groups"), ("readGroups", "Planners"), ("writeGroups", "Leads")));
        Assert.Equal(200, (await readers.LoadAsync("1", Context(Owner), default)).Status);
        Assert.Equal(403, (await readers.LoadAsync("1", Context(Other), default)).Status);
        var record = (await readers.LoadAsync("1", Context(Owner), default)).Record!;
        Assert.False(record.Capabilities.Update);
        Assert.Equal(403, (await readers.UpdateAsync("1", Json(new { title = "Planner edit" }), record.Version!, Context(Owner), default)).Status);
        Assert.Equal(403, (await readers.CreateAsync(Json(new { title = "Planner task" }), Context(Owner), default)).Status);
        // User Groups never bypass or impersonate the configured Member Groups.
        Assert.Equal(403, (await readers.UpdateAsync("1", Json(new { title = "Administrator edit" }), record.Version!, Context(Administrator), default)).Status);
        Assert.Equal(403, (await readers.LoadAsync("1", Context(Editor), default)).Status);

        var writers = Working(Tasks(("writeGroups", "Crew")));
        Assert.Equal(200, (await writers.LoadAsync("1", Context(Owner), default)).Status);
        Assert.Equal(403, (await writers.CreateAsync(Json(new { title = "Owner task" }), Context(Owner), default)).Status);
        Assert.Equal(200, (await writers.CreateAsync(Json(new { title = "Crew task" }), Context(Other), default)).Status);

        var noDelete = Working(Tasks(("allowDelete", "0"), ("allowInsert", "0")));
        var task = (await noDelete.LoadAsync("2", Context(Owner), default)).Record!;
        Assert.True(task.Capabilities is { Update: true, Delete: false });
        Assert.Equal(403, (await noDelete.DeleteAsync("2", task.Version!, Context(Administrator), default)).Status);
        Assert.Equal(403, (await noDelete.CreateAsync(Json(new { title = "x" }), Context(Administrator), default)).Status);
        Assert.Equal(1, Count("TASK_ID = 2"));
    }

    [Fact]
    public async Task Values_and_keys_are_bound_never_spliced_into_statements()
    {
        var source = Working(Tasks(("source", "SELECT * FROM TASKS WHERE TITLE <> :PARAM.exclude OR :PARAM.exclude IS NULL"), ("keyColumns", "TASK_ID")));
        foreach (var key in new[] { "1 OR 1=1", "1; DELETE FROM TASKS", "' OR ''='", "", "999" })
            Assert.Equal(404, (await source.LoadAsync(key, Context(Owner), default)).Status);
        var hostile = "Robert'); DROP TABLE TASKS; --";
        var loaded = await source.LoadAsync("1", Context(Owner, new() { ["exclude"] = hostile }), default);
        Assert.Equal(200, loaded.Status);
        Assert.Equal(404, (await source.LoadAsync("1", Context(Owner, new() { ["exclude"] = "Paint the hangar" }), default)).Status);
        var created = await source.CreateAsync(Json(new { title = hostile }), Context(Owner), default);
        Assert.Equal(hostile, created.Record!.Values["title"]);
        Assert.Equal(hostile, Scalar($"SELECT TITLE FROM TASKS WHERE TASK_ID = {created.Record.Key}"));
        Assert.Equal(4, Count());
    }

    [Fact]
    public async Task Creates_are_idempotent_per_submission()
    {
        var source = Working(Tasks());
        var context = Context(Owner);
        var first = await source.CreateAsync(Json(new { title = "Once" }), context, default);
        var retry = await source.CreateAsync(Json(new { title = "Once" }), context, default);
        Assert.Equal(first.Record!.Key, retry.Record!.Key);
        Assert.Equal(1, Count("TITLE = 'Once'"));
        Assert.Equal(422, (await source.CreateAsync(Json(new { title = "Twice" }), context, default)).Status);
        Assert.Equal(200, (await source.CreateAsync(Json(new { title = "Once" }), Context(Owner), default)).Status);
        Assert.Equal(2, Count("TITLE = 'Once'"));
        Assert.Equal(204, (await source.DeleteAsync(first.Record.Key!, first.Record.Version!, Context(Owner), default)).Status);
        Assert.Equal(404, (await source.CreateAsync(Json(new { title = "Once" }), context, default)).Status); // The receipt outlives the row.
    }

    [Fact]
    public async Task A_select_source_writes_the_key_table_and_keeps_its_other_columns_read_only()
    {
        var source = Working(Tasks(
            ("source", "SELECT t.TASK_ID, t.TITLE, t.PROJECT_ID, p.NAME AS PROJECT_NAME, t.TITLE || '!' AS SHOUT, t.VERSION, t.OWNER_SID\nFROM TASKS t LEFT JOIN PROJECTS p ON p.PROJECT_ID = t.PROJECT_ID -- every task"),
            ("mapping", ""), ("auditColumns", ""), ("insertDefaults", "OWNER_SID = :USER_SID")));
        var fields = source.Fields.ToDictionary(f => f.Key);
        Assert.True(fields["projectName"].ReadOnly && fields["shout"].ReadOnly);
        Assert.False(fields["title"].ReadOnly || fields["projectId"].ReadOnly);
        var task = (await source.LoadAsync("1", Context(Owner), default)).Record!;
        Assert.Equal("Hangar", task.Values["projectName"]);
        Assert.Equal("Paint the hangar!", task.Values["shout"]);
        var updated = await source.UpdateAsync("1", Json(new { title = "Paint it", projectId = "2" }), task.Version!, Context(Owner), default);
        Assert.Equal("Runway", updated.Record!.Values["projectName"]);
        Assert.Equal("Paint it!", updated.Record.Values["shout"]);
        Assert.Equal(422, (await source.UpdateAsync("1", Json(new { projectName = "Other" }), updated.Record.Version!, Context(Owner), default)).Status);
        var created = await source.CreateAsync(Json(new { title = "Through the SELECT", projectId = "1" }), Context(Owner), default);
        Assert.Equal("Hangar", created.Record!.Values["projectName"]);
        Assert.Equal("Hangar", Scalar("SELECT NAME FROM PROJECTS WHERE PROJECT_ID = 1"));
    }

    [Fact]
    public async Task User_groups_bind_as_a_list_and_the_restriction_applies_to_the_select()
    {
        var source = Working(Tasks(("rowRestriction", "TAG IN (:USER_GROUPS) OR OWNER_SID = :USER_SID AND :NOW > '2000-01-01'")));
        Assert.Equal(200, (await source.LoadAsync("2", Context(Other), default)).Status); // Crew
        Assert.Equal(404, (await source.LoadAsync("1", Context(Other), default)).Status);
        Assert.Equal(200, (await source.LoadAsync("3", Context(Owner), default)).Status); // by SID
        var noGroups = Working(Tasks(("rowRestriction", "TAG IN (:USER_GROUPS)")));
        Assert.Equal(404, (await noGroups.LoadAsync("1", Context(Administrator), default)).Status);
        Assert.Equal(200, (await noGroups.LoadAsync("1", Context(Owner), default)).Status); // Planners
    }

    [Fact]
    public async Task Only_code_holds_bind_variables_never_strings_or_comments()
    {
        var source = Working(Tasks(("rowRestriction",
            "OWNER_SID = :USER_SID AND TAG <> ':NOT_A_VARIABLE' -- :ALSO_NOT, and a closing ) in a comment\n/* :NOR_THIS */ AND '10:30' = '10:30'")));
        Assert.Equal(200, (await source.LoadAsync("1", Context(Owner), default)).Status);
        Assert.Equal(404, (await source.LoadAsync("2", Context(Owner), default)).Status);
        Assert.Contains("closes a parenthesis", Source(Tasks(("rowRestriction", "1 = 1) OR (1 = 1"))).Problem);
        Assert.Contains("unterminated comment", Source(Tasks(("rowRestriction", "1 = 1 /* open"))).Problem);
    }

    [Fact]
    public async Task Custom_statements_run_whole_with_bound_parameters()
    {
        var source = Working(Tasks(("customStatements", """
            -- insert
            INSERT INTO TASK_LOG (ENTRY) VALUES ('created by ' || :USER_NAME);
            INSERT INTO TASKS (TITLE, OWNER_SID, TAG) VALUES (UPPER(:FIELD.title), :USER_SID, 'custom') RETURNING TASK_ID;
            -- update
            UPDATE TASKS SET TITLE = :FIELD.title || ' (edited)', VERSION = VERSION + 1, UPDATED_BY = :USER_KEY
            WHERE TASK_ID = :FIELD.taskId AND VERSION = :FIELD.version
            -- delete
            INSERT INTO TASK_LOG (ENTRY) VALUES ('deleted ' || :FIELD.taskId);
            DELETE FROM TASKS WHERE TASK_ID = :FIELD.taskId
            """)));
        var created = await source.CreateAsync(Json(new { title = "custom insert" }), Context(Owner), default);
        Assert.Equal(200, created.Status);
        Assert.Equal("CUSTOM INSERT", created.Record!.Values["title"]);
        Assert.Equal("created by jsmith", Scalar("SELECT ENTRY FROM TASK_LOG ORDER BY ID DESC LIMIT 1"));
        var updated = await source.UpdateAsync(created.Record.Key!, Json(new { title = "Renamed" }), created.Record.Version!, Context(Owner), default);
        Assert.Equal("Renamed (edited)", updated.Record!.Values["title"]);
        Assert.Equal("1001", Scalar($"SELECT UPDATED_BY FROM TASKS WHERE TASK_ID = {created.Record.Key}"));
        Assert.Equal(409, (await source.UpdateAsync(created.Record.Key!, Json(new { title = "Stale" }), created.Record.Version!, Context(Owner), default)).Status);
        Assert.Equal(204, (await source.DeleteAsync(created.Record.Key!, updated.Record.Version!, Context(Owner), default)).Status);
        Assert.Equal($"deleted {created.Record.Key}", Scalar("SELECT ENTRY FROM TASK_LOG ORDER BY ID DESC LIMIT 1"));
        Assert.Equal(0, Count($"TASK_ID = {created.Record.Key}"));
    }

    [Fact]
    public async Task Composite_keys_join_their_values_with_a_bar()
    {
        var pairs = Working(new() {
            ["alias"] = "pairs", ["access"] = "Public", ["connection"] = "TasksDb", ["source"] = "PAIRS", ["keyColumns"] = "A, B",
            ["allowInsert"] = "1", ["allowUpdate"] = "1", ["allowDelete"] = "1",
        });
        var row = (await pairs.LoadAsync("1|y", Context(Owner), default)).Record!;
        Assert.Null(row.Version);
        Assert.Equal("One y", row.Values["label"]);
        Assert.Equal("1|y", row.Key);
        Assert.Equal(404, (await pairs.LoadAsync("1", Context(Owner), default)).Status);
        Assert.Equal(200, (await pairs.UpdateAsync("1|y", Json(new { label = "Changed" }), row.Version!, Context(Owner), default)).Status);
        Assert.Equal("One x", Scalar("SELECT LABEL FROM PAIRS WHERE A = 1 AND B = 'x'"));
        Assert.Equal(422, (await pairs.UpdateAsync("1|x", Json(new { b = "z" }), (await pairs.LoadAsync("1|x", Context(Owner), default)).Record!.Version!, Context(Owner), default)).Status);
        var created = await pairs.CreateAsync(Json(new { a = "2", b = "z", label = "Two z" }), Context(Owner), default);
        Assert.Equal("2|z", created.Record!.Key);
        // A bar or percent sign inside a value is escaped, so the key still reads back as its parts.
        var awkward = await pairs.CreateAsync(Json(new { a = "3", b = "x|y%z", label = "Awkward" }), Context(Owner), default);
        Assert.Equal("3|x%7Cy%25z", awkward.Record!.Key);
        Assert.Equal("Awkward", (await pairs.LoadAsync(awkward.Record.Key!, Context(Owner), default)).Record!.Values["label"]);
    }

    [Fact]
    public void Publishing_reports_every_definition_mistake()
    {
        IReadOnlyList<string> Validate(params (string, object?)[] settings)
        {
            var values = Tasks(settings);
            var errors = new List<string>();
            var definition = DataSourceDefinition.Read(Guid.NewGuid(), "Tasks", alias => values.GetValueOrDefault(alias), picked => picked.Split(','), errors);
            return definition is null ? errors : runtime.Validate(definition);
        }
        void Refused(string expected, params (string, object?)[] settings)
        {
            var errors = Validate(settings);
            Assert.True(errors.Any(e => e.Contains(expected, StringComparison.OrdinalIgnoreCase)), $"Expected '{expected}' in: {string.Join(" | ", errors)}");
        }
        Assert.Empty(Validate());
        Refused("not registered", ("connection", "NoSuchDb"));
        Refused("was not found", ("source", "NO_SUCH_TABLE"));
        Refused("could not be read", ("source", "SELECT NOPE FROM TASKS"));
        Refused("without semicolons", ("source", "SELECT * FROM TASKS; DELETE FROM TASKS"));
        Refused("table name", ("source", "TASKS WHERE 1=1"));
        Refused("Enter the key columns", ("keyColumns", ""));
        Refused("primary key or a unique key", ("keyColumns", "TITLE"));
        Refused("primary key or a unique key", ("source", "LOOSE"), ("keyColumns", "ID"), ("concurrencyMode", DataSourceConcurrency.LastSaveWins), ("auditColumns", ""), ("insertDefaults", ""), ("mapping", ""));
        Refused("is a view", ("source", "OPEN_TASKS"), ("concurrencyMode", DataSourceConcurrency.LastSaveWins), ("auditColumns", ""), ("insertDefaults", ""), ("mapping", ""));
        Refused("not a column of the row source", ("keyColumns", "NOPE"));
        Refused("not a bind variable", ("rowRestriction", "OWNER_SID = :USER"));
        Refused("restricts nothing", ("rowRestriction", "OWNER_SID = :PARAM.owner"));
        Refused("restricts nothing", ("rowRestriction", "OWNER_SID = :FIELD.ownerSid"));
        Refused("not a bind variable", ("rowRestriction", "OWNER_SID = :APP"));
        Assert.Empty(Validate(("rowRestriction", "OWNER_SID = :APP.tenant")));
        Refused("outside IN", ("rowRestriction", "TAG = :USER_GROUPS"));
        Refused("could not be applied", ("rowRestriction", "NOPE = 1"));
        Refused("unterminated", ("rowRestriction", "TAG = 'open"));
        Refused("not valid JSON", ("mapping", "{ nope"));
        Refused("does not have", ("mapping", """{ "x": "NOPE" }"""));
        Refused("kind must be", ("mapping", """{ "dueDate": { "kind": "calendar" } }"""));
        Refused("not a valid number", ("insertDefaults", "PRIORITY = high"));
        Refused("not a field", ("insertDefaults", "TAG = :FIELD.nope"));
        Refused("cannot use :USER_GROUPS", ("insertDefaults", "TAG = :USER_GROUPS"));
        Refused("not an audit column", ("auditColumns", "deletedBy = TAG"));
        Refused("must be a text", ("auditColumns", "createdBy = PRIORITY"));
        Refused("does not compile", ("customStatements", "-- update\nUPDATE TASKS SET NOPE = 1 WHERE TASK_ID = :FIELD.taskId"));
        Refused("not a field", ("customStatements", "-- delete\nDELETE FROM TASKS WHERE TASK_ID = :FIELD.nope"));
        Refused("Start the custom statements", ("customStatements", "DELETE FROM TASKS"));
        Refused("alias", ("alias", "has spaces"));
        Assert.Contains("not a column of the row source", Source(Tasks(("keyColumns", "NOPE"))).Problem);
        Assert.Equal(3, Count()); // Validation compiles statements and reads schemas; it never writes.
    }
}
