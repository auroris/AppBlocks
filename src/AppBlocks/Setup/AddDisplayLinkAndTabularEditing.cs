using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Infrastructure.Migrations;
using AppBlocks.Blocks;
using AppBlocks.Tabular;

namespace AppBlocks.Setup;

/// <summary>
/// Gives an existing site's Display value block its link template (<c>linkTemplate</c>) and its Tabular form block its Without editing
/// setting (<c>hideEdit</c>), as the package's schema declares them, and puts the Tabular form block's settings in the package's order,
/// where Without editing sits between Without adding and Without deleting. Both properties are optional and empty means today's
/// behaviour: a value shown as text, and rows changed where the data source allows it. A type that is not there, as on a fresh site
/// that the installer gives the current definitions, is left alone, as is a property the type already has.
/// </summary>
public sealed class AddDisplayLinkAndTabularEditing(IMigrationContext context, IContentTypeService contentTypes, IDataTypeService dataTypes,
    IDataTypeContainerService dataFolders, PropertyEditorCollection editors, IConfigurationEditorJsonSerializer serializer, IShortStringHelper strings,
    IOptions<PackageMigrationSettings> settings, IKeyValueService keyValues, ICoreScopeProvider scopes)
    : AsyncMigrationBase(context)
{
    internal const string DisplayAlias = "appDisplay";

    protected override async Task MigrateAsync()
    {
        using var scope = scopes.CreateCoreScope();
        // The migration records its state even when no schema change is needed; lock before the first read on SQLite.
        scope.EagerWriteLock(Constants.Locks.KeyValues);
        if (settings.Value.RunSchemaAndContentMigrations && keyValues.GetValue(SchemaInstaller.UninstalledKey) != "true")
        {
            var schema = new AppBlocksSchema(typeof(AddDisplayLinkAndTabularEditing).Assembly);
            await AddAsync(schema, DisplayAlias, AppBlockField.LinkTemplateAlias);
            await AddAsync(schema, AppBlockSchema.TabularAlias, TabularBlock.HideEditAlias);
        }
        scope.Complete();
    }

    /// <summary>Adds <paramref name="alias"/> to the package's type when it lacks it, and gives the package's properties the package's order.</summary>
    private async Task AddAsync(AppBlocksSchema schema, string typeAlias, string alias)
    {
        var definition = schema.Definitions("ContentTypes").Single(d => d.Attribute("Alias")!.Value == typeAlias);
        // The package's type, by key: a site's own type that took the alias is not the package's to change.
        if (contentTypes.Get(Guid.Parse(definition.Attribute("Key")!.Value)) is not { } type) return;
        var properties = definition.Element("GenericProperties")!.Elements("GenericProperty").ToArray();
        var changed = false;
        if (!type.PropertyTypeExists(alias))
        {
            var property = properties.Single(p => SchemaProperties.Value(p, "Alias") == alias);
            var dataTypeKey = Guid.Parse(SchemaProperties.Value(property, "Definition"));
            if (await dataTypes.GetAsync(dataTypeKey) is null)
                await SchemaInstaller.CreateDataTypeAsync(schema.Definitions("DataTypes").Single(d => Guid.Parse(d.Attribute("Key")!.Value) == dataTypeKey),
                    dataTypes, dataFolders, editors, serializer);
            var tab = (string?)property.Element("Tab")?.Attribute("Alias");
            if (!string.IsNullOrEmpty(tab) && type.PropertyGroups.All(group => group.Alias != tab))
                type.PropertyGroups.Add(SchemaProperties.Group(definition.Element("Tabs")!.Elements("Tab").Single(t => SchemaProperties.Value(t, "Alias") == tab)));
            SchemaProperties.Add(type, await new SchemaProperties(dataTypes, strings).PropertyAsync(type.Alias, property), tab);
            changed = true;
        }
        // The new property took a place among the others, so those after it moved down by one.
        foreach (var property in properties)
            if (type.PropertyTypes.FirstOrDefault(p => p.Alias == SchemaProperties.Value(property, "Alias")) is { } existing
                && (int)property.Element("SortOrder")! is var order && existing.SortOrder != order)
            {
                existing.SortOrder = order;
                changed = true;
            }
        if (!changed) return;
        var result = await contentTypes.UpdateAsync(type, Constants.Security.SuperUserKey);
        if (!result.Success) throw new InvalidOperationException($"AppBlocks could not add the {alias} property to {type.Alias}: {result.Result}.");
    }
}
