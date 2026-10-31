using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Services;

namespace AppBlocks.Setup;

/// <summary>Groups package-owned definitions from their original locations without moving a site's own definitions or folders.</summary>
public sealed class SchemaFolderMigration(IContentTypeService contentTypes, IDataTypeService dataTypes,
    IContentTypeContainerService contentFolders, IDataTypeContainerService dataFolders,
    IEnumerable<AppBlocksSchema> schemas) : IAppBlocksSchemaMigration
{
    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        foreach (var schema in schemas.DistinctBy(s => s.Assembly))
        {
            foreach (var definition in schema.Definitions("DataTypes"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = Guid.Parse(definition.Attribute("Key")!.Value);
                var path = SchemaProperties.Value(definition.Element("Info")!, "Folder");
                if (await dataTypes.GetAsync(key) is not { } type
                    || !await SchemaFolders.IsLegacyLocationAsync(dataFolders, type, path)) continue;
                var folder = await SchemaFolders.EnsureAsync(dataFolders, path);
                var result = await dataTypes.MoveAsync(type, folder?.Key, Constants.Security.SuperUserKey);
                if (!result.Success) throw new InvalidOperationException($"AppBlocks could not move data type '{type.Name}' to '{path}': {result.Status}.");
            }
            foreach (var definition in schema.Definitions("ContentTypes"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = Guid.Parse(definition.Attribute("Key")!.Value);
                var path = SchemaProperties.Value(definition.Element("Info")!, "Folder");
                if (contentTypes.Get(key) is not { } type
                    || !await SchemaFolders.IsLegacyLocationAsync(contentFolders, type, path)) continue;
                var folder = await SchemaFolders.EnsureAsync(contentFolders, path);
                var result = await contentTypes.MoveAsync(key, folder?.Key);
                if (!result.Success) throw new InvalidOperationException($"AppBlocks could not move content type '{type.Alias}' to '{path}': {result.Status}.");
            }
        }
    }
}
