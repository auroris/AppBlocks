using System.Text.Json.Nodes;
using System.Xml.Linq;
using AppBlocks.Connections;
using AppBlocks.Setup;
using Microsoft.Extensions.FileProviders;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using UmbracoAppHost.Blocks;

/// <summary>Optional, demo-only website content. The AppBlocks package supplies no page type or template.</summary>
public sealed class DemoSeed(IConfiguration configuration, IServiceProvider services, IContentTypeService types, IDataTypeService dataTypes,
    IContentService contents, ITemplateService templates, IShortStringHelper strings, DataEditorCollection editors,
    IConfigurationEditorJsonSerializer serializer, IWebHostEnvironment environment, ConnectionCatalog connections)
    : INotificationAsyncHandler<UmbracoApplicationStartedNotification>
{
    public static Guid Key(string value) => AppBlockSchema.Key("standalone-demo:" + value);
    public async Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken ct)
    {
        if (!configuration.GetValue<bool>("AppBlocksDemo:SeedContent")) return;
        if (contents.GetById(Key("page")) is { Published: true } existing) {
            var changed = false;
            foreach (var alias in new[] { "mainContent", "sidebar" }) {
                var value = JsonNode.Parse(existing.GetValue(alias)?.ToString() ?? "{}");
                foreach (var block in value?["contentData"]?.AsArray() ?? [])
                foreach (var property in block?["values"]?.AsArray() ?? [])
                    if (property?["alias"]?.GetValue<string>() == "forms" && property["value"] is JsonObject nested) {
                        property["value"] = nested.ToJsonString(); changed = true;
                    }
                if (changed) existing.SetValue(alias, value!.ToJsonString());
            }
            if (changed) Save(existing);
            return;
        }
        await ActivatorUtilities.CreateInstance<SchemaInstaller>(services).EnsureAsync(ct);
        await using (var connection = connections.Openable("UmbracoDb"))
        {
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE IF NOT EXISTS AppBlocksDemoTasks (id INTEGER PRIMARY KEY AUTOINCREMENT, title TEXT NOT NULL); INSERT INTO AppBlocksDemoTasks (title) SELECT 'A task from a normal content page' WHERE NOT EXISTS (SELECT 1 FROM AppBlocksDemoTasks);";
            await command.ExecuteNonQueryAsync(ct);
        }
        var text = await dataTypes.GetAsync(Guid.Parse("90cb2487-adcb-2ec5-c4a0-840175ba5424")) ?? throw new InvalidOperationException("Missing text data type.");
        var richText = types.Get("demoText") ?? new ContentType(strings, -1) { Key = Key("text-type"), Alias = "demoText", Name = "Demo text", IsElement = true };
        if (richText.Id == 0) { richText.AddPropertyType(new PropertyType(strings, text) { Alias = "text", Name = "Text" }); await types.CreateAsync(richText, Constants.Security.SuperUserKey); }
        var blockKeys = new[] { richText.Key, AppBlockSchema.Key(AppBlockSchema.FormsContainerAlias), AppBlockSchema.Key("appReport") };
        var list = await DataType("list", "Umbraco.BlockList", new BlockListConfiguration {
            Blocks = blockKeys.Select(key => new BlockListConfiguration.BlockConfiguration { ContentElementTypeKey = key }).ToArray(),
        });
        var grid = await DataType("grid", "Umbraco.BlockGrid", new BlockGridConfiguration {
            GridColumns = 12, Blocks = blockKeys.Select(key => new BlockGridConfiguration.BlockGridBlockConfiguration { ContentElementTypeKey = key, AllowAtRoot = true, AllowInAreas = true }).ToArray(),
        });
        var template = await templates.GetAsync("DemoContent");
        if (template is null) {
            template = new Template(strings, "Demo content", "DemoContent") { Content = await System.IO.File.ReadAllTextAsync(Path.Combine(environment.ContentRootPath, "Views", "DemoContent.cshtml"), ct) };
            if (!(await templates.CreateAsync(template, Constants.Security.SuperUserKey)).Success) throw new InvalidOperationException("Could not create demo template.");
        }
        var pageType = types.Get("demoArticle");
        if (pageType is null) {
            pageType = new ContentType(strings, -1) { Key = Key("page-type"), Alias = "demoArticle", Name = "Demo article", AllowedAsRoot = true, AllowedTemplates = [template] };
            pageType.SetDefaultTemplate(template);
            pageType.AddPropertyType(new PropertyType(strings, list) { Alias = "mainContent", Name = "Article content" });
            pageType.AddPropertyType(new PropertyType(strings, grid) { Alias = "sidebar", Name = "Sidebar" });
            if (!(await types.CreateAsync(pageType, Constants.Security.SuperUserKey)).Success) throw new InvalidOperationException("Could not create demo article type.");
        }
#pragma warning disable CS0618
        var root = contents.GetById(Key("sources")) ?? contents.Create("Demo data", -1, "dataSources");
        root.Key = Key("sources"); Save(root);
        var source = contents.GetById(Key("source")) ?? contents.Create("Tasks", root.Id, "sqlDataSource");
        source.Key = Key("source"); source.SetValue("alias", "demo.tasks"); source.SetValue("source", "AppBlocksDemoTasks");
        source.SetValue("keyColumns", "id"); source.SetValue("allowInsert", true); source.SetValue("allowUpdate", true); Save(source);
        var page = contents.GetById(Key("page")) ?? contents.Create("A normal content page", -1, "demoArticle"); page.Key = Key("page"); page.TemplateId = template.Id;
        page.SetValue("mainContent", PageBlocks("Umbraco.BlockList", source.Key, richText.Key, "main").ToJsonString());
        page.SetValue("sidebar", PageBlocks("Umbraco.BlockGrid", source.Key, richText.Key, "side").ToJsonString());
        Save(page);
        void Save(IContent content) {
            if (!contents.Save(content).Success) throw new InvalidOperationException("Could not save demo " + content.Name);
            var result = contents.Publish(content, ["*"]);
            if (!result.Success) throw new InvalidOperationException("Could not publish demo " + content.Name + ": " + string.Join("; ", result.EventMessages?.GetAll().Select(m => m.Message) ?? []));
        }
#pragma warning restore CS0618
    }

    private async Task<IDataType> DataType(string name, string editor, object config)
    {
        if (await dataTypes.GetAsync(Key(name)) is { } found) return found;
        var type = new DataType(editors.First(e => e.Alias == editor), serializer) { Key = Key(name), Name = "Demo " + name, EditorUiAlias = "Umb.PropertyEditorUi." + editor.Split('.')[1] };
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        type.SetConfigurationData(System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(System.Text.Json.JsonSerializer.Serialize(config, options), options)!);
        if (!(await dataTypes.CreateAsync(type, Constants.Security.SuperUserKey)).Success) throw new InvalidOperationException("Could not create demo " + name);
        return type;
    }

    internal static JsonObject PageBlocks(string editor, Guid source, Guid textType, string prefix)
    {
        var body = Set(editor);
        Add(body, editor, Key(prefix + "-text"), textType, ("text", JsonValue.Create("This text belongs to the website. The following forms and reports use native Umbraco blocks.")));
        var report = Add(body, editor, Key(prefix + "-report"), AppBlockSchema.Key("appReport"), ("heading", JsonValue.Create("Tasks")), ("dataSource", JsonValue.Create(source.ToString())), ("pageSize", JsonValue.Create(10)));
        var fields = Set("Umbraco.BlockGrid");
        var iface = Set("Umbraco.BlockList");
        Add(iface, "Umbraco.BlockList", Key(prefix + "-interface"), AppBlockSchema.Key("appFormInterface"), ("resource", JsonValue.Create("demo.tasks")), ("keyParameter", JsonValue.Create(prefix + "Id")));
        var form = Add(fields, "Umbraco.BlockGrid", Key(prefix + "-form"), AppBlockSchema.Key("appForm"), ("heading", JsonValue.Create("Edit task")), ("formInterface", iface));
        var field = Add(fields, "Umbraco.BlockGrid", Key(prefix + "-title"), AppBlockSchema.Key("appText"), ("field", JsonValue.Create("title")), ("label", JsonValue.Create("Task title")));
        fields["layout"]!["Umbraco.BlockGrid"]!.AsArray().Remove(field);
        using var stream = new ManifestEmbeddedFileProvider(typeof(AppBlocks.AppBlocksExtensions).Assembly).GetFileInfo("Schema/DataTypes/ApplicationApplicationBlocks.config").CreateReadStream();
        var config = JsonNode.Parse(XElement.Load(stream).Element("Config")!.Value)!;
        var formConfig = config["blocks"]!.AsArray().Single(b => b!["contentElementTypeKey"]!.GetValue<string>() == AppBlockSchema.Key("appForm").ToString())!;
        form["areas"]!.AsArray().Add(new JsonObject { ["key"] = formConfig["areas"]![0]!["key"]!.DeepClone(), ["items"] = new JsonArray(field) });
        // ContentService.SetValue expects storage values; nested Block Grid properties store serialized JSON.
        Add(body, editor, Key(prefix + "-container"), AppBlockSchema.Key(AppBlockSchema.FormsContainerAlias), ("forms", JsonValue.Create(fields.ToJsonString())));
        return body;
    }

    private static JsonObject Set(string editor) => new() { ["layout"] = new JsonObject { [editor] = new JsonArray() }, ["contentData"] = new JsonArray(), ["settingsData"] = new JsonArray(), ["expose"] = new JsonArray() };
    private static JsonObject Add(JsonObject set, string editor, Guid key, Guid type, params (string Alias, JsonNode? Value)[] values)
    {
        set["contentData"]!.AsArray().Add(new JsonObject { ["key"] = key.ToString(), ["contentTypeKey"] = type.ToString(), ["values"] = new JsonArray(values.Select(v => (JsonNode)new JsonObject { ["alias"] = v.Alias, ["value"] = v.Value, ["culture"] = null, ["segment"] = null }).ToArray()) });
        set["expose"]!.AsArray().Add(new JsonObject { ["contentKey"] = key.ToString(), ["culture"] = null, ["segment"] = null });
        var layout = new JsonObject { ["contentKey"] = key.ToString(), ["settingsKey"] = null, ["columnSpan"] = 12, ["rowSpan"] = 1, ["areas"] = new JsonArray() };
        set["layout"]![editor]!.AsArray().Add(layout); return layout;
    }
}
