using System.Text.Json;
using System.Xml.Linq;
using AppBlocks.Setup;
using Microsoft.Extensions.FileProviders;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Services;
using UmbracoAppHost.Blocks;

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
                using var expected = JsonDocument.Parse(xml.Element("Config")!.Value);
                using var actual = JsonDocument.Parse(JsonSerializer.Serialize(dataType.ConfigurationData));
                Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement), dataType.Name);
            }
        }

        var sources = types.Get("dataSources")!;
        Assert.Equal(new[] { "sqlDataSource", "listOfValues" }, sources.AllowedContentTypes!.OrderBy(c => c.SortOrder).Select(c => c.Alias));
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
        await installer.EnsureAsync(default);
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
        await types.DeleteAsync(replacement.Key, Constants.Security.SuperUserKey);
        await installer.EnsureAsync(default);
        Assert.Equal(beforeTypes, types.GetAll().Select(t => t.Key).Order());
    }

    private sealed class MigrationProbe : IAppBlocksSchemaMigration
    {
        public int Calls { get; private set; }
        public Task EnsureAsync(CancellationToken cancellationToken) { Calls++; return Task.CompletedTask; }
    }

    private sealed class FailingMigration : IAppBlocksSchemaMigration
    {
        public Task EnsureAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Simulated schema migration failure.");
    }
}
