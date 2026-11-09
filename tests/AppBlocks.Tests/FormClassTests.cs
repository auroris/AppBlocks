using System.Text.Json;
using AppBlocks.Blocks;
using AppBlocks.Forms;

namespace AppBlocks.Tests;

public sealed class FormClassTests
{
    [Fact]
    public async Task A_form_binds_a_registered_class_or_a_published_data_source_and_the_class_keeps_a_shared_alias()
    {
        var orders = new Resource("orders");
        var registry = new FormResourceRegistry([orders], [new ContentSources(new Resource("orders.records"), new Resource("orders"))]);
        var validator = new AppBlocksValidator(registry);
        await validator.LoadAsync();
        Assert.Empty(validator.Validate(Form("orders")));
        Assert.Empty(validator.Validate(Form("orders.records")));
        Assert.Contains("Select a registered C# form class or a published data source.", validator.Validate(Form("content-node")));
        Assert.Same(orders, await registry.FindAsync("orders"));
    }

    [Fact]
    public void A_form_that_does_not_edit_a_required_field_publishes_with_a_warning_that_it_cannot_create()
    {
        var validator = new AppBlocksValidator(new FormResourceRegistry([new Resource("tasks", new FormField("title", "Title", "text", Required: true),
            new FormField("owner", "Owner", "text", Required: true), new FormField("status", "Status", "text"), new FormField("code", "Code", "text", Required: true, HasServerDefault: true))], []));
        var page = BlockPages.Empty();
        BlockPages.Layout(page).Add(BlockPages.FormBlock(page, "tasks", [BlockPages.Add(page, "appText", ("field", "status")), BlockPages.Add(page, "appText", ("field", "owner"), ("readOnly", "1"))], []));
        var warnings = new List<string>();
        Assert.Empty(validator.Validate(page.ToJsonString(), warnings));
        Assert.Equal("This form cannot create records: the required fields 'title', 'owner' are not editable in it.", Assert.Single(warnings));
        // A read-only form never creates, so it has nothing to warn about.
        BlockPages.SetValue(BlockPages.Value(BlockPages.Blocks(page, "appForm").Single(), "formInterface")!["contentData"]![0]!, "viewOnly", "1");
        warnings.Clear();
        Assert.Empty(validator.Validate(page.ToJsonString(), warnings));
        Assert.Empty(warnings);
    }

    [Fact]
    public void Form_validation_explains_when_a_saved_interface_still_uses_Block_List()
    {
        var validator = new AppBlocksValidator(new FormResourceRegistry([new Resource("orders")], []));
        var legacy = Form("orders").Replace("Umbraco.SingleBlock", "Umbraco.BlockList", StringComparison.Ordinal);
        Assert.Contains("Recreate the Form Interface using the Single Block editor.", validator.ValidateEmbedded(legacy));
    }

    [Fact]
    public void A_plugin_builds_a_form_page_from_aliases_that_publishing_accepts_and_that_installs_the_same_each_time()
    {
        static string Build()
        {
            var values = new AppBlockValues();
            var form = values.Form(SampleOrdersResource.ResourceAlias, [
                values.Field("appText", "customer", new { label = "Customer" }, id: "orders.customer", columnSpan: 6),
                values.Field("appSelect", "region", id: "orders.region", columnSpan: 6), values.Field("appSelect", "status", id: "orders.status"),
                values.Field("appNumber", "amount", id: "orders.amount"), values.Field("appDate", "orderDate", id: "orders.date"),
            ], returnPage: Guid.Parse("3f1d2c4b-0000-4000-8000-000000000001"), actions: new { saveLabel = "Save order" }, values: new { heading = "Order" }, id: "orders.form");
            values.Place(values.Add(AppBlockSchema.RichTextAlias, id: "orders.intro"), form);
            return values.ToJson();
        }
        var json = Build();
        Assert.Equal(json, Build());
        var warnings = new List<string>();
        Assert.Empty(new AppBlocksValidator(new FormResourceRegistry([new SampleOrdersResource()], [])).Validate(json, warnings));
        Assert.Empty(warnings);

        // The areas are the ones the schema's grid gives a form, in its order, as the backoffice saves them.
        var page = BlockPages.Empty();
        var expected = BlockPages.Wrap(page, "appForm")["areas"]!.AsArray().Select(area => area!["key"]!.GetValue<string>());
        var layout = System.Text.Json.Nodes.JsonNode.Parse(json)!["layout"]!["Umbraco.BlockGrid"]!.AsArray();
        Assert.Equal(expected, layout[1]!["areas"]!.AsArray().Select(area => area!["key"]!.GetValue<string>()));
        Assert.Equal(6, layout[1]!["areas"]![0]!["items"]![0]!["columnSpan"]!.GetValue<int>());
        Assert.Throws<ArgumentException>(() => new AppBlockValues().Add("appForm").Place("nowhere"));
        Assert.Throws<ArgumentException>(() => new AppBlockValues().Field(AppBlockSchema.RichTextAlias, "customer"));
    }

    [Fact]
    public void A_date_time_field_default_may_be_NOW_UTC()
    {
        var validator = new AppBlocksValidator(new FormResourceRegistry([new Resource("events", new FormField("startsAt", "Starts at", "datetime"), new FormField("stamp", "Stamp", "text"))], []));
        string Page(string field, string block, string value)
        {
            var page = BlockPages.Empty();
            BlockPages.Layout(page).Add(BlockPages.FormBlock(page, "events", [BlockPages.Add(page, block, ("field", field), ("defaultValue", value))], []));
            return page.ToJsonString();
        }
        // The engine resolves it for a date-time field as the UTC moment, so publishing no longer refuses it.
        Assert.Empty(validator.Validate(Page("startsAt", "appDateTime", " :now_utc ")));
        Assert.Empty(validator.Validate(Page("startsAt", "appDisplay", ":NOW_UTC")));
        Assert.Empty(validator.Validate(Page("startsAt", "appDateTime", ":NOW")));
        Assert.Empty(validator.Validate(Page("stamp", "appText", ":NOW_UTC")));
    }

    private static string Form(string resource)
    {
        var key = Guid.NewGuid(); var configuration = Guid.NewGuid();
        var formInterface = JsonSerializer.Serialize(new {
            contentData = new[] { new { key = configuration, contentTypeKey = AppBlockSchema.Key("appFormInterface"), values = new[] { new { alias = "resource", value = resource } } } },
            layout = new Dictionary<string, object> { ["Umbraco.SingleBlock"] = new[] { new { contentKey = configuration } } },
        });
        return JsonSerializer.Serialize(new {
            contentData = new[] { new { key, contentTypeKey = AppBlockSchema.Key("appForm"), values = new[] { new { alias = "formInterface", value = formInterface } } } },
            layout = new Dictionary<string, object> { ["Umbraco.BlockGrid"] = new[] { new { contentKey = key } } },
        });
    }

    /// <summary>Resources defined outside code, as published data sources arrive.</summary>
    private sealed class ContentSources(params IFormResource[] resources) : IFormResourceProvider
    {
        public IEnumerable<IFormResource> Resources => resources;
    }
    private sealed class Resource(string alias, params FormField[] fields) : IFormResource
    {
        public string Alias => alias;
        public string ContractVersion => "1";
        public IReadOnlyList<FormField> Fields => fields;
        public Task<FormResult> InitializeAsync(FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Denied());
        public Task<FormResult> LoadAsync(string key, FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Denied());
        public Task<FormResult> CreateAsync(JsonElement input, FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Denied());
        public Task<FormResult> UpdateAsync(string key, JsonElement input, string? checksum, FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Denied());
        public Task<FormResult> DeleteAsync(string key, string? checksum, FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Denied());
    }
}
