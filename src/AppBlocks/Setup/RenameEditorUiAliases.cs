using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Migrations;

namespace AppBlocks.Setup;

/// <summary>
/// Moves an existing site's data types from the editor UI aliases the pickers first had, <c>AppHost.*</c>, to the package's own
/// <c>AppBlocks.*</c>. A data type stores its editor UI alias, and the manifests register only the new ones, so a data type left on
/// an old alias would show no editor. Every data type on an old alias moves, the package's and any a site made from the same editor.
/// </summary>
public sealed class RenameEditorUiAliases(IMigrationContext context, ICoreScopeProvider scopes, IDataTypeService dataTypes,
    IOptions<PackageMigrationSettings> settings, IKeyValueService keyValues) : AsyncMigrationBase(context)
{
    internal static readonly IReadOnlyDictionary<string, string> Renamed = new Dictionary<string, string>
    {
        ["AppHost.DataSourcePicker"] = "AppBlocks.DataSourcePicker",
        ["AppHost.DataSourceTest"] = "AppBlocks.DataSourceTest",
        ["AppHost.FieldPicker"] = "AppBlocks.FieldPicker",
        ["AppHost.ResourcePicker"] = "AppBlocks.ResourcePicker",
        ["AppHost.ListPicker"] = "AppBlocks.ListPicker",
    };

    protected override async Task MigrateAsync()
    {
        using var scope = scopes.CreateCoreScope();
        // Completion writes migration state, including when no data type needs a change.
        scope.EagerWriteLock(Constants.Locks.KeyValues);
        if (settings.Value.RunSchemaAndContentMigrations && keyValues.GetValue(SchemaInstaller.UninstalledKey) != "true")
            foreach (var (old, current) in Renamed)
                foreach (var type in await dataTypes.GetByEditorUiAlias(old))
                {
                    type.EditorUiAlias = current;
                    var result = await dataTypes.UpdateAsync(type, Constants.Security.SuperUserKey);
                    if (!result.Success) throw new InvalidOperationException($"AppBlocks could not move data type '{type.Name}' to the editor {current}: {result.Status}.");
                }
        scope.Complete();
    }
}
