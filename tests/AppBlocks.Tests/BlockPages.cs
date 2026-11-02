using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using AppBlocks.Blocks;
using AppBlocks.Forms;
using Microsoft.Extensions.FileProviders;

namespace AppBlocks.Tests;

/// <summary>Block grid values as the backoffice saves them, for tests of what publishing accepts. The reports suite links this file.</summary>
internal static class BlockPages
{
    /// <summary>The Tabular form block <see cref="OrdersForm"/> places in its form.</summary>
    public static readonly Guid LinesBlock = Guid.Parse("07c3df77-52bf-9126-972e-c56da2deded3");

    public static JsonNode Empty() => JsonNode.Parse("""{"contentData":[],"settingsData":[],"expose":[],"layout":{"Umbraco.BlockGrid":[]}}""")!;
    public static JsonArray Layout(JsonNode content) => content["layout"]!["Umbraco.BlockGrid"]!.AsArray();
    public static IEnumerable<JsonNode> Blocks(JsonNode content, string alias) => content["contentData"]!.AsArray().Select(block => block!)
        .Where(block => block["contentTypeKey"]!.GetValue<string>() == AppBlockSchema.Key(alias).ToString());
    public static JsonNode? Value(JsonNode block, string alias) => block["values"]!.AsArray().FirstOrDefault(value => value!["alias"]!.GetValue<string>() == alias)?["value"];
    public static void SetValue(JsonNode block, string alias, string value)
    {
        var property = block["values"]!.AsArray().FirstOrDefault(item => item!["alias"]!.GetValue<string>() == alias);
        if (property is not null) property["value"] = value;
        else block["values"]!.AsArray().Add(new JsonObject { ["alias"] = alias, ["value"] = value, ["culture"] = null, ["segment"] = null });
    }

    /// <summary>Adds a block to the page's content and returns its layout item, which is for the caller to place.</summary>
    public static JsonNode Add(JsonNode content, string alias, params (string Alias, string Value)[] values)
    {
        var key = Guid.NewGuid().ToString();
        var block = new JsonObject { ["key"] = key, ["contentTypeKey"] = AppBlockSchema.Key(alias).ToString(), ["values"] = new JsonArray() };
        foreach (var value in values) SetValue(block, value.Alias, value.Value);
        content["contentData"]!.AsArray().Add(block);
        content["expose"]!.AsArray().Add(new JsonObject { ["contentKey"] = key, ["culture"] = null, ["segment"] = null });
        return new JsonObject { ["contentKey"] = key, ["columnSpan"] = 12, ["rowSpan"] = 1, ["areas"] = new JsonArray(), ["settingsKey"] = null };
    }

    /// <summary>Adds a block whose areas, those the package's grid gives it, hold these items.</summary>
    public static JsonNode Wrap(JsonNode content, string alias, params JsonNode[][] children) => Fill(Add(content, alias), alias, children);

    private static JsonNode Fill(JsonNode layout, string alias, JsonNode[][] children)
    {
        var block = GridBlock(alias);
        foreach (var (area, index) in block["areas"]!.AsArray().Select((area, index) => (area: area!, index)))
        {
            var items = new JsonArray();
            if (index < children.Length) foreach (var item in children[index]) items.Add(item);
            layout["areas"]!.AsArray().Add(new JsonObject { ["key"] = area["key"]!.DeepClone(), ["items"] = items });
        }
        return layout;
    }

    private static JsonNode GridBlock(string alias)
    {
        using var stream = new ManifestEmbeddedFileProvider(typeof(AppBlocksExtensions).Assembly).GetFileInfo("Schema/DataTypes/ApplicationApplicationBlocks.config").CreateReadStream();
        var configuration = JsonNode.Parse(XElement.Load(stream).Element("Config")!.Value)!;
        return configuration["blocks"]!.AsArray().Single(item => item!["contentElementTypeKey"]!.GetValue<string>() == AppBlockSchema.Key(alias).ToString())!;
    }

    /// <summary>The block values' copies of a fragment, with the layout items for the caller to place.</summary>
    public static JsonNode[] Merge(JsonNode into, JsonNode fragment)
    {
        foreach (var name in new[] { "contentData", "settingsData", "expose" })
            foreach (var item in fragment[name]!.AsArray()) into[name]!.AsArray().Add(item!.DeepClone());
        return Layout(fragment).Select(item => item!.DeepClone()).ToArray();
    }

    public static JsonNode Static(string marker)
    {
        var content = Empty();
        Layout(content).Add(Wrap(content, AppBlockSchema.RegionAlias, [Add(content, AppBlockSchema.HtmlAlias, ("html", "<p>" + marker + "</p>"))]));
        return content;
    }

    /// <summary>A form over <see cref="SampleOrdersResource"/>: its fields in a fieldset, an Attachments block, a Tabular form block over the order lines, and its actions. The first block is the customer field.</summary>
    public static JsonNode OrdersForm(string resource = SampleOrdersResource.ResourceAlias)
    {
        var page = Empty();
        JsonNode[] fields = [
            Add(page, "appText", ("field", "customer")), Add(page, "appSelect", ("field", "region")), Add(page, "appSelect", ("field", "status")),
            Add(page, "appNumber", ("field", "amount")), Add(page, "appDate", ("field", "orderDate")), Add(page, "appTextArea", ("field", "notes")),
        ];
        var fieldset = Wrap(page, "appFieldset", fields);
        SetValue(page["contentData"]!.AsArray()[^1]!, "heading", "Order");
        var attachments = Add(page, AppBlockSchema.AttachmentsAlias, ("heading", "Attachments"), ("purposes", "Invoice\nDelivery note\nOther"));
        var lines = Add(page, AppBlockSchema.TabularAlias, ("heading", "Order lines"), ("dataSource", "orders.lines"), ("linkField", "orderId"), ("fields", "item, quantity, unitPrice, notes"), ("maxRows", "50"));
        var linesBlock = page["contentData"]!.AsArray()[^1]!;
        linesBlock["key"] = LinesBlock.ToString();
        page["expose"]!.AsArray()[^1]!["contentKey"] = LinesBlock.ToString();
        lines["contentKey"] = LinesBlock.ToString();
        var actions = Add(page, "appActions", ("saveLabel", "Save order"));
        Layout(page).Add(FormBlock(page, resource, [fieldset, attachments, lines], [actions]));
        return page;
    }

    /// <summary>A form holding every content block beside fields that use the field properties: rich text and regions of HTML, CSS and JavaScript.</summary>
    public static JsonNode ContentBlocksForm()
    {
        var page = Empty();
        const string callout = "<div class=\"callout\"><p>Raw HTML.</p></div>";
        const string style = ".callout { padding: 1rem; }";
        var text = Add(page, AppBlockSchema.RichTextAlias, ("text", """{"blocks":{"contentData":[],"settingsData":[],"expose":[],"layout":{}},"markup":"<p>Content blocks sit in a form beside its fields.</p>"}"""));
        var code = Wrap(page, AppBlockSchema.RegionAlias, [
            Add(page, AppBlockSchema.HtmlAlias, ("html", callout)), Add(page, AppBlockSchema.CssAlias, ("css", style)),
            Add(page, AppBlockSchema.ScriptAlias, ("script", "document.title = 'Content blocks';")),
        ]);
        var properties = Wrap(page, AppBlockSchema.RegionAlias, [
            Add(page, AppBlockSchema.HtmlAlias, ("html", callout)), Add(page, AppBlockSchema.CssAlias, ("css", style)),
            Add(page, "appText", ("field", "customer"), ("placeholder", "Who is ordering"), ("defaultValue", ":USER_NAME"), ("width", "6")),
            Add(page, "appSelect", ("field", "region"), ("placeholder", "Choose a region"), ("width", "3")),
            Add(page, "appNumber", ("field", "amount"), ("placeholder", "0.00"), ("width", "3")),
            Add(page, "appDate", ("field", "orderDate"), ("width", "4")),
            Add(page, "appTextArea", ("field", "notes"), ("placeholder", "Anything the warehouse should know"), ("defaultValue", ":PARAM.note"), ("width", "8")),
            Add(page, "appSelect", ("field", "status"), ("hidden", "1")),
        ]);
        var actions = Add(page, "appActions", ("saveLabel", "Save order"), ("cancelLabel", "Back"));
        Layout(page).Add(FormBlock(page, SampleOrdersResource.ResourceAlias, [text, code, properties], [actions]));
        return page;
    }

    /// <summary>A page holding one Schedule block, over two sources by their aliases, showing days.</summary>
    public static JsonNode Schedule()
    {
        var page = Empty();
        Layout(page).Add(Add(page, AppBlockSchema.ScheduleAlias, ("heading", "Orders schedule"), ("rowsSource", "orders.schedule-rows"), ("barsSource", "orders-schedule"),
            ("daysBefore", "7"), ("daysAfter", "14"), ("columns", "Days"), ("linkTemplate", "/orders-form/?id={key}")));
        return page;
    }

    public static JsonNode FormBlock(JsonNode page, string resource, JsonNode[] content, JsonNode[] actions)
    {
        var form = Wrap(page, "appForm", content, actions);
        var block = page["contentData"]!.AsArray()[^1]!;
        SetValue(block, "heading", "Order details");
        var formInterface = Guid.NewGuid().ToString();
        block["values"]!.AsArray().Add(new JsonObject {
            ["alias"] = "formInterface", ["culture"] = null, ["segment"] = null,
            ["value"] = new JsonObject {
                ["contentData"] = new JsonArray(new JsonObject {
                    ["contentTypeKey"] = AppBlockSchema.Key("appFormInterface").ToString(), ["key"] = formInterface,
                    ["values"] = new JsonArray(
                        new JsonObject { ["alias"] = "resource", ["culture"] = null, ["segment"] = null, ["value"] = resource },
                        new JsonObject { ["alias"] = "keyParameter", ["culture"] = null, ["segment"] = null, ["value"] = "id" }),
                }),
                ["settingsData"] = new JsonArray(),
                ["expose"] = new JsonArray(new JsonObject { ["contentKey"] = formInterface, ["culture"] = null, ["segment"] = null }),
                ["layout"] = new JsonObject { ["Umbraco.SingleBlock"] = new JsonArray(new JsonObject { ["contentKey"] = formInterface, ["settingsKey"] = null }) },
            },
        });
        return form;
    }
}

/// <summary>The fields of a small orders form, for publishing to judge bindings against. Nothing in these tests runs it.</summary>
internal sealed class SampleOrdersResource : IFormResource
{
    public const string ResourceAlias = "sample.orders";
    private static FormChoice[] Choices(params string[] values) => values.Select(v => new FormChoice(v, v)).ToArray();

    public string Alias => ResourceAlias;
    public string ContractVersion => "1";
    public IReadOnlyList<FormField> Fields { get; } = [
        new("customer", "Customer", "text", Required: true, MaxLength: 200),
        new("region", "Region", "text", Required: true, Choices: Choices("NORTH", "SOUTH", "EAST", "WEST")),
        new("status", "Status", "text", Required: true, Choices: Choices("NEW", "PENDING", "SHIPPED", "CANCELLED")),
        new("amount", "Amount", "number", Required: true, Minimum: 0, Maximum: 999999999),
        new("orderDate", "Order date", "date", Required: true),
        new("notes", "Notes", "text", MaxLength: 4000),
    ];
    public Task<FormResult> InitializeAsync(FormExecutionContext context, CancellationToken ct) => throw new NotSupportedException();
    public Task<FormResult> LoadAsync(string key, FormExecutionContext context, CancellationToken ct) => throw new NotSupportedException();
    public Task<FormResult> CreateAsync(JsonElement input, FormExecutionContext context, CancellationToken ct) => throw new NotSupportedException();
    public Task<FormResult> UpdateAsync(string key, JsonElement input, string version, FormExecutionContext context, CancellationToken ct) => throw new NotSupportedException();
    public Task<FormResult> DeleteAsync(string key, string version, FormExecutionContext context, CancellationToken ct) => throw new NotSupportedException();
}
