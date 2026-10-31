using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using AppBlocks.Setup;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Entities;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;

namespace AppBlocks.Documents.Tests;

internal static class SchemaFolderChecks
{
    public static async Task VerifyAsync(IServiceProvider services)
    {
        var types = services.GetRequiredService<IContentTypeService>();
        var data = services.GetRequiredService<IDataTypeService>();
        var contentFolders = services.GetRequiredService<IContentTypeContainerService>();
        var dataFolders = services.GetRequiredService<IDataTypeContainerService>();
        var schemas = services.GetServices<AppBlocksSchema>().ToArray();
        var typeDefinitions = schemas.SelectMany(schema => schema.Definitions("ContentTypes")).ToArray();
        var dataDefinitions = schemas.SelectMany(schema => schema.Definitions("DataTypes")).ToArray();

        // Every core and companion definition installs in the declared AppBlocks folder on a fresh site.
        foreach (var definition in typeDefinitions)
            await AssertFolderAsync(contentFolders, types.Get(Key(definition))!, Folder(definition));
        foreach (var definition in dataDefinitions)
            await AssertFolderAsync(dataFolders, (await data.GetAsync(Key(definition)))!, Folder(definition));
        Assert.DoesNotContain(await dataFolders.GetAllAsync(), folder => folder.Name == "Application");
        Assert.DoesNotContain(await contentFolders.GetAllAsync(), folder => folder.Name == "Application");

        async Task<EntityContainer> DataFolder(string name, Guid? parent = null)
        {
            var result = await dataFolders.CreateAsync(null, name, parent, Constants.Security.SuperUserKey);
            Assert.True(result.Success); return result.Result!;
        }
        async Task<EntityContainer> ContentFolder(string name)
        {
            var result = await contentFolders.CreateAsync(null, name, null, Constants.Security.SuperUserKey);
            Assert.True(result.Success); return result.Result!;
        }
        var oldData = await DataFolder("Application");
        var oldForms = await DataFolder("Forms", oldData.Key);
        var oldTypes = await ContentFolder("Application");
        var customDataFolder = await DataFolder("Site types");
        var customTypeFolder = await ContentFolder("Site types");
        var text = (await data.GetAsync("Application: Text"))!;
        var formClass = (await data.GetByEditorUiAlias("AppBlocks.FormClassPicker")).Single();
        var connection = (await data.GetAsync("AppBlocks: Connection"))!;
        var picker = (await data.GetAsync("AppBlocks: Table"))!;
        Assert.True((await data.MoveAsync(text, oldData.Key, Constants.Security.SuperUserKey)).Success);
        Assert.True((await data.MoveAsync(formClass, oldForms.Key, Constants.Security.SuperUserKey)).Success);
        Assert.True((await data.MoveAsync(connection, null, Constants.Security.SuperUserKey)).Success);
        Assert.True((await data.MoveAsync(picker, customDataFolder.Key, Constants.Security.SuperUserKey)).Success);
        var appText = types.Get("appText")!;
        appText.Name = "Site customized text block";
        Assert.True((await types.UpdateAsync(appText, Constants.Security.SuperUserKey)).Success);
        Assert.True((await types.MoveAsync(appText.Key, oldTypes.Key)).Success);
        var document = types.Get("paperReport")!;
        Assert.True((await types.MoveAsync(document.Key, null)).Success);
        var date = types.Get("appDate")!;
        Assert.True((await types.MoveAsync(date.Key, customTypeFolder.Key)).Success);

        // Sharing a legacy folder does not make a site's own definitions package-owned.
        var siteType = new ContentType(services.GetRequiredService<IShortStringHelper>(), oldTypes.Id) {
            Alias = "siteFolderContent", Name = "Site content", Key = Guid.NewGuid(),
        };
        Assert.True((await types.CreateAsync(siteType, Constants.Security.SuperUserKey)).Success);
        var siteData = new DataType(services.GetRequiredService<PropertyEditorCollection>().Single(editor => editor.Alias == "Umbraco.TextBox"),
            services.GetRequiredService<IConfigurationEditorJsonSerializer>(), oldForms.Id) { Name = "Site text", Key = Guid.NewGuid() };
        Assert.True((await data.CreateAsync(siteData, Constants.Security.SuperUserKey)).Success);
        var ownedTypes = typeDefinitions.Select(Key).ToHashSet();
        var ownedData = dataDefinitions.Select(Key).ToHashSet();
        var untouchedTypes = types.GetAll().Where(type => !ownedTypes.Contains(type.Key)).ToDictionary(type => type.Key, type => type.ParentId);
        var untouchedData = (await data.GetAllAsync()).Where(type => !ownedData.Contains(type.Key)).ToDictionary(type => type.Key, type => type.ParentId);
        var typeIds = types.GetAll().ToDictionary(type => type.Key, type => type.Id);
        var dataIds = (await data.GetAllAsync()).ToDictionary(type => type.Key, type => type.Id);
        var configuration = JsonSerializer.Serialize(text.ConfigurationData);

        var installer = services.GetRequiredService<SchemaInstaller>();
        await installer.EnsureAsync(default);
        var folderKeys = (await dataFolders.GetAllAsync()).Concat(await contentFolders.GetAllAsync()).Select(folder => folder.Key).Order().ToArray();
        await installer.EnsureAsync(default);
        Assert.Equal(folderKeys, (await dataFolders.GetAllAsync()).Concat(await contentFolders.GetAllAsync()).Select(folder => folder.Key).Order());
        await AssertFolderAsync(dataFolders, (await data.GetAsync(text.Key))!, "AppBlocks");
        await AssertFolderAsync(dataFolders, (await data.GetAsync(formClass.Key))!, "AppBlocks/Forms");
        await AssertFolderAsync(dataFolders, (await data.GetAsync(connection.Key))!, "AppBlocks");
        await AssertFolderAsync(contentFolders, types.Get(appText.Key)!, "AppBlocks");
        await AssertFolderAsync(contentFolders, types.Get(document.Key)!, "AppBlocks");
        Assert.Equal("Site customized text block", types.Get(appText.Key)!.Name);
        Assert.Equal(configuration, JsonSerializer.Serialize((await data.GetAsync(text.Key))!.ConfigurationData));
        Assert.Equal(customDataFolder.Id, (await data.GetAsync(picker.Key))!.ParentId);
        Assert.Equal(customTypeFolder.Id, types.Get(date.Key)!.ParentId);
        foreach (var (key, parent) in untouchedData) Assert.Equal(parent, (await data.GetAsync(key))!.ParentId);
        foreach (var (key, parent) in untouchedTypes) Assert.Equal(parent, types.Get(key)!.ParentId);
        Assert.Equal(typeIds.OrderBy(pair => pair.Key), types.GetAll().ToDictionary(type => type.Key, type => type.Id).OrderBy(pair => pair.Key));
        Assert.Equal(dataIds.OrderBy(pair => pair.Key), (await data.GetAllAsync()).ToDictionary(type => type.Key, type => type.Id).OrderBy(pair => pair.Key));
    }

    private static Guid Key(XElement definition) => Guid.Parse(definition.Attribute("Key")!.Value);
    private static string Folder(XElement definition) => definition.Element("Info")!.Element("Folder")!.Value;
    private static async Task AssertFolderAsync<T>(IEntityTypeContainerService<T> folders, T entity, string expected) where T : ITreeEntity
    {
        Assert.True(expected == "AppBlocks" || expected.StartsWith("AppBlocks/", StringComparison.Ordinal));
        var names = new Stack<string>();
        for (var folder = await folders.GetParentAsync(entity); folder is not null; folder = await folders.GetParentAsync(folder))
            names.Push(folder.Name!);
        Assert.Equal(expected.Split('/').Select(WebUtility.UrlDecode), names);
    }
}
