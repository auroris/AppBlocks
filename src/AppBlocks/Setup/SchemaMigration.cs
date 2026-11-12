using System.Xml.Linq;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;

namespace AppBlocks.Setup;

/// <summary>
/// Adds a package's schema properties to a site's existing type through Umbraco services, preserving properties already on the type.
/// A companion's package migration step takes it from dependency injection.
/// </summary>
public sealed class SchemaMigration(IContentTypeService contentTypes, IDataTypeService dataTypes, IDataTypeContainerService dataFolders,
    PropertyEditorCollection editors, IConfigurationEditorJsonSerializer serializer, IShortStringHelper strings, IEnumerable<AppBlocksSchema> schemas)
{
    /// <summary>
    /// Adds the properties of <paramref name="aliases"/> that <paramref name="type"/> lacks, as <paramref name="definition"/> declares
    /// them, with their tabs and with the data types the site lacks, from the schema of AppBlocks or of any companion that registers one.
    /// </summary>
    public async Task AddMissingAsync(IContentType type, XElement definition, IEnumerable<string> aliases)
    {
        var missing = aliases.Where(alias => !type.PropertyTypeExists(alias)).ToArray();
        if (missing.Length == 0) return;
        var dataTypeDefinitions = schemas.SelectMany(schema => schema.Definitions("DataTypes")).ToArray();
        foreach (var alias in missing)
        {
            var property = definition.Element("GenericProperties")!.Elements("GenericProperty").Single(p => SchemaProperties.Value(p, "Alias") == alias);
            var dataTypeKey = Guid.Parse(SchemaProperties.Value(property, "Definition"));
            if (await dataTypes.GetAsync(dataTypeKey) is null)
                await SchemaInstaller.CreateDataTypeAsync(dataTypeDefinitions.First(d => Guid.Parse(d.Attribute("Key")!.Value) == dataTypeKey),
                    dataTypes, dataFolders, editors, serializer);
            var tab = (string?)property.Element("Tab")?.Attribute("Alias");
            if (!string.IsNullOrEmpty(tab) && type.PropertyGroups.All(group => group.Alias != tab))
                type.PropertyGroups.Add(SchemaProperties.Group(definition.Element("Tabs")!.Elements("Tab").Single(t => SchemaProperties.Value(t, "Alias") == tab)));
            SchemaProperties.Add(type, await new SchemaProperties(dataTypes, strings).PropertyAsync(type.Alias, property), tab);
        }
        var result = await contentTypes.UpdateAsync(type, Constants.Security.SuperUserKey);
        if (!result.Success) throw new InvalidOperationException($"AppBlocks could not add {string.Join(", ", missing)} to {type.Alias}: {result.Result}.");
    }
}
