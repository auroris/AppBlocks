using System.Text.Json;
using System.Xml.Linq;
using AppBlocks.Samples;
using Microsoft.Extensions.FileProviders;
using AppBlocks.Blocks;
using Umbraco.Cms.Core.Models.Blocks;
using Umbraco.Cms.Core.Models.PublishedContent;

namespace AppBlocks.Tests;

public sealed class NativeBlocksTests
{
    [Theory]
    [InlineData("Umbraco.BlockList")]
    [InlineData("Umbraco.BlockGrid")]
    [InlineData("Umbraco.SingleBlock")]
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
    public void A_native_block_keeps_its_page_out_of_caches_only_when_it_or_something_inside_it_is_interactive()
    {
        static BlockGridItem Block(string alias, params BlockGridItem[] children)
        {
            var type = TestStubs.Stub<IPublishedContentType>((method, _) => method.Name switch {
                "get_Alias" => alias, "get_PropertyTypes" => Array.Empty<IPublishedPropertyType>(), _ => throw new NotImplementedException(method.Name),
            });
            var element = TestStubs.Stub<IPublishedElement>((method, _) => method.Name == "get_ContentType" ? type : throw new NotImplementedException(method.Name));
            return new(Guid.NewGuid(), element, null, null) { Areas = children.Length == 0 ? [] : [new BlockGridArea(children, "content", 1, 12)] };
        }
        IAppBlockPlugin[] plugins = [new Plugin("publicList", variesByData: true), new Plugin("banner", variesByData: false)];
        Assert.False(AppBlockContent.IsInteractive([Block(AppBlockSchema.FormsContainerAlias, Block(AppBlockSchema.RichTextAlias))], plugins));
        Assert.True(AppBlockContent.IsInteractive([Block(AppBlockSchema.FormsContainerAlias, Block(AppBlockSchema.RegionAlias, Block("appForm")))], plugins));
        Assert.True(AppBlockContent.IsInteractive([Block(AppBlockSchema.TabularAlias)], plugins));
        Assert.True(AppBlockContent.IsInteractive([Block(AppBlockSchema.FormsContainerAlias, Block("publicList"))], plugins));
        Assert.False(AppBlockContent.IsInteractive([Block(AppBlockSchema.FormsContainerAlias, Block("banner"))], plugins));
    }

    private sealed class Plugin(string alias, bool variesByData) : IAppBlockPlugin
    {
        public string Alias => alias;
        public string ViewPath => alias + ".cshtml";
        public bool RequiresUser => false;
        public bool VariesByData => variesByData;
        public void Validate(JsonElement block, List<string> errors) { }
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
        Assert.DoesNotContain(types, t => (string?)t.Attribute("Alias") == "appReport");
        string[] Properties(string alias) => [.. types.Single(t => (string?)t.Attribute("Alias") == alias).Descendants("GenericProperty").Select(p => p.Element("Alias")!.Value)];
        // Everything the data source engine and the block views read. A site's content sets these, and uSync no longer brings them.
        Assert.Contains("paperReport", Properties("appForm"));
        Assert.Contains("sql", Properties("listOfValues"));
        Assert.Contains("connection", Properties("listOfValues"));
        foreach (var property in new[] { "connection", "source", "keyColumns", "mapping", "rowRestriction", "insertDefaults", "auditColumns", "keySequences", "customStatements", "allowAnonymousWrites" })
            Assert.Contains(property, Properties("sqlDataSource"));
        var source = types.Single(type => (string?)type.Attribute("Alias") == "sqlDataSource")
            .Descendants("GenericProperty").Single(property => property.Element("Alias")!.Value == "source");
        Assert.Equal("64d2fba9-1cb2-490b-9de9-37211a072cf4", source.Element("Definition")!.Value);
        var access = types.Single(type => (string?)type.Attribute("Alias") == "sqlDataSource")
            .Descendants("GenericProperty").Single(property => property.Element("Alias")!.Value == "access");
        Assert.True((bool)access.Element("Mandatory")!);
        Assert.Contains("Select an access value", access.Element("MandatoryMessage")!.Value);
        using (var stream = files.GetFileInfo("Schema/DataTypes/AppBlocksTable.config").CreateReadStream()) {
            var picker = XElement.Load(stream);
            Assert.Equal("Umbraco.TextArea", picker.Element("Info")!.Element("EditorAlias")!.Value);
            Assert.Equal("AppBlocks.TablePicker", picker.Element("Info")!.Element("EditorUIAlias")!.Value);
        }
        // The content blocks Block.cshtml renders. Raw HTML and script stay out of the Forms container's grid: a site offers them by adding them to its own.
        var grid = files.GetDirectoryContents("Schema/DataTypes").Single(f => f.Name == "ApplicationApplicationBlocks.config");
        using (var gridStream = grid.CreateReadStream())
        {
            var gridConfig = XElement.Load(gridStream).Element("Config")!.Value;
            foreach (var alias in new[] { AppBlockSchema.HtmlAlias, AppBlockSchema.CssAlias, AppBlockSchema.ScriptAlias })
            {
                var type = types.Single(t => (string?)t.Attribute("Alias") == alias);
                Assert.Equal("true", type.Element("Info")!.Element("IsElement")!.Value);
                Assert.DoesNotContain(type.Attribute("Key")!.Value, gridConfig);
            }
        }
        Assert.DoesNotContain(typeof(AppBlocksExtensions).Assembly.GetReferencedAssemblies(), a => a.Name!.StartsWith("InteractiveReport"));
        Assert.False(files.GetFileInfo("Views/Partials/blockgrid/Components/appReport.cshtml").Exists);
        foreach (var file in files.GetDirectoryContents("Schema/DataTypes").Where(f => !f.IsDirectory)) {
            using var stream = file.CreateReadStream();
            var config = XElement.Load(stream).Element("Config")?.Value;
            if (string.IsNullOrWhiteSpace(config)) continue;
            using var json = JsonDocument.Parse(config);
            Assert.DoesNotContain(AppBlockSchema.Key("appReport").ToString(), config);
        }
    }
}
