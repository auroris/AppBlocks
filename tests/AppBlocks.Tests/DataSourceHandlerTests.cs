using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AppBlocks.DataSources;
using AppBlocks.Forms;

// Hooks are async lambdas whether or not they await, so each test reads as the class it stands for.
#pragma warning disable CS1998

namespace AppBlocks.Tests;

/// <summary>
/// A data source's backing class on SQLite, without Umbraco: each hook's outcomes, the SQL helper's binds, what the engine still
/// checks around the class, and a class that is no longer registered.
/// </summary>
public sealed class DataSourceHandlerTests : IDisposable
{
    private static readonly DataSourceUser Owner = new("1001", "S-1-5-21-1", "jsmith", "jsmith@example.invalid", ["Buyers"], false);
    private static readonly DataSourceUser Other = new("1002", "S-1-5-21-2", "kjones", "kjones@example.invalid", ["Crew"], false);
    private readonly string file = Path.Combine(Directory.CreateTempSubdirectory("data-source-handler-").FullName, "orders.db");
    private readonly ServiceProvider services;
    private readonly DataSourceRuntime runtime;

    public DataSourceHandlerTests()
    {
        Execute("""
            CREATE TABLE ORDERS (
                ORDER_ID INTEGER PRIMARY KEY,
                CUSTOMER VARCHAR(100) NOT NULL,
                STATUS TEXT NOT NULL DEFAULT 'NEW',
                OWNER TEXT NULL,
                TOTAL NUMERIC NULL,
                VERSION INTEGER NOT NULL DEFAULT 1,
                CREATED_BY TEXT NULL);
            CREATE TABLE ORDER_LOG (ID INTEGER PRIMARY KEY, ORDER_ID INTEGER NULL, ENTRY TEXT NOT NULL, BY_KEY TEXT NULL);
            CREATE TABLE ORDER_LINES (
                LINE_ID INTEGER PRIMARY KEY,
                ORDER_ID INTEGER NOT NULL REFERENCES ORDERS (ORDER_ID),
                PRODUCT TEXT NOT NULL,
                QUANTITY INTEGER NOT NULL DEFAULT 1,
                VERSION INTEGER NOT NULL DEFAULT 1);
            INSERT INTO ORDERS (CUSTOMER, STATUS, OWNER, TOTAL) VALUES ('Hangar Co', 'NEW', '1001', 10), ('Runway Ltd', 'CLOSED', '1001', 20);
            INSERT INTO ORDER_LINES (ORDER_ID, PRODUCT, QUANTITY) VALUES (1, 'Paint', 3);
            """);
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        collection.AddTestConnections(connections => connections.AddConnection("OrdersDb", _ => new SqliteConnection($"Data Source={file};Pooling=False")));
        collection.AddSingleton<DataSourceRuntime>();
        services = collection.BuildServiceProvider();
        runtime = services.GetRequiredService<DataSourceRuntime>();
    }

    public void Dispose() => services.Dispose();

    [Fact]
    public async Task A_class_that_continues_rewrites_what_is_written_and_shown_and_the_engine_writes_it()
    {
        var handler = new Handler {
            Initialize = async operation => { operation.Values!["status"] = "DRAFT"; return DataSourceOutcome.Continue; },
            Create = async operation => {
                operation.Values!["customer"] = operation.Values["customer"]!.ToString()!.ToUpperInvariant();
                // A read-only field the form cannot send, which the class may set.
                operation.Values["status"] = "APPROVED";
                operation.Values["total"] = 42.5m;
                return DataSourceOutcome.Continue;
            },
            Update = async operation => {
                Assert.Equal("APPROVED", operation.Current!["status"]);
                operation.Values!["status"] = "CHANGED";
                // A field the class removes stays as stored.
                operation.Values.Remove("total");
                return DataSourceOutcome.Continue;
            },
            Load = async operation => { operation.Values!["customer"] = "Shown: " + operation.Values["customer"]; return DataSourceOutcome.Continue; },
        };
        var source = Orders(handler);
        var context = Context(Owner);

        var initialized = await source.InitializeAsync(context, default);
        Assert.Equal(200, initialized.Status);
        Assert.Equal("DRAFT", initialized.Record!.Values["status"]);

        var created = await source.CreateAsync(Json(new { customer = "Tower Inc", total = "3" }), context, default);
        Assert.Equal(200, created.Status);
        Assert.Equal("TOWER INC", created.Record!.Values["customer"]);
        Assert.Equal("APPROVED", created.Record.Values["status"]);
        Assert.Equal("42.5", created.Record.Values["total"]);
        var key = created.Record.Key!;
        Assert.Equal("APPROVED", Scalar($"SELECT STATUS FROM ORDERS WHERE ORDER_ID = {key}"));

        var updated = await source.UpdateAsync(key, Json(new { total = "7" }), null, context, default);
        Assert.Equal(200, updated.Status);
        Assert.Equal("CHANGED", updated.Record!.Values["status"]);
        Assert.Equal("42.5", updated.Record.Values["total"]);
        Assert.Equal(1L, Scalar($"SELECT VERSION FROM ORDERS WHERE ORDER_ID = {key}"));

        var loaded = await source.LoadAsync(key, context, default);
        Assert.Equal("Shown: TOWER INC", loaded.Record!.Values["customer"]);
        Assert.Equal("TOWER INC", Scalar($"SELECT CUSTOMER FROM ORDERS WHERE ORDER_ID = {key}"));
        Assert.Equal([DataSourceAction.Initialize, DataSourceAction.Create, DataSourceAction.Update, DataSourceAction.Load], handler.Seen);
    }

    [Fact]
    public async Task A_class_that_handles_a_write_runs_its_own_statements_in_the_engines_transaction()
    {
        var handler = new Handler {
            Create = async operation => {
                Assert.NotNull(operation.Transaction);
                Assert.Same(operation.Transaction!.Connection, operation.Connection);
                var id = await operation.Sql.ScalarAsync("""
                    INSERT INTO ORDERS (CUSTOMER, STATUS, OWNER) VALUES (:FIELD.customer, :ARG.status, :USER_KEY) RETURNING ORDER_ID
                    """, new { status = "HANDLED" });
                await operation.Sql.ExecuteAsync("INSERT INTO ORDER_LOG (ORDER_ID, ENTRY, BY_KEY) VALUES (:ARG.id, 'created', :USER_KEY)", new Dictionary<string, object?> { ["id"] = id });
                return DataSourceOutcome.Handled(Convert.ToString(id, CultureInfo.InvariantCulture));
            },
            Update = async operation => {
                await operation.Sql.ExecuteAsync("UPDATE ORDERS SET STATUS = 'BY CLASS' WHERE ORDER_ID = :ARG.key", new { key = operation.Key });
                return DataSourceOutcome.Handled();
            },
            // A soft delete: the row stays, marked.
            Delete = async operation => {
                Assert.Equal(1, await operation.Sql.ExecuteAsync("UPDATE ORDERS SET STATUS = 'CANCELLED' WHERE ORDER_ID = :FIELD.orderId"));
                return DataSourceOutcome.Handled();
            },
        };
        var source = Orders(handler);
        var context = Context(Owner);

        var created = await source.CreateAsync(Json(new { customer = "Tower Inc" }), context, default);
        Assert.Equal(200, created.Status);
        Assert.Equal("HANDLED", created.Record!.Values["status"]);
        Assert.Equal("1001", created.Record.Values["owner"]);
        var key = created.Record.Key!;
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM ORDERS WHERE CUSTOMER = 'Tower Inc'"));
        Assert.Equal("1001", Scalar($"SELECT BY_KEY FROM ORDER_LOG WHERE ORDER_ID = {key}"));

        var updated = await source.UpdateAsync(key, Json(new { customer = "Ignored" }), null, context, default);
        Assert.Equal(200, updated.Status);
        Assert.Equal("BY CLASS", updated.Record!.Values["status"]);
        Assert.Equal("Tower Inc", updated.Record.Values["customer"]);

        Assert.Equal(204, (await source.DeleteAsync(key, null, context, default)).Status);
        Assert.Equal("CANCELLED", Scalar($"SELECT STATUS FROM ORDERS WHERE ORDER_ID = {key}"));
    }

    [Fact]
    public async Task A_refusal_writes_nothing_and_undoes_what_the_class_already_wrote()
    {
        var handler = new Handler {
            Update = async operation => {
                await operation.Sql.ExecuteAsync("INSERT INTO ORDER_LOG (ORDER_ID, ENTRY) VALUES (:FIELD.orderId, 'tried')");
                return Equals(operation.Current!["status"], "CLOSED") ? DataSourceOutcome.Invalid("status", "A closed order cannot change.") : DataSourceOutcome.Continue;
            },
            Delete = async _ => DataSourceOutcome.Refuse(),
            Load = async operation => operation.Key == "1" ? DataSourceOutcome.Refuse(404) : DataSourceOutcome.Continue,
        };
        var source = Orders(handler);
        var context = Context(Owner);

        var refused = await source.UpdateAsync("2", Json(new { customer = "Changed" }), null, context, default);
        Assert.Equal(422, refused.Status);
        Assert.Equal(["A closed order cannot change."], refused.Errors!["status"]);
        Assert.Equal("Runway Ltd", Scalar("SELECT CUSTOMER FROM ORDERS WHERE ORDER_ID = 2"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM ORDER_LOG"));
        Assert.Equal(403, (await source.DeleteAsync("2", null, context, default)).Status);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM ORDERS WHERE ORDER_ID = 2"));
        Assert.Equal(404, (await source.LoadAsync("1", context, default)).Status);
        Assert.Throws<ArgumentOutOfRangeException>(() => DataSourceOutcome.Refuse(500));
    }

    [Fact]
    public async Task The_engine_checks_access_before_the_class_and_every_policy_after_it()
    {
        var handler = new Handler {
            Create = async operation => { operation.Values!["status"] = "APPROVED"; return DataSourceOutcome.Continue; },
        };
        var seen = new List<DataSourceRequest>();
        var policy = new Policy(request => { seen.Add(request); return request.Action != DataSourceAction.Create || Equals(request.Proposed!["status"], "APPROVED"); });
        var source = Orders(handler, [policy], ("writeGroups", "Buyers"));

        // Without the opt-in anonymous visitors cannot write, and write groups still refuse other Members before the class is asked.
        var anonymous = Context(Owner) with { Member = new ClaimsPrincipal(new ClaimsIdentity()) };
        Assert.Equal(403, (await source.CreateAsync(Json(new { customer = "Anonymous" }), anonymous, default)).Status);
        Assert.Equal(403, (await source.CreateAsync(Json(new { customer = "Crew" }), Context(Other), default)).Status);
        Assert.Empty(handler.Seen);

        Assert.Equal(200, (await source.CreateAsync(Json(new { customer = "Tower Inc" }), Context(Owner), default)).Status);
        Assert.Equal([DataSourceAction.Create], handler.Seen);
        var request = Assert.Single(seen, r => r.Action == DataSourceAction.Create);
        Assert.Equal("APPROVED", request.Proposed!["status"]);

        // A policy that refuses after a handled write undoes the class's insert too.
        var writing = new Handler {
            Create = async operation => {
                var id = await operation.Sql.ScalarAsync("INSERT INTO ORDERS (CUSTOMER) VALUES (:FIELD.customer) RETURNING ORDER_ID");
                return DataSourceOutcome.Handled(Convert.ToString(id, CultureInfo.InvariantCulture));
            },
        };
        var refusing = Orders(writing, [new Policy(r => r.Action != DataSourceAction.Create)]);
        Assert.Equal(403, (await refusing.CreateAsync(Json(new { customer = "Never" }), Context(Owner), default)).Status);
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM ORDERS WHERE CUSTOMER = 'Never'"));
    }

    [Fact]
    public async Task A_class_cannot_place_a_row_outside_the_restriction_or_set_what_the_engine_owns()
    {
        var hidden = new Handler { Create = async operation => { operation.Values!["owner"] = "someone else"; return DataSourceOutcome.Continue; } };
        var restricted = Orders(hidden, [], ("rowRestriction", "OWNER = :USER_KEY"), ("insertDefaults", "OWNER = :USER_KEY"));
        Assert.Equal(403, (await restricted.CreateAsync(Json(new { customer = "Hidden" }), Context(Owner), default)).Status);
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM ORDERS WHERE CUSTOMER = 'Hidden'"));

        var audit = new Handler { Create = async operation => { operation.Values!["createdBy"] = "forged"; return DataSourceOutcome.Continue; } };
        var audited = await Orders(audit, [], ("auditColumns", "createdBy = CREATED_BY")).CreateAsync(Json(new { customer = "Audit" }), Context(Owner), default);
        Assert.Equal(422, audited.Status);
        Assert.Contains("createdBy", audited.Errors!.Keys);

        var rekey = new Handler { Update = async operation => { operation.Values!["orderId"] = "99"; return DataSourceOutcome.Continue; } };
        var rekeyed = await Orders(rekey).UpdateAsync("1", Json(new { customer = "Rekeyed" }), null, Context(Owner), default);
        Assert.Equal(422, rekeyed.Status);
        Assert.Contains("orderId", rekeyed.Errors!.Keys);

        var unknown = new Handler { Create = async operation => { operation.Values!["nothing"] = "x"; return DataSourceOutcome.Continue; } };
        Assert.Equal(422, (await Orders(unknown).CreateAsync(Json(new { customer = "Unknown" }), Context(Owner), default)).Status);
        var wrongKind = new Handler { Create = async operation => { operation.Values!["total"] = "many"; return DataSourceOutcome.Continue; } };
        var invalid = await Orders(wrongKind).CreateAsync(Json(new { customer = "Wrong kind" }), Context(Owner), default);
        Assert.Equal(422, invalid.Status);
        Assert.Contains("total", invalid.Errors!.Keys);
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM ORDERS"));
    }

    [Fact]
    public async Task Lists_and_queries_ask_the_class_which_can_change_shown_rows_but_only_refuse_a_query()
    {
        var handler = new Handler {
            List = async operation => { foreach (var row in operation.Rows!) row["customer"] = "Row: " + row["customer"]; return DataSourceOutcome.Continue; },
            Query = async operation => Equals(operation.Context.Parameters?.GetValueOrDefault("deny"), "1") ? DataSourceOutcome.Refuse() : DataSourceOutcome.Continue,
        };
        var source = Orders(handler);
        var rows = await source.ListRowsAsync(new DataSourceScope("owner", "1001"), Context(Owner), 100, default);
        Assert.Equal(200, rows.Status);
        Assert.Equal(new object?[] { "Row: Hangar Co", "Row: Runway Ltd" }, rows.Rows!.Select(row => row.Values["customer"]));

        var queries = new DataSourceQueries(runtime, new Users(), [], null, new DataSourceHandlers([handler]));
        var definition = source.Definition;
        Assert.Equal(2, (await queries.QueryAsync(definition, Context(Owner))).Rows.Count);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => queries.QueryAsync(definition, Context(Owner, new() { ["deny"] = "1" })));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => queries.AuthorizeAsync(definition, Context(Owner, new() { ["deny"] = "1" }), default));
    }

    [Fact]
    public async Task A_class_implements_only_the_hooks_it_needs_and_its_statements_open_a_connection_when_none_is()
    {
        var source = Orders(new CountingDefaults());
        var initialized = await source.InitializeAsync(Context(Owner), default);
        Assert.Equal(200, initialized.Status);
        Assert.Equal("Order 3", initialized.Record!.Values["customer"]);
        Assert.Equal(200, (await source.LoadAsync("1", Context(Owner), default)).Status);
        Assert.Equal(200, (await source.UpdateAsync("1", Json(new { customer = "Plain" }), null, Context(Owner), default)).Status);
        Assert.Equal("Plain", Scalar("SELECT CUSTOMER FROM ORDERS WHERE ORDER_ID = 1"));
    }

    [Fact]
    public async Task A_backing_class_that_is_not_registered_makes_the_data_source_unavailable()
    {
        var source = new SqlDataSource(Definition(Values(("handler", "orders.gone"))), runtime, new Users(), [], null, new DataSourceHandlers([new Handler()]));
        Assert.Contains("orders.gone", source.Problem);
        Assert.NotEmpty(source.Fields);
        Assert.Equal(503, (await source.InitializeAsync(Context(Owner), default)).Status);
        Assert.Equal(503, (await source.LoadAsync("1", Context(Owner), default)).Status);
        Assert.Equal(503, (await source.CreateAsync(Json(new { customer = "Unguarded" }), Context(Owner), default)).Status);
        Assert.Equal(503, (await source.UpdateAsync("1", Json(new { customer = "Unguarded" }), null, Context(Owner), default)).Status);
        Assert.Equal(503, (await source.DeleteAsync("1", null, Context(Owner), default)).Status);
        Assert.Equal(503, (await source.ListRowsAsync(new DataSourceScope("owner", "1001"), Context(Owner), 100, default)).Status);
        Assert.False(await source.MayWriteAsync(Context(Owner).Member, default));
        // Without any registry, as a test or a site without classes constructs it, the same.
        Assert.NotNull(new SqlDataSource(source.Definition, runtime, new Users(), []).Problem);
        var queries = new DataSourceQueries(runtime, new Users(), [], null, new DataSourceHandlers());
        await Assert.ThrowsAsync<DataSourceException>(() => queries.QueryAsync(source.Definition, Context(Owner)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => queries.AuthorizeAsync(source.Definition, Context(Owner), default));
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM ORDERS"));

        var errors = new List<string>();
        Assert.Null(DataSourceDefinition.Read(Guid.NewGuid(), "Orders", alias => Values(("handler", "not an alias")).GetValueOrDefault(alias), picked => picked.Split(','), errors));
        Assert.Contains(errors, error => error.Contains("backing class"));
        Assert.Throws<InvalidOperationException>(() => new DataSourceHandlers([new Handler(), new Handler()]));
    }

    [Fact]
    public async Task The_sql_helper_binds_every_value_including_the_classs_own_and_refuses_what_it_cannot_bind()
    {
        string? text = null;
        IReadOnlyList<(string Name, object? Value)>? bindings = null;
        var handler = new Handler {
            Update = async operation => {
                var parameters = operation.Sql.Render("""
                    UPDATE ORDERS SET TOTAL = :ARG.amount, OWNER = :USER_KEY -- :ARG.ignored in a comment
                    WHERE ORDER_ID = :FIELD.orderId AND CUSTOMER = :FIELD.customer AND STATUS <> ':ARG.text' AND :PARAM.region IS NOT NULL AND :NOW IS NOT NULL
                    """, new { amount = 12.5m }, out var rendered);
                (text, bindings) = (rendered, parameters.Bindings.ToArray());
                Assert.Throws<DataSourceException>(() => operation.Sql.Render("SELECT :ARG.missing", null, out _));
                Assert.Throws<DataSourceException>(() => operation.Sql.Render("SELECT :FIELD.nothing", null, out _));
                Assert.Throws<DataSourceException>(() => operation.Sql.Render("SELECT :USER_THING", null, out _));
                return DataSourceOutcome.Continue;
            },
        };
        var updated = await Orders(handler).UpdateAsync("1", Json(new { customer = "Proposed" }), null, Context(Owner, new() { ["region"] = "NORTH" }), default);
        Assert.Equal(200, updated.Status);
        Assert.DoesNotContain("12.5", text);
        Assert.Contains("-- :ARG.ignored in a comment", text);
        Assert.Contains("':ARG.text'", text);
        Assert.Equal(12.5m, bindings![0].Value);
        Assert.Equal("1001", bindings[1].Value);
        Assert.Equal(1L, Convert.ToInt64(bindings[2].Value, CultureInfo.InvariantCulture));
        // :FIELD binds the proposed value, not the stored one.
        Assert.Equal("Proposed", bindings[3].Value);
        Assert.Equal("NORTH", bindings[4].Value);
        Assert.Equal(6, bindings.Count);

        // :USER_ values need an account, and only a write changes data.
        var anonymous = new Handler {
            Initialize = async operation => {
                await Assert.ThrowsAsync<DataSourceException>(() => operation.Sql.ScalarAsync("SELECT :USER_KEY"));
                await Assert.ThrowsAsync<InvalidOperationException>(() => operation.Sql.ExecuteAsync("DELETE FROM ORDERS"));
                return DataSourceOutcome.Continue;
            },
        };
        Assert.Equal(200, (await Orders(anonymous, [], ("access", "Public")).InitializeAsync(Context(Owner) with { Member = new ClaimsPrincipal(new ClaimsIdentity()) }, default)).Status);
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM ORDERS"));

        // Editor SQL never takes :ARG values: only a class passes them.
        var errors = new List<string>();
        Assert.Null(DataSourceDefinition.Read(Guid.NewGuid(), "Orders", alias => Values(("insertDefaults", "OWNER = :ARG.owner")).GetValueOrDefault(alias), picked => picked.Split(','), errors));
        Assert.Contains(errors, error => error.Contains(":ARG."));
        Assert.Contains(runtime.Validate(Definition(Values(("rowRestriction", "OWNER = :ARG.owner")))), error => error.Contains(":ARG."));
        Assert.Contains(runtime.Validate(Definition(Values(("customStatements", "-- delete\nDELETE FROM ORDERS WHERE ORDER_ID = :ARG.id")))), error => error.Contains(":ARG."));
    }

    [Fact]
    public void An_oracle_anonymous_block_renders_whole_with_named_binds()
    {
        var column = new PlanColumn("orderId", "ORDER_ID", "Order", FieldKinds.Number, false, true, false, null, null, null, null, null);
        var plan = new DataSourcePlan {
            Definition = Definition(Values()), Dialect = OracleDialect.Instance, Columns = [column], Keys = [column], Defaults = [], Fields = [column.ToField()], ContractVersion = "test",
        };
        var operation = new DataSourceOperation("orders", DataSourceAction.Update, Owner, Context(Owner), plan.Dialect.Kind) { Values = new Dictionary<string, object?> { ["orderId"] = "7" } };
        var sql = new DataSourceSql(plan, operation, variable => variable.Kind == BindVariableKind.UserKey ? Owner.Key : null, _ => throw new InvalidOperationException());
        var parameters = sql.Render("""
            BEGIN
              orders_api.approve(p_order => :FIELD.orderId, p_by => :USER_KEY, p_note => :ARG.note);
              INSERT INTO order_log (order_id, entry) VALUES (:FIELD.orderId, q'[it's :ARG.not_a_bind]');
            END;
            """, new { note = "approved" }, out var text);
        Assert.Equal("""
            BEGIN
              orders_api.approve(p_order => :ds0, p_by => :ds1, p_note => :ds2);
              INSERT INTO order_log (order_id, entry) VALUES (:ds3, q'[it's :ARG.not_a_bind]');
            END;
            """, text);
        Assert.Equal(new object?[] { 7m, Owner.Key, "approved", 7m }, parameters.Bindings.Select(b => b.Value));
    }

    [Fact]
    public async Task A_master_and_its_rows_save_in_one_transaction_and_a_refused_row_writes_nothing()
    {
        var orders = Orders(new Handler());
        var lines = Lines();
        var context = Context(Owner);
        var batch = Batch(lines, Change("create", values: new { product = "Bolt", quantity = "2" }), Change("create", values: new { product = "Nut", orderId = "999" }));

        // A new master's key becomes the link value of its new rows, whatever a change says.
        var created = await orders.SaveWithDetailsAsync(null, Json(new { customer = "Tower Inc" }), null, [batch], context, default);
        Assert.Equal(200, created.Status);
        var key = created.Master.Record!.Key!;
        var rows = Assert.Single(created.Details!).Rows!;
        Assert.Equal(["Bolt", "Nut"], rows.Select(row => (string)row.Values["product"]!));
        Assert.All(rows, row => Assert.Equal(key, row.Values["orderId"]));
        Assert.Equal(2L, Scalar($"SELECT COUNT(*) FROM ORDER_LINES WHERE ORDER_ID = {key}"));

        // The same submission again answers as the first did and writes nothing twice.
        var retried = await orders.SaveWithDetailsAsync(null, Json(new { customer = "Tower Inc" }), null, [batch], context, default);
        Assert.Equal(key, retried.Master.Record!.Key);
        Assert.Equal(2, retried.Details![0].Rows!.Count);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM ORDERS WHERE CUSTOMER = 'Tower Inc'"));
        Assert.Equal(2L, Scalar($"SELECT COUNT(*) FROM ORDER_LINES WHERE ORDER_ID = {key}"));

        // A row the engine refuses rolls back the master and every earlier row, and names its block and its place.
        var refused = await orders.SaveWithDetailsAsync(null, Json(new { customer = "Never" }), null,
            [Batch(lines, Change("create", values: new { product = "Washer" })), Batch(lines, Change("create", values: new { product = "Ok" }), Change("create", values: new { quantity = "1" }))],
            Context(Owner), default);
        Assert.Equal(422, refused.Status);
        Assert.Equal(1, refused.Detail);
        Assert.Equal(1, refused.Refusal!.Index);
        Assert.Contains("product", refused.Refusal.Errors!.Keys);
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM ORDERS WHERE CUSTOMER = 'Never'"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM ORDER_LINES WHERE PRODUCT IN ('Washer', 'Ok')"));

        // An update of the master with an update and a delete of its rows; a row of another master is not found there.
        var bolt = rows[0];
        var updated = await orders.SaveWithDetailsAsync(key, Json(new { customer = "Tower Two" }), "1",
            [Batch(lines, Change("update", bolt.Key, bolt.Version, new { quantity = "5" }), Change("delete", rows[1].Key, rows[1].Version))], Context(Owner), default);
        Assert.Equal(200, updated.Status);
        Assert.Equal("Tower Two", updated.Master.Record!.Values["customer"]);
        Assert.Equal("5", Assert.Single(updated.Details![0].Rows!).Values["quantity"]);
        var foreign = await orders.SaveWithDetailsAsync(key, Json(new { customer = "Tower Three" }), "2",
            [Batch(lines, Change("delete", "1"))], Context(Owner), default);
        Assert.Equal(404, foreign.Status);
        Assert.Equal("Tower Two", Scalar($"SELECT CUSTOMER FROM ORDERS WHERE ORDER_ID = {key}"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM ORDER_LINES WHERE ORDER_ID = 1"));

        // The allow flags and the size of a batch are checked before anything is written.
        var noDeletes = Lines(("allowDelete", "0"));
        var flagged = await orders.SaveWithDetailsAsync(key, Json(new { customer = "Flagged" }), "2", [Batch(noDeletes, Change("delete", bolt.Key))], Context(Owner), default);
        Assert.Equal((403, 0, 0), (flagged.Status, flagged.Detail, flagged.Refusal!.Index));
        var many = Enumerable.Range(0, SqlDataSource.MaxChanges + 1).Select(i => Change("create", values: new { product = "P" + i })).ToArray();
        Assert.Equal(422, (await orders.SaveWithDetailsAsync(key, Json(new { customer = "Many" }), "2", [Batch(lines, many)], Context(Owner), default)).Status);
        Assert.Equal("Tower Two", Scalar($"SELECT CUSTOMER FROM ORDERS WHERE ORDER_ID = {key}"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Master_and_detail_concurrency_is_opt_in_and_conflicts_roll_back_the_save(bool masterChecksum, bool detailChecksum)
    {
        var orders = Orders(new Handler(), null, ("concurrencyMode", masterChecksum ? DataSourceConcurrency.Checksum : DataSourceConcurrency.LastSaveWins));
        var lines = Lines(("concurrencyMode", detailChecksum ? DataSourceConcurrency.Checksum : DataSourceConcurrency.LastSaveWins));
        var master = (await orders.LoadAsync("1", Context(Owner), default)).Record!;
        var detail = (await lines.LoadAsync("1", Context(Owner), default)).Record!;
        var first = await orders.SaveWithDetailsAsync("1", Json(new { customer = "First save" }), master.Version,
            [Batch(lines, Change("update", "1", detail.Version, new { quantity = "4" }))], Context(Owner), default);
        Assert.Equal(200, first.Status);

        var last = await orders.SaveWithDetailsAsync("1", Json(new { customer = "Last save" }), master.Version,
            [Batch(lines, Change("update", "1", detail.Version, new { quantity = "5" }))], Context(Owner), default);
        var detectsConflicts = masterChecksum || detailChecksum;
        Assert.Equal(detectsConflicts ? 409 : 200, last.Status);
        Assert.Equal(detectsConflicts ? "First save" : "Last save", Scalar("SELECT CUSTOMER FROM ORDERS WHERE ORDER_ID = 1"));
        Assert.Equal(detectsConflicts ? 4L : 5L, Scalar("SELECT QUANTITY FROM ORDER_LINES WHERE LINE_ID = 1"));
    }

    [Fact]
    public async Task The_masters_backing_class_sees_every_batch_and_may_apply_change_or_refuse_rows()
    {
        var handler = new Handler {
            Create = async operation => operation.Details.Sum(detail => detail.Changes.Count) == 0
                ? DataSourceOutcome.Invalid("", "An order needs at least one line.") : DataSourceOutcome.Continue,
            Update = async operation => {
                var detail = Assert.Single(operation.Details);
                Assert.Equal(("orders.lines", "orderId", operation.Key), (detail.DataSource, detail.LinkField, detail.MasterKey));
                foreach (var change in detail.Changes.ToArray())
                {
                    var product = change.Values.TryGetProperty("product", out var value) ? value.GetString() : null;
                    // Applied by the class itself, in the same transaction, and taken out of what the engine applies.
                    if (product == "Special")
                    {
                        await operation.Sql.ExecuteAsync("INSERT INTO ORDER_LINES (ORDER_ID, PRODUCT, QUANTITY) VALUES (:ARG.order, 'Special (by class)', :ARG.quantity)",
                            new { order = operation.Key, quantity = 7 });
                        detail.Changes.Remove(change);
                    }
                    // Changed before the engine applies it.
                    else if (product == "Lower") detail.Changes[detail.Changes.IndexOf(change)] = change with { Values = Json(new { product = "lower" }) };
                    else if (product == "Forbidden") return DataSourceOutcome.Invalid("", "Forbidden lines are not sold.");
                }
                return DataSourceOutcome.Continue;
            },
        };
        var orders = Orders(handler);
        var lines = Lines();

        var empty = await orders.SaveWithDetailsAsync(null, Json(new { customer = "Empty" }), null, [Batch(lines)], Context(Owner), default);
        Assert.Equal(422, empty.Status);
        Assert.Null(empty.Detail);
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM ORDERS WHERE CUSTOMER = 'Empty'"));

        var saved = await orders.SaveWithDetailsAsync("1", Json(new { customer = "Hangar Co" }), "1",
            [Batch(lines, Change("create", values: new { product = "Special" }), Change("create", values: new { product = "Lower" }))], Context(Owner), default);
        Assert.Equal(200, saved.Status);
        Assert.Equal(["Paint", "Special (by class)", "lower"], saved.Details![0].Rows!.Select(row => (string)row.Values["product"]!));
        Assert.Equal(7L, Scalar("SELECT QUANTITY FROM ORDER_LINES WHERE PRODUCT = 'Special (by class)' AND ORDER_ID = 1"));

        var forbidden = await orders.SaveWithDetailsAsync("1", Json(new { customer = "Hangar Co" }), "1",
            [Batch(lines, Change("create", values: new { product = "Special" }), Change("create", values: new { product = "Forbidden" }))], Context(Owner), default);
        Assert.Equal(422, forbidden.Status);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM ORDER_LINES WHERE PRODUCT = 'Special (by class)'"));
    }

    private SqlDataSource Lines(params (string Alias, object? Value)[] settings)
    {
        var source = new SqlDataSource(Definition(Values([("alias", "orders.lines"), ("source", "ORDER_LINES"), ("keyColumns", "LINE_ID"), ("mapping", null), ("handler", null), .. settings])),
            runtime, new Users(), []);
        Assert.True(source.Problem is null, source.Problem);
        return source;
    }

    [Fact]
    public async Task Anonymous_master_and_detail_writes_require_each_source_to_opt_in_and_keep_handler_refusals_atomic()
    {
        var handler = new Handler {
            Create = async operation => {
                Assert.True(operation.User.IsAnonymous);
                await operation.Sql.ExecuteAsync("INSERT INTO ORDER_LOG (ENTRY) VALUES ('anonymous')");
                return Equals(operation.Values!["customer"], "Refuse") ? DataSourceOutcome.Refuse() : DataSourceOutcome.Continue;
            },
        };
        var orders = Orders(handler, [], ("access", "Public"), ("allowAnonymousWrites", true));
        var lines = Lines(("access", "Public"), ("allowAnonymousWrites", true));
        var context = Context(Owner) with { Member = new ClaimsPrincipal(new ClaimsIdentity()) };
        var batch = Batch(lines, Change("create", values: new { product = "Visitor line" })) with { Context = context };
        var saved = await orders.SaveWithDetailsAsync(null, Json(new { customer = "Visitor" }), null, [batch], context, default);
        Assert.Equal(200, saved.Status);
        Assert.Equal("Visitor line", Assert.Single(saved.Details![0].Rows!).Values["product"]);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM ORDER_LOG"));

        var refused = await orders.SaveWithDetailsAsync(null, Json(new { customer = "Refuse" }), null, [batch],
            context with { SubmissionKey = Guid.NewGuid().ToString() }, default);
        Assert.Equal(403, refused.Status);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM ORDER_LOG"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM ORDERS WHERE CUSTOMER = 'Refuse'"));

        var closed = Lines(("access", "Public"));
        var denied = await orders.SaveWithDetailsAsync(null, Json(new { customer = "Closed detail" }), null,
            [batch with { Source = closed }], context with { SubmissionKey = Guid.NewGuid().ToString() }, default);
        Assert.Equal(403, denied.Status);
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM ORDERS WHERE CUSTOMER = 'Closed detail'"));
    }

    private static DataSourceChange Change(string operation, string? key = null, string? version = null, object? values = null)
        => new(operation, key, version, Json(values ?? new { }));

    private static DataSourceDetailBatch Batch(SqlDataSource source, params DataSourceChange[] changes)
        => new(Guid.NewGuid(), source, "orderId", changes, 100, Context(Owner));

    private SqlDataSource Orders(IDataSourceHandler handler, IDataSourcePolicy[]? policies = null, params (string Alias, object? Value)[] settings)
    {
        var source = new SqlDataSource(Definition(Values(settings)), runtime, new Users(), policies ?? [], null, new DataSourceHandlers([handler]));
        Assert.True(source.Problem is null, source.Problem);
        return source;
    }

    private static Dictionary<string, object?> Values(params (string Alias, object? Value)[] settings)
    {
        var values = new Dictionary<string, object?> {
            ["access"] = "Not public", ["alias"] = "orders", ["connection"] = "OrdersDb", ["source"] = "ORDERS", ["keyColumns"] = "ORDER_ID",
            ["allowInsert"] = "1", ["allowUpdate"] = "1", ["allowDelete"] = "1",
            ["mapping"] = """{ "status": { "readOnly": true } }""", ["handler"] = "orders.rules",
        };
        foreach (var (alias, value) in settings) values[alias] = value;
        return values;
    }

    private static DataSourceDefinition Definition(Dictionary<string, object?> values)
    {
        var errors = new List<string>();
        var definition = DataSourceDefinition.Read(Guid.NewGuid(), "Orders", alias => values.GetValueOrDefault(alias), picked => picked.Split(','), errors);
        Assert.True(definition is not null, string.Join(" ", errors));
        return definition;
    }

    private static FormExecutionContext Context(DataSourceUser user, Dictionary<string, string>? parameters = null) => new(
        new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, user.Key)], IdentityConstants.ApplicationScheme)),
        Guid.NewGuid(), Guid.NewGuid(), "1", Guid.NewGuid().ToString(), parameters);

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
        return command.ExecuteScalar();
    }

    /// <summary>A backing class whose hooks are what a test sets, recording which it was asked.</summary>
    internal sealed class Handler : IDataSourceHandler
    {
        public string Alias => "orders.rules";
        public List<DataSourceAction> Seen { get; } = [];
        public Func<DataSourceOperation, Task<DataSourceOutcome>>? Initialize, Load, Create, Update, Delete, List, Query;

        private async ValueTask<DataSourceOutcome> Ask(Func<DataSourceOperation, Task<DataSourceOutcome>>? hook, DataSourceOperation operation)
        {
            Seen.Add(operation.Action);
            return hook is null ? DataSourceOutcome.Continue : await hook(operation);
        }

        public ValueTask<DataSourceOutcome> InitializeAsync(DataSourceOperation operation, CancellationToken ct) => Ask(Initialize, operation);
        public ValueTask<DataSourceOutcome> LoadAsync(DataSourceOperation operation, CancellationToken ct) => Ask(Load, operation);
        public ValueTask<DataSourceOutcome> CreateAsync(DataSourceOperation operation, CancellationToken ct) => Ask(Create, operation);
        public ValueTask<DataSourceOutcome> UpdateAsync(DataSourceOperation operation, CancellationToken ct) => Ask(Update, operation);
        public ValueTask<DataSourceOutcome> DeleteAsync(DataSourceOperation operation, CancellationToken ct) => Ask(Delete, operation);
        public ValueTask<DataSourceOutcome> ListAsync(DataSourceOperation operation, CancellationToken ct) => Ask(List, operation);
        public ValueTask<DataSourceOutcome> QueryAsync(DataSourceOperation operation, CancellationToken ct) => Ask(Query, operation);
    }

    /// <summary>Only Initialize: a new order is named after how many there are, read on a connection the helper opens.</summary>
    private sealed class CountingDefaults : IDataSourceHandler
    {
        public string Alias => "orders.rules";

        public async ValueTask<DataSourceOutcome> InitializeAsync(DataSourceOperation operation, CancellationToken ct)
        {
            Assert.Null(operation.Connection);
            var count = await operation.Sql.ScalarAsync("SELECT COUNT(*) + 1 FROM ORDERS", ct: ct);
            operation.Values!["customer"] = $"Order {count}";
            return DataSourceOutcome.Continue;
        }
    }

    private sealed class Users : IDataSourceUserResolver
    {
        public Task<DataSourceUser?> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct)
            => Task.FromResult(new[] { Owner, Other }.FirstOrDefault(u => u.Key == principal.FindFirstValue(ClaimTypes.NameIdentifier)));
    }

    private sealed class Policy(Func<DataSourceRequest, bool> allow) : IDataSourcePolicy
    {
        public ValueTask<bool> IsAllowedAsync(DataSourceRequest request, CancellationToken ct) => ValueTask.FromResult(allow(request));
    }
}
