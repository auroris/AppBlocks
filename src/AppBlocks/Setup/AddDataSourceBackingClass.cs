using Umbraco.Cms.Core;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Infrastructure.Migrations;
using AppBlocks.DataSources;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Scoping;

namespace AppBlocks.Setup;

/// <summary>
/// Gives an existing site's SQL data source type its optional backing class: the <c>handler</c> property on the Advanced tab, and
/// the "Application: Backing class" data type its picker needs when the site lacks it, both as the package's schema declares them.
/// Does nothing when the package's <c>sqlDataSource</c> type is not there, as on a fresh site, which the installer gives the current
/// definitions, or when the type already has the property.
/// </summary>
public sealed class AddDataSourceBackingClass(IMigrationContext context, IContentTypeService contentTypes, IDataTypeService dataTypes,
    IDataTypeContainerService dataFolders, PropertyEditorCollection editors, IConfigurationEditorJsonSerializer serializer, IShortStringHelper strings,
    IOptions<PackageMigrationSettings> settings, IKeyValueService keyValues, ICoreScopeProvider scopes)
    : AsyncMigrationBase(context)
{
    internal const string Property = "handler";

    protected override async Task MigrateAsync()
    {
        using var scope = scopes.CreateCoreScope();
        // The migration records its state even when no schema change is needed; lock before the first read on SQLite.
        scope.EagerWriteLock(Constants.Locks.KeyValues);
        await UpdateAsync();
        scope.Complete();
    }

    private async Task UpdateAsync()
    {
        if (!settings.Value.RunSchemaAndContentMigrations || keyValues.GetValue(SchemaInstaller.UninstalledKey) == "true") return;
        var schema = new AppBlocksSchema(typeof(AddDataSourceBackingClass).Assembly);
        var definition = schema.Definitions("ContentTypes").Single(d => d.Attribute("Alias")!.Value == DataSourceSchema.SqlAlias);
        // The package's type, by key: a site's own type that took the alias is not the package's to change.
        if (contentTypes.Get(Guid.Parse(definition.Attribute("Key")!.Value)) is not { } type || type.PropertyTypeExists(Property)) return;
        var property = definition.Element("GenericProperties")!.Elements("GenericProperty").Single(p => SchemaProperties.Value(p, "Alias") == Property);
        var dataTypeKey = Guid.Parse(SchemaProperties.Value(property, "Definition"));
        if (await dataTypes.GetAsync(dataTypeKey) is null)
            await SchemaInstaller.CreateDataTypeAsync(schema.Definitions("DataTypes").Single(d => Guid.Parse(d.Attribute("Key")!.Value) == dataTypeKey),
                dataTypes, dataFolders, editors, serializer);
        var tab = (string?)property.Element("Tab")?.Attribute("Alias");
        if (!string.IsNullOrEmpty(tab) && type.PropertyGroups.All(group => group.Alias != tab))
            type.PropertyGroups.Add(SchemaProperties.Group(definition.Element("Tabs")!.Elements("Tab").Single(t => SchemaProperties.Value(t, "Alias") == tab)));
        SchemaProperties.Add(type, await new SchemaProperties(dataTypes, strings).PropertyAsync(type.Alias, property), tab);
        var result = await contentTypes.UpdateAsync(type, Constants.Security.SuperUserKey);
        if (!result.Success) throw new InvalidOperationException($"AppBlocks could not add the backing class property to {type.Alias}: {result.Result}.");
    }
}
