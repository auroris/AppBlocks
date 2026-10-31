using System.Net;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Entities;
using Umbraco.Cms.Core.Services;

namespace AppBlocks.Setup;

internal static class SchemaFolders
{
    internal static async Task<EntityContainer?> EnsureAsync<T>(IEntityTypeContainerService<T> folders, string path) where T : ITreeEntity
    {
        EntityContainer? parent = null;
        var level = 1;
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = WebUtility.UrlDecode(segment);
            var folder = (await folders.GetAsync(name, level++)).FirstOrDefault(f => f.ParentId == (parent?.Id ?? -1));
            if (folder is null)
            {
                var result = await folders.CreateAsync(null, name, parent?.Key, Constants.Security.SuperUserKey);
                if (!result.Success || result.Result is null)
                    throw new InvalidOperationException($"AppBlocks could not create schema folder '{path}': {result.Status}.");
                folder = result.Result;
            }
            parent = folder;
        }
        return parent;
    }

    internal static async Task<bool> IsLegacyLocationAsync<T>(IEntityTypeContainerService<T> folders, T entity, string destination) where T : ITreeEntity
    {
        if (entity.ParentId == -1) return true;
        var path = new Stack<string>();
        for (var folder = await folders.GetParentAsync(entity); folder is not null; folder = await folders.GetParentAsync(folder))
            path.Push(folder.Name!);
        var expected = destination.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(WebUtility.UrlDecode).ToArray();
        if (expected.Length == 0 || expected[0] != "AppBlocks") return false;
        expected[0] = "Application";
        return path.SequenceEqual(expected);
    }
}
