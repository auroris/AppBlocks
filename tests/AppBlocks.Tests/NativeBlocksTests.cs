using System.Text.Json;
using System.Xml.Linq;
using AppBlocks.Samples;
using Microsoft.Extensions.FileProviders;
using UmbracoAppHost.Blocks;

namespace AppBlocks.Tests;

public sealed class NativeBlocksTests
{
    [Theory]
    [InlineData("Umbraco.BlockList")]
    [InlineData("Umbraco.BlockGrid")]
    public void Discovery_finds_forms_inside_a_native_container_and_ignores_text_that_looks_like_blocks(string editor)
    {
        var form = Guid.NewGuid(); var field = Guid.NewGuid(); var container = Guid.NewGuid();
        var nested = new {
            layout = new Dictionary<string, object> { ["Umbraco.BlockGrid"] = new[] { new { contentKey = form, areas = new[] { new { items = new[] { new { contentKey = field } } } } } } },
            contentData = new object[] {
                new { key = form, contentTypeKey = AppBlockSchema.Key("appForm"), values = Array.Empty<object>() },
                new { key = field, contentTypeKey = AppBlockSchema.Key("appCheckbox"), values = new[] { new { alias = "field", value = "approved" } } },
            },
        };
        var json = JsonSerializer.Serialize(new {
            layout = new Dictionary<string, object> { [editor] = new[] { new { contentKey = container } } },
            contentData = new object[] {
                new { key = container, contentTypeKey = AppBlockSchema.Key(AppBlockSchema.FormsContainerAlias), values = new[] { new { alias = "forms", value = nested } } },
                new { key = Guid.NewGuid(), contentTypeKey = Guid.NewGuid(), values = new[] { new { alias = "text", value = JsonSerializer.Serialize(nested) } } },
            },
        });
        var discovered = Assert.Single(FormInventory.Parse(Guid.NewGuid(), "Ordinary article", false, null, json));
        Assert.Equal(form, discovered.FormKey);
        Assert.Equal("checkbox", Assert.Single(discovered.Fields).Kind);
        Assert.Equal("approved", discovered.Fields[0].Key);
    }

    [Fact]
    public void Package_schema_contains_native_blocks_without_a_page_type_or_template_and_has_unique_definition_keys()
    {
        var files = new ManifestEmbeddedFileProvider(typeof(AppBlocksExtensions).Assembly);
        Assert.Empty(files.GetDirectoryContents("Schema/Templates"));
        var identities = new HashSet<Guid>();
        var types = files.GetDirectoryContents("Schema/ContentTypes").Where(f => !f.IsDirectory).Select(f => {
            using var stream = f.CreateReadStream(); return XElement.Load(stream);
        }).ToArray();
        Assert.DoesNotContain(types, t => (string?)t.Attribute("Alias") == "appBlockPage");
        foreach (var type in types) {
            Assert.True(identities.Add(Guid.Parse(type.Attribute("Key")!.Value)));
            foreach (var key in type.Descendants("GenericProperty").Select(p => p.Element("Key")!).Concat(type.Element("Tabs")!.Elements("Tab").Select(p => p.Element("Key")!)))
                Assert.True(identities.Add(Guid.Parse(key.Value)), "Duplicate schema identity: " + key.Value);
        }
        Assert.Equal("true", types.Single(t => (string?)t.Attribute("Alias") == AppBlockSchema.FormsContainerAlias).Element("Info")!.Element("IsElement")!.Value);
        var report = types.Single(t => (string?)t.Attribute("Alias") == "appReport");
        Assert.Contains(report.Descendants("GenericProperty"), p => p.Element("Alias")!.Value == "dataSource");
        Assert.DoesNotContain(report.Descendants("GenericProperty"), p => p.Element("Alias")!.Value is "reportName" or "sql");
    }
}
