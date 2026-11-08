using System.Text.Json;
using AppBlocks.Blocks;
using AppBlocks.Forms;

namespace AppBlocks.Tests;

public sealed class FormClassTests
{
    [Fact]
    public void Form_validation_uses_registered_classes_without_loading_content_sources()
    {
        var registry = new FormResourceRegistry([new Resource("orders")], [new UnavailableContentSources()]);
        var validator = new AppBlocksValidator(registry);
        Assert.Empty(validator.ValidateEmbedded(Form("orders")));
        Assert.Contains("Select a registered C# form class.", validator.ValidateEmbedded(Form("content-node")));
    }

    [Fact]
    public void Form_validation_explains_when_a_saved_interface_still_uses_Block_List()
    {
        var validator = new AppBlocksValidator(new FormResourceRegistry([new Resource("orders")], []));
        var legacy = Form("orders").Replace("Umbraco.SingleBlock", "Umbraco.BlockList", StringComparison.Ordinal);
        Assert.Contains("Recreate the Form Interface using the Single Block editor.", validator.ValidateEmbedded(legacy));
    }

    [Fact]
    public void A_date_time_field_default_cannot_be_NOW_UTC()
    {
        var validator = new AppBlocksValidator(new FormResourceRegistry([new Resource("events", new FormField("startsAt", "Starts at", "datetime"), new FormField("stamp", "Stamp", "text"))], []));
        string Page(string field, string block, string value)
        {
            var page = BlockPages.Empty();
            BlockPages.Layout(page).Add(BlockPages.FormBlock(page, "events", [BlockPages.Add(page, block, ("field", field), ("defaultValue", value))], []));
            return page.ToJsonString();
        }
        Assert.Contains("The default of 'startsAt' cannot be :NOW_UTC: a date-time field holds the web server's local time. Use :NOW, or bind a text field to keep a UTC stamp.",
            validator.ValidateEmbedded(Page("startsAt", "appDateTime", " :now_utc ")));
        Assert.Contains(validator.ValidateEmbedded(Page("startsAt", "appDisplay", ":NOW_UTC")), error => error.Contains(":NOW_UTC"));
        Assert.Empty(validator.ValidateEmbedded(Page("startsAt", "appDateTime", ":NOW")));
        Assert.Empty(validator.ValidateEmbedded(Page("stamp", "appText", ":NOW_UTC")));
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

    private sealed class UnavailableContentSources : IFormResourceProvider
    {
        public IEnumerable<IFormResource> Resources => throw new InvalidOperationException("Forms must not load content sources.");
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
