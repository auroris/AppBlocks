using System.Net;
using System.Xml.Linq;
using AppBlocks.Setup;
using Umbraco.Cms.Core.Models.Entities;
using Umbraco.Cms.Core.Services;

namespace AppBlocks.Documents.Tests;

internal static class SchemaFolderChecks
{
    public static async Task VerifyAsync(IServiceProvider services)
    {
        var types = services.GetRequiredService<IContentTypeService>();
        var data = services.GetRequiredService<IDataTypeService>();
        var contentFolders = services.GetRequiredService<IContentTypeContainerService>();
        var dataFolders = services.GetRequiredService<IDataTypeContainerService>();

        foreach (var schema in services.GetServices<AppBlocksSchema>())
        {
            foreach (var definition in schema.Definitions("ContentTypes"))
                await AssertFolderAsync(contentFolders, types.Get(Key(definition))!, Folder(definition));
            foreach (var definition in schema.Definitions("DataTypes"))
                await AssertFolderAsync(dataFolders, (await data.GetAsync(Key(definition)))!, Folder(definition));
        }
        Assert.DoesNotContain(await dataFolders.GetAllAsync(), folder => folder.Name == "Application");
        Assert.DoesNotContain(await contentFolders.GetAllAsync(), folder => folder.Name == "Application");

        var folderKeys = (await dataFolders.GetAllAsync()).Concat(await contentFolders.GetAllAsync()).Select(folder => folder.Key).Order().ToArray();
        var installer = services.GetRequiredService<SchemaInstaller>();
        await installer.EnsureAsync(default);
        await installer.EnsureAsync(default);
        Assert.Equal(folderKeys, (await dataFolders.GetAllAsync()).Concat(await contentFolders.GetAllAsync()).Select(folder => folder.Key).Order());
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
