using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Migrations;

namespace AppBlocks.Setup;

/// <summary>Changes the package's Form Interface editor. Existing development values must be recreated as Single Blocks.</summary>
public sealed class UseSingleBlockFormInterface(IMigrationContext context, ICoreScopeProvider scopes, IDataTypeService dataTypes,
    PropertyEditorCollection editors, IConfigurationEditorJsonSerializer serializer, IOptions<PackageMigrationSettings> settings,
    IKeyValueService keyValues) : AsyncMigrationBase(context)
{
    internal static readonly Guid DataTypeKey = Guid.Parse("e40346b8-4576-8645-4dff-f8032990c127");

    protected override async Task MigrateAsync()
    {
        using var scope = scopes.CreateCoreScope();
        // Completion writes migration state, including when the data type already uses Single Block.
        scope.EagerWriteLock(Constants.Locks.KeyValues);
        if (settings.Value.RunSchemaAndContentMigrations && keyValues.GetValue(SchemaInstaller.UninstalledKey) != "true"
            && await dataTypes.GetAsync(DataTypeKey) is { EditorAlias: Constants.PropertyEditors.Aliases.BlockList } type)
        {
            var definition = new AppBlocksSchema(typeof(UseSingleBlockFormInterface).Assembly).Definitions("DataTypes")
                .Single(d => Guid.Parse(d.Attribute("Key")!.Value) == DataTypeKey);
            var (info, editor, storage, configuration) = SchemaInstaller.Editor(definition, editors, serializer);
            type.Editor = editor;
            type.EditorUiAlias = SchemaProperties.Value(info, "EditorUIAlias");
            type.DatabaseType = storage;
            type.ConfigurationData = configuration;
            var result = await dataTypes.UpdateAsync(type, Constants.Security.SuperUserKey);
            if (!result.Success) throw new InvalidOperationException($"AppBlocks could not switch Form Interface to Single Block: {result.Status}.");
            RebuildCache = true;
        }
        scope.Complete();
    }
}
