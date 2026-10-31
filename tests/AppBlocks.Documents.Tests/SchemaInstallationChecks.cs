using System.Text.Json;
using System.Xml.Linq;
using AppBlocks.Setup;
using Microsoft.Extensions.FileProviders;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Services;
using AppBlocks.Blocks;

namespace AppBlocks.Documents.Tests;

internal static class SchemaInstallationChecks
{
    public static async Task VerifyAsync(IServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        var services = scope.ServiceProvider;
        var types = services.GetRequiredService<IContentTypeService>();
        var dataTypes = services.GetRequiredService<IDataTypeService>();
        var folders = services.GetRequiredService<IDataTypeContainerService>();
        using var dependencies = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "AppBlocks.Documents.Tests.deps.json")));
        Assert.DoesNotContain(dependencies.RootElement.GetProperty("libraries").EnumerateObject(),
            library => library.Name.StartsWith("uSync", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name!.StartsWith("uSync", StringComparison.OrdinalIgnoreCase));

        foreach (var schema in services.GetServices<AppBlocksSchema>())
        {
            var files = new ManifestEmbeddedFileProvider(schema.Assembly);
            foreach (var file in files.GetDirectoryContents("Schema/ContentTypes").Where(f => !f.IsDirectory))
            {
                using var stream = file.CreateReadStream();
                var xml = XElement.Load(stream);
                var type = types.Get(Guid.Parse(xml.Attribute("Key")!.Value));
                Assert.NotNull(type);
                Assert.Equal(xml.Attribute("Alias")!.Value, type.Alias);
                Assert.Equal((bool)xml.Element("Info")!.Element("IsElement")!, type.IsElement);
                var properties = xml.Element("GenericProperties")!.Elements("GenericProperty").ToArray();
                Assert.Equal(properties.Length, type.PropertyTypes.Count());
                foreach (var property in properties)
                {
                    var saved = Assert.Single(type.PropertyTypes, p => p.Alias == property.Element("Alias")!.Value);
                    Assert.Equal(Guid.Parse(property.Element("Key")!.Value), saved.Key);
                    Assert.Equal((bool)property.Element("Mandatory")!, saved.Mandatory);
                    Assert.Equal((int)property.Element("SortOrder")!, saved.SortOrder);
                    Assert.Equal((await dataTypes.GetAsync(Guid.Parse(property.Element("Definition")!.Value)))!.Id, saved.DataTypeId);
                    var group = type.PropertyGroups.Single(g => g.Alias == (string?)property.Element("Tab")!.Attribute("Alias"));
                    Assert.Contains(group.PropertyTypes!, p => p.Key == saved.Key);
                }
                foreach (var tab in xml.Element("Tabs")!.Elements("Tab"))
                    Assert.Equal(Guid.Parse(tab.Element("Key")!.Value), type.PropertyGroups.Single(g => g.Alias == tab.Element("Alias")!.Value).Key);
            }
            foreach (var file in files.GetDirectoryContents("Schema/DataTypes").Where(f => !f.IsDirectory))
            {
                using var stream = file.CreateReadStream();
                var xml = XElement.Load(stream);
                var dataType = await dataTypes.GetAsync(Guid.Parse(xml.Attribute("Key")!.Value));
                Assert.NotNull(dataType);
                Assert.Equal(xml.Element("Info")!.Element("EditorAlias")!.Value, dataType.EditorAlias);
                Assert.Equal(xml.Element("Info")!.Element("EditorUIAlias")!.Value, dataType.EditorUiAlias);
                Assert.Equal(ValueTypes.ToStorageType(dataType.Editor!.GetValueEditor().ValueType), dataType.DatabaseType);
                using var expected = JsonDocument.Parse(xml.Element("Config")!.Value);
                using var actual = JsonDocument.Parse(JsonSerializer.Serialize(dataType.ConfigurationData));
                Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement), dataType.Name);
            }
        }

        var sources = types.Get("dataSources")!;
        Assert.Equal(new[] { "sqlDataSource", "listOfValues" }, sources.AllowedContentTypes!.OrderBy(c => c.SortOrder).Select(c => c.Alias));
        await VerifyRowSourceUpgradeAsync(services);
        await SchemaFolderChecks.VerifyAsync(services);
        var grid = Assert.Single(await dataTypes.GetByEditorAliasAsync("Umbraco.BlockGrid"), d => d.Name == "AppBlocks: Forms container");
        var configuration = Assert.IsType<BlockGridConfiguration>(grid.ConfigurationObject);
        Assert.Contains(configuration.Blocks, b => b.ContentElementTypeKey == AppBlockSchema.Key("appForm"));
        Assert.Contains(configuration.Blocks, b => b.Areas.Length > 0);
        Assert.Single(await folders.GetAllAsync(), f => f.Name == "Data sources"); // Encoded and plain paths share one folder.

        var form = types.Get("appForm")!;
        var formId = form.Id;
        form.Name = "Site customized form";
        form.PropertyTypes.Single(p => p.Alias == "heading").Name = "Site heading";
        Assert.True((await types.UpdateAsync(form, Constants.Security.SuperUserKey)).Success);
        grid.Name = "Site customized grid";
        grid.ConfigurationData = new Dictionary<string, object> { ["gridColumns"] = 6, ["blocks"] = Array.Empty<object>() };
        Assert.True((await dataTypes.UpdateAsync(grid, Constants.Security.SuperUserKey)).Success);
        var beforeTypes = types.GetAll().Select(t => t.Key).Order().ToArray();
        var beforeData = (await dataTypes.GetAllAsync()).Select(t => t.Key).Order().ToArray();
        var beforeFolders = (await folders.GetAllAsync()).Select(t => t.Key).Order().ToArray();
        var migration = new MigrationProbe();
        var installer = ActivatorUtilities.CreateInstance<SchemaInstaller>(services, (object)new IAppBlocksSchemaMigration[] { migration });
        var runtime = services.GetRequiredService<IRuntimeState>();
        var level = runtime.Level;
        var reason = runtime.Reason;
        try {
            // Hosts must still be able to install definitions explicitly before their import, outside Run.
            runtime.Configure(RuntimeLevel.Upgrade, reason);
            await installer.EnsureAsync(default);
        } finally { runtime.Configure(level, reason); }
        await installer.EnsureAsync(default);
        Assert.Equal(2, migration.Calls); // Migrations also run when all definitions already exist.
        Assert.Equal(beforeTypes, types.GetAll().Select(t => t.Key).Order());
        Assert.Equal(beforeData, (await dataTypes.GetAllAsync()).Select(t => t.Key).Order());
        Assert.Equal(beforeFolders, (await folders.GetAllAsync()).Select(t => t.Key).Order());
        Assert.Equal(formId, types.Get(form.Key)!.Id);
        Assert.Equal("Site customized form", types.Get(form.Key)!.Name);
        Assert.Equal("Site heading", types.Get(form.Key)!.PropertyTypes.Single(p => p.Alias == "heading").Name);
        Assert.Equal("Site customized grid", (await dataTypes.GetAsync(grid.Key))!.Name);
        Assert.Equal(6, Assert.IsType<BlockGridConfiguration>((await dataTypes.GetAsync(grid.Key))!.ConfigurationObject).GridColumns);

        // A site installed from an earlier schema gets what was added to it since, with its own changes left alone.
        var forms = types.Get("appForm")!;
        forms.RemovePropertyType("paperReport");
        Assert.True((await types.UpdateAsync(forms, Constants.Security.SuperUserKey)).Success);
        var dataSource = types.Get("sqlDataSource")!;
        dataSource.RemovePropertyType("mapping");
        dataSource.RemovePropertyType("customStatements");
        Assert.True((await types.UpdateAsync(dataSource, Constants.Security.SuperUserKey)).Success);
        await ActivatorUtilities.CreateInstance<SchemaInstaller>(services).EnsureAsync(default);
        Assert.Equal(Guid.Parse("a0d3f966-38f3-f7f4-5bdb-774a158efab3"), types.Get("appForm")!.PropertyTypes.Single(p => p.Alias == "paperReport").Key);
        Assert.Equal("Site heading", types.Get("appForm")!.PropertyTypes.Single(p => p.Alias == "heading").Name);
        Assert.Contains(types.Get("sqlDataSource")!.PropertyGroups["advanced"].PropertyTypes!, p => p.Alias == "customStatements");
        Assert.Contains(types.Get("sqlDataSource")!.PropertyTypes, p => p.Alias == "mapping");

        // There is no document content yet, so exercise a missing companion definition and a failed installation.
        var document = types.Get("paperReport")!;
        await types.DeleteAsync(document.Key, Constants.Security.SuperUserKey);
        Assert.Null(types.Get(document.Key));
        var failing = ActivatorUtilities.CreateInstance<SchemaInstaller>(services, (object)new IAppBlocksSchemaMigration[] { new FailingMigration() });
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.EnsureAsync(default));
        Assert.Null(types.Get(document.Key)); // The type created before the migration failed must have rolled back.
        await installer.EnsureAsync(default);
        Assert.NotNull(types.Get(document.Key));

        await types.DeleteAsync(document.Key, Constants.Security.SuperUserKey);
        var replacement = new Umbraco.Cms.Core.Models.ContentType(services.GetRequiredService<Umbraco.Cms.Core.Strings.IShortStringHelper>(), -1)
        {
            Key = Guid.NewGuid(), Alias = "paperReport", Name = "Site document", AllowedAsRoot = true,
        };
        Assert.True((await types.CreateAsync(replacement, Constants.Security.SuperUserKey)).Success);
        await installer.EnsureAsync(default);
        Assert.Null(types.Get(document.Key));
        Assert.Equal(replacement.Key, types.Get("paperReport")!.Key);
        Assert.Equal("Site document", types.Get("paperReport")!.Name);
        Assert.Empty(types.Get("paperReport")!.PropertyTypes);
        Assert.Equal(-1, types.Get("paperReport")!.ParentId);
        await types.DeleteAsync(replacement.Key, Constants.Security.SuperUserKey);
        await installer.EnsureAsync(default);
        Assert.Equal(beforeTypes, types.GetAll().Select(t => t.Key).Order());
    }

    private sealed class MigrationProbe : IAppBlocksSchemaMigration
    {
        public int Calls { get; private set; }
        public Task EnsureAsync(CancellationToken cancellationToken) { Calls++; return Task.CompletedTask; }
    }

    private static async Task VerifyRowSourceUpgradeAsync(IServiceProvider services)
    {
        var types = services.GetRequiredService<IContentTypeService>();
        var dataTypes = services.GetRequiredService<IDataTypeService>();
        var contents = services.GetRequiredService<IContentService>();
        var editors = services.GetRequiredService<PropertyEditorCollection>();
        await using (var connection = services.GetRequiredService<AppBlocks.Connections.ConnectionCatalog>().Openable("AppBlocksDb")) {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE PickerUpgradeOrders (id INTEGER PRIMARY KEY, title TEXT)";
            await command.ExecuteNonQueryAsync();
        }
        var pickerKey = Guid.Parse("64d2fba9-1cb2-490b-9de9-37211a072cf4");
        var multilineKey = Guid.Parse("306b51cd-102b-ad7d-459c-be97ee8778e7");
        var picker = (await dataTypes.GetAsync(pickerKey))!;
        Assert.Equal(Umbraco.Cms.Core.Models.ValueStorageType.Ntext, picker.DatabaseType);
        Assert.Equal(pickerKey, types.Get("sqlDataSource")!.PropertyTypes.Single(p => p.Alias == "source").DataTypeKey);

        // Simulate the original table picker and save both a published value and a different draft.
        picker.Editor = editors.Single(editor => editor.Alias == "Umbraco.TextBox");
        picker.DatabaseType = Umbraco.Cms.Core.Models.ValueStorageType.Nvarchar;
        Assert.True((await dataTypes.UpdateAsync(picker, Constants.Security.SuperUserKey)).Success);
#pragma warning disable CS0618
        var parent = contents.Create("Picker upgrade sources", -1, "dataSources");
        Assert.True(contents.Save(parent).Success);
        Assert.True(contents.Publish(parent, ["*"]).Success);
        var node = contents.Create("Picker upgrade source", parent.Id, "sqlDataSource");
        node.SetValue("alias", "picker.upgrade"); node.SetValue("keyColumns", "id");
        node.SetValue("source", "PickerUpgradeOrders");
        Assert.True(contents.Save(node).Success);
        var published = contents.Publish(node, ["*"]);
        Assert.True(published.Success, published.Result + ": " + string.Join("; ", published.EventMessages?.GetAll().Select(message => message.Message) ?? []));
        node.SetValue("source", "SELECT id FROM PickerUpgradeOrders");
        Assert.True(contents.Save(node).Success);
        var versions = contents.GetVersions(node.Id).ToDictionary(version => version.VersionId, version => version.GetValue<string>("source"));
        Assert.Contains("PickerUpgradeOrders", versions.Values);
        Assert.Contains("SELECT id FROM PickerUpgradeOrders", versions.Values);
        var installer = services.GetRequiredService<SchemaInstaller>();
        await installer.EnsureAsync(default);
        await installer.EnsureAsync(default);
        Assert.Equal(Umbraco.Cms.Core.Models.ValueStorageType.Ntext, (await dataTypes.GetAsync(pickerKey))!.DatabaseType);
        Assert.Equal("Umbraco.TextArea", (await dataTypes.GetAsync(pickerKey))!.EditorAlias);
        foreach (var version in contents.GetVersions(node.Id)) Assert.Equal(versions[version.VersionId], version.GetValue<string>("source"));

        // A site installed with the plain multiline source also regains the picker without moving its value.
        var sourceType = types.Get("sqlDataSource")!;
        var sourceProperty = sourceType.PropertyTypes.Single(property => property.Alias == "source");
        var multiline = (await dataTypes.GetAsync(multilineKey))!;
        sourceProperty.DataTypeId = multiline.Id; sourceProperty.DataTypeKey = multiline.Key;
        Assert.True((await types.UpdateAsync(sourceType, Constants.Security.SuperUserKey)).Success);
        const string prefix = "SELECT id FROM PickerUpgradeOrders /*";
        var sql = prefix + new string('x', 20000 - prefix.Length - 3) + " */";
        Assert.Equal(AppBlocks.DataSources.DataSourceDefinition.MaxSourceLength, sql.Length);
        node = contents.GetById(node.Id)!;
        node.SetValue("source", sql);
        Assert.True(contents.Save(node).Success);
        await installer.EnsureAsync(default);
        Assert.Equal(pickerKey, types.Get("sqlDataSource")!.PropertyTypes.Single(property => property.Alias == "source").DataTypeKey);
        Assert.Equal(sql, contents.GetById(node.Id)!.GetValue<string>("source"));
        Assert.True(contents.Publish(contents.GetById(node.Id)!, ["*"]).Success);
        Assert.Equal(sql, contents.GetById(node.Id)!.GetValue<string>("source"));

        // A site's explicit alternative editor remains its own choice.
        sourceType = types.Get("sqlDataSource")!;
        sourceProperty = sourceType.PropertyTypes.Single(property => property.Alias == "source");
        var custom = (await dataTypes.GetAsync(Guid.Parse("90cb2487-adcb-2ec5-c4a0-840175ba5424")))!;
        sourceProperty.DataTypeId = custom.Id; sourceProperty.DataTypeKey = custom.Key;
        Assert.True((await types.UpdateAsync(sourceType, Constants.Security.SuperUserKey)).Success);
        await installer.EnsureAsync(default);
        Assert.Equal(custom.Key, types.Get("sqlDataSource")!.PropertyTypes.Single(property => property.Alias == "source").DataTypeKey);
        sourceType = types.Get("sqlDataSource")!;
        sourceProperty = sourceType.PropertyTypes.Single(property => property.Alias == "source");
        sourceProperty.DataTypeId = picker.Id; sourceProperty.DataTypeKey = picker.Key;
        Assert.True((await types.UpdateAsync(sourceType, Constants.Security.SuperUserKey)).Success);
        contents.Delete(parent);
#pragma warning restore CS0618
    }

    private sealed class FailingMigration : IAppBlocksSchemaMigration
    {
        public Task EnsureAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Simulated schema migration failure.");
    }
}
