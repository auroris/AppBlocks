using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AppBlocks.Blocks;
using AppBlocks.DataSources;
using AppBlocks.Forms;
using AppBlocks.Tabular;

namespace AppBlocks.Tests;

/// <summary>
/// The Tabular form block's settings and what publishing checks: a published data source with a writable link field,
/// columns that are its fields, the owner rules Attachments uses, a row limit in range, and new rows that can be
/// completed. Orders and order lines data sources run against a temporary database.
/// </summary>
public sealed class TabularBlockTests
{
    private static TabularBlock Read(params (string Alias, string Value)[] values)
        => TabularBlock.Read(alias => values.LastOrDefault(v => v.Alias == alias).Value);

    [Fact]
    public void Settings_are_read_from_their_text_with_defaults_and_limits()
    {
        var empty = Read();
        Assert.Equal("", empty.DataSource);
        Assert.Equal("", empty.LinkField);
        Assert.Empty(empty.Fields);
        Assert.True(empty.OwnedByForm);
        Assert.False(empty.HideAdd || empty.HideDelete || empty.ReadOnly);
        Assert.Equal(TabularBlock.DefaultMaxRows, empty.MaxRows);
        Assert.Equal("Add row", empty.AddLabel);
        Assert.Equal("Save rows", empty.SaveLabel);
        Assert.Null(empty.Heading);
        Assert.Null(empty.EmptyMessage);

        var block = Read(("dataSource", " orders.lines "), ("linkField", "orderId"), ("fields", "item, quantity;unitPrice\tnotes\n item  "),
            ("ownerResource", "orders"), ("ownerKey", ":PARAM.id"), ("hideAdd", "1"), ("hideDelete", "True"), ("readOnly", "false"),
            ("maxRows", "50"), ("heading", "Lines"), ("addLabel", "New line"), ("saveLabel", "  "));
        Assert.Equal("orders.lines", block.DataSource);
        // Commas, semicolons and whitespace separate the names; each is kept once, in the order written.
        Assert.Equal(["item", "quantity", "unitPrice", "notes"], block.Fields);
        Assert.False(block.OwnedByForm);
        Assert.Equal((true, true, false), (block.HideAdd, block.HideDelete, block.ReadOnly));
        Assert.Equal(50, block.MaxRows);
        Assert.Equal("Lines", block.Heading);
        Assert.Equal("New line", block.AddLabel);
        Assert.Equal("Save rows", block.SaveLabel);

        Assert.Equal(TabularBlock.MaxRowsLimit, Read(("maxRows", "5000")).MaxRows);
        Assert.Equal(TabularBlock.DefaultMaxRows, Read(("maxRows", "0")).MaxRows);
        Assert.Equal(TabularBlock.DefaultMaxRows, Read(("maxRows", "many")).MaxRows);
        Assert.Empty(TabularBlock.ParseFields(" ,; "));
        // The site's AppBlocks:Tabular:MaxRows moves the cap; its default is today's 1000.
        Assert.Equal(TabularBlock.MaxRowsLimit, new TabularOptions().MaxRows);
        Assert.Equal(1000, TabularBlock.MaxRowsLimit);
        Assert.Equal(5000, TabularBlock.Read(alias => alias == "maxRows" ? "5000" : null, 10000).MaxRows);
        Assert.Equal(20, TabularBlock.Read(alias => alias == "maxRows" ? "50" : null, 20).MaxRows);
        Assert.Equal(20, TabularBlock.Read(_ => null, 20).MaxRows);

        // Columns: the named fields the data source has, in order; or every field but the link field.
        FormField[] fields = [new("lineId", "Line", "number"), new("orderId", "Order", "number"), new("item", "Item", "text"), new("notes", "Notes", "text")];
        Assert.Equal(["lineId", "item", "notes"], Read(("linkField", "orderId")).Columns(fields).Select(f => f.Key));
        Assert.Equal(["notes", "item"], Read(("linkField", "orderId"), ("fields", "Notes nowhere item")).Columns(fields).Select(f => f.Key));
    }

    [Fact]
    public async Task Publishing_accepts_the_sample_tabular_forms_and_judges_the_data_source()
    {
        using var provider = Services();
        using var scope = provider.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<AppBlocksValidator>();
        await validator.LoadAsync();
        Assert.Empty(validator.Validate(BlockPages.OrdersForm().ToJsonString()));
        Assert.Empty(validator.Validate(Form(("fields", ""))));

        Assert.Contains(validator.Validate(Form(("dataSource", ""))), e => e == "Select a published data source for the tabular form.");
        Assert.Contains(validator.Validate(Form(("dataSource", "sample.orders"))),
            e => e == "Select a published data source for the tabular form; the rows of a C# resource cannot be edited as a table.");
        Assert.Contains(validator.Validate(Form(("dataSource", "orders.broken"))), e => e.StartsWith("The data source orders.broken is not available:"));
        // Without a catalogue, as here, a data source the registry does not know is not judged; a site's publishing refuses it.
        Assert.Empty(validator.Validate(Form(("dataSource", "nowhere"))));

        Assert.Contains(validator.Validate(Form(("linkField", ""))), e => e.StartsWith("Name the tabular form's link field"));
        Assert.Contains(validator.Validate(Form(("linkField", "parent"))), e => e.Contains("'parent' is not a field of orders.lines"));
        Assert.Contains(validator.Validate(Form(("linkField", "lineId"))),
            e => e == "The link field must be a field the data source writes; keys the database assigns and read-only fields cannot link rows to their record.");

        Assert.Contains(validator.Validate(Form(("fields", "item, colour"))), e => e.Contains("'colour' is not a field of orders.lines"));
        Assert.Contains(validator.Validate(Form(("fields", "item, orderId"))), e => e.StartsWith("The link field cannot be a column of the tabular form"));

        foreach (var limit in new[] { "0", "1001", "2.5", "many" })
            Assert.Contains(validator.Validate(Form(("maxRows", limit))), e => e == "The tabular form's row limit must be a whole number from 1 to 1000.");
        Assert.Empty(validator.Validate(Form(("maxRows", "1000"))));
    }

    [Fact]
    public async Task Publishing_requires_new_rows_to_be_completable_unless_the_block_never_adds_them()
    {
        using var provider = Services();
        using var scope = provider.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<AppBlocksValidator>();
        await validator.LoadAsync();
        // Item is required and has no default; quantity and unit price have defaults, so they may be left out.
        const string message = "Required field 'item' of orders.lines must be a column of the tabular form, or have a default.";
        Assert.Contains(validator.Validate(Form(("fields", "quantity, unitPrice"))), e => e == message);
        Assert.Empty(validator.Validate(Form(("fields", "item, notes"))));
        Assert.Empty(validator.Validate(Form(("fields", "quantity, unitPrice"), ("hideAdd", "1"))));
        Assert.Empty(validator.Validate(Form(("fields", "quantity, unitPrice"), ("readOnly", "1"))));
    }

    [Fact]
    public async Task Publishing_applies_the_attachments_owner_rules_to_the_tabular_form()
    {
        using var provider = Services();
        using var scope = provider.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<AppBlocksValidator>();
        await validator.LoadAsync();

        // Inside a form the rows belong to the form's record unless an owner is named.
        Assert.Contains(validator.Validate(Form(("ownerKey", "4"))),
            e => e == "Leave the Tabular form block's owner key empty when the rows belong to the form's record, or select the owner resource.");
        Assert.Contains(validator.Validate(Form(("ownerResource", "nowhere"), ("ownerKey", "4"))),
            e => e == "'nowhere' is not a registered form resource or a published data source, so it cannot own rows.");
        Assert.Contains(validator.Validate(Form(("ownerResource", "orders"))), e => e == "Give the Tabular form block an owner key: :PARAM.name, :FIELD.alias or a literal key.");
        Assert.Contains(validator.Validate(Form(("ownerResource", "orders"), ("ownerKey", ":FIELD.colour"))),
            e => e == "'colour' is not a field of sample.orders, so the Tabular form block cannot take its owner key from it.");
        Assert.Contains(validator.Validate(Form(("ownerResource", "orders"), ("ownerKey", ":PARAM."))), e => e == "Name the page parameter after :PARAM.");
        Assert.Empty(validator.Validate(Form(("ownerResource", "orders"), ("ownerKey", ":FIELD.customer"))));

        // On its own the block must name its owner, on a website page and at an application page's top level.
        Assert.Contains(validator.Validate(Standalone()), e => e == "Place the Tabular form block inside a form, or select the resource that owns the rows.");
        Assert.Contains(validator.Validate(Standalone()), e => e == "Place the Tabular form block inside a form, or select the resource that owns the rows.");
        Assert.Contains(validator.Validate(Standalone(("ownerResource", "orders"), ("ownerKey", ":FIELD.orderId"))),
            e => e == "A Tabular form block outside a form has no record to take :FIELD values from; use :PARAM.name or place it in a form.");
        Assert.Empty(validator.Validate(Standalone(("ownerResource", "orders"), ("ownerKey", ":PARAM.id"))));
        Assert.Empty(validator.Validate(Standalone(("ownerResource", "orders"), ("ownerKey", ":PARAM.id"))));

        // The rules moved out of the Attachments check unchanged.
        var attachments = BlockPages.Empty();
        BlockPages.Layout(attachments).Add(BlockPages.Add(attachments, AppBlockSchema.AttachmentsAlias, ("heading", "Files")));
        Assert.Contains(validator.Validate(attachments.ToJsonString()), e => e == "Place the Attachments block inside a form, or select the resource that owns the files.");
    }

    [Fact]
    public void The_tabular_form_type_keeps_its_key_and_its_settings_in_order()
    {
        var key = AppBlockSchema.Key(AppBlockSchema.TabularAlias).ToString();
        Assert.Equal("50f10848-f57d-705a-5ccb-bcbd4e49d613", key);
        var type = new AppBlocks.Setup.AppBlocksSchema(typeof(AppBlocks.AppBlocksExtensions).Assembly).Definitions("ContentTypes").Single(definition => definition.Attribute("Alias")!.Value == AppBlockSchema.TabularAlias);
        Assert.Equal(key, type.Attribute("Key")!.Value);
        Assert.Equal(AppBlockSchema.TabularAlias, type.Attribute("Alias")!.Value);
        var aliases = type.Element("GenericProperties")!.Elements("GenericProperty").Select(p => p.Element("Alias")!.Value).ToArray();
        Assert.Equal(new[] { TabularBlock.HeadingAlias, TabularBlock.HelpAlias, TabularBlock.DataSourceAlias, TabularBlock.LinkFieldAlias, TabularBlock.FieldsAlias,
            TabularBlock.OwnerResourceAlias, TabularBlock.OwnerKeyAlias, TabularBlock.HideAddAlias, TabularBlock.HideDeleteAlias, TabularBlock.ReadOnlyAlias,
            TabularBlock.MaxRowsAlias, TabularBlock.EmptyMessageAlias, TabularBlock.AddLabelAlias, TabularBlock.SaveLabelAlias }, aliases);
    }

    /// <summary>The orders form with its Tabular form block's settings replaced; an empty value clears it.</summary>
    private static string Form(params (string Alias, string Value)[] settings)
    {
        var page = BlockPages.OrdersForm();
        var block = page["contentData"]!.AsArray().Single(b => b!["key"]!.GetValue<string>() == BlockPages.LinesBlock.ToString())!;
        foreach (var (alias, value) in settings) BlockPages.SetValue(block, alias, value);
        return page.ToJsonString();
    }

    /// <summary>A page holding nothing but a Tabular form block over the order lines, with these settings.</summary>
    private static string Standalone(params (string Alias, string Value)[] settings)
    {
        var page = BlockPages.Empty();
        BlockPages.Layout(page).Add(BlockPages.Add(page, AppBlockSchema.TabularAlias,
            [("dataSource", "orders.lines"), ("linkField", "orderId"), ("fields", "item, quantity, unitPrice, notes"), .. settings]));
        return page.ToJsonString();
    }

    /// <summary>A validator over Orders and Orders lines data sources and a broken copy of the latter, with their database in a temporary folder.</summary>
    private static ServiceProvider Services()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("app-tabular-test-").FullName, "orders.db");
        var connectionString = $"Data Source={file};Pooling=False";
        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE ORDERS (ORDER_ID INTEGER PRIMARY KEY, CUSTOMER TEXT NOT NULL, VERSION INTEGER NOT NULL DEFAULT 1);
                CREATE TABLE ORDER_LINES (
                    LINE_ID INTEGER PRIMARY KEY, ORDER_ID INTEGER NOT NULL REFERENCES ORDERS (ORDER_ID), ITEM TEXT NOT NULL,
                    QUANTITY INTEGER NOT NULL DEFAULT 1, UNIT_PRICE NUMERIC NOT NULL DEFAULT 0, NOTES TEXT NULL,
                    VERSION INTEGER NOT NULL DEFAULT 1, CREATED_BY TEXT NULL, CREATED_AT TEXT NULL, UPDATED_BY TEXT NULL, UPDATED_AT TEXT NULL);
                """;
            command.ExecuteNonQuery();
        }
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddTestConnections(connections => connections.AddConnection("OrdersDb", _ => new SqliteConnection(connectionString)));
        services.AddScoped<IFormResource, SampleOrdersResource>();
        services.AddScoped<FormResourceRegistry>();
        services.AddSingleton<DataSourceRuntime>();
        services.AddScoped<IFormResourceProvider>(sp => new FixedResources(
            new SqlDataSource(OrdersDataSource(), sp.GetRequiredService<DataSourceRuntime>(), new NoUsers(), []),
            new SqlDataSource(OrderLinesDataSource(), sp.GetRequiredService<DataSourceRuntime>(), new NoUsers(), []),
            new SqlDataSource(OrderLinesDataSource(("alias", "orders.broken"), ("source", "NO_SUCH_TABLE")),
                sp.GetRequiredService<DataSourceRuntime>(), new NoUsers(), [])));
        services.AddScoped<AppBlocksValidator>();
        return services.BuildServiceProvider();
    }

    private static DataSourceDefinition OrdersDataSource() => DataSource("Orders", new() {
        ["alias"] = "orders", ["access"] = "Public", ["connection"] = "OrdersDb", ["source"] = "ORDERS", ["keyColumns"] = "ORDER_ID", ["concurrencyMode"] = DataSourceConcurrency.Checksum,
        ["allowInsert"] = "1", ["allowUpdate"] = "1", ["allowDelete"] = "1",
    });

    private static DataSourceDefinition OrderLinesDataSource(params (string Alias, string Value)[] overrides)
    {
        var values = new Dictionary<string, object?> {
            ["alias"] = "orders.lines", ["access"] = "Public", ["connection"] = "OrdersDb", ["source"] = "ORDER_LINES", ["keyColumns"] = "LINE_ID", ["concurrencyMode"] = DataSourceConcurrency.Checksum,
            ["allowInsert"] = "1", ["allowUpdate"] = "1", ["allowDelete"] = "1",
            ["auditColumns"] = "createdBy = CREATED_BY\ncreatedAt = CREATED_AT\nupdatedBy = UPDATED_BY\nupdatedAt = UPDATED_AT",
            ["insertDefaults"] = "QUANTITY = 1\nUNIT_PRICE = 0",
            ["mapping"] = """{ "item": { "maxLength": 200 }, "quantity": { "min": 1, "max": 9999 }, "unitPrice": { "min": 0, "max": 999999.99 }, "notes": { "maxLength": 1000 }, "createdAt": { "kind": "datetime" }, "updatedAt": { "kind": "datetime" } }""",
        };
        foreach (var (alias, value) in overrides) values[alias] = value;
        return DataSource("Orders lines", values);
    }

    private static DataSourceDefinition DataSource(string name, Dictionary<string, object?> values)
    {
        var errors = new List<string>();
        var definition = DataSourceDefinition.Read(Guid.NewGuid(), name, alias => values.GetValueOrDefault(alias), _ => [], errors);
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
}
