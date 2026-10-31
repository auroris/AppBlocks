using System.Xml.Linq;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;

namespace AppBlocks.Setup;

/// <summary>
/// The schema's properties as Umbraco objects, and the migrations' way to give a site that installed a type from an
/// older schema a property added since. The property's key, name and editor all come from the schema file.
/// </summary>
public sealed class SchemaProperties(IContentTypeService contentTypes, IDataTypeService dataTypes, IShortStringHelper strings,
    IEnumerable<AppBlocksSchema> schemas)
{
    /// <summary>
    /// Adds the named properties to the type the schema installed, when it lacks them. A type a site made itself under
    /// the same alias is not the schema's, and is left alone. So is a companion's type that is not installed.
    /// </summary>
    public async Task AddMissingAsync(string alias, params string[] properties)
    {
        var definition = Definition(alias);
        if (contentTypes.Get(Guid.Parse(definition.Attribute("Key")!.Value)) is not { } type) return;
        var missing = definition.Element("GenericProperties")!.Elements("GenericProperty")
            .Where(p => properties.Contains(Value(p, "Alias")) && !type.PropertyTypeExists(Value(p, "Alias"))).ToArray();
        if (missing.Length == 0) return;
        foreach (var property in missing)
        {
            var group = (string?)property.Element("Tab")?.Attribute("Alias");
            if (!string.IsNullOrEmpty(group) && !type.PropertyGroups.Contains(group))
                type.PropertyGroups.Add(Group(definition.Element("Tabs")!.Elements("Tab").Single(tab => Value(tab, "Alias") == group)));
            Add(type, await PropertyAsync(type.Alias, property), group);
        }
        var result = await contentTypes.UpdateAsync(type, Constants.Security.SuperUserKey);
        if (!result.Success) throw new InvalidOperationException($"AppBlocks could not add {string.Join(", ", missing.Select(p => Value(p, "Alias")))} to '{alias}': {result.Result}.");
    }

    /// <summary>The schema's definition of a content type.</summary>
    public XElement Definition(string alias) => schemas.DistinctBy(s => s.Assembly).SelectMany(s => s.Definitions("ContentTypes"))
        .Single(d => d.Attribute("Alias")!.Value == alias);

    internal async Task<PropertyType> PropertyAsync(string typeAlias, XElement property)
    {
        var dataTypeKey = Guid.Parse(Value(property, "Definition"));
        var dataType = await dataTypes.GetAsync(dataTypeKey)
            ?? throw new InvalidOperationException($"AppBlocks property '{typeAlias}.{Value(property, "Alias")}' requires missing data type '{dataTypeKey}'.");
        return new PropertyType(strings, dataType)
        {
            Key = Guid.Parse(Value(property, "Key")),
            Alias = Value(property, "Alias"),
            Name = Value(property, "Name"),
            Description = Value(property, "Description"),
            SortOrder = (int)property.Element("SortOrder")!,
            Mandatory = (bool)property.Element("Mandatory")!,
            MandatoryMessage = Value(property, "MandatoryMessage"),
            ValidationRegExp = Value(property, "Validation"),
            ValidationRegExpMessage = Value(property, "ValidationRegExpMessage"),
            LabelOnTop = (bool)property.Element("LabelOnTop")!,
            Variations = Enum.Parse<ContentVariation>(Value(property, "Variations")),
        };
    }

    internal static void Add(IContentType type, PropertyType property, string? group)
    {
        if (string.IsNullOrEmpty(group)) type.AddPropertyType(property);
        else type.AddPropertyType(property, group);
    }

    internal static PropertyGroup Group(XElement tab) => new(true)
    {
        Key = Guid.Parse(Value(tab, "Key")),
        Name = Value(tab, "Caption"),
        Alias = Value(tab, "Alias"),
        Type = Enum.Parse<PropertyGroupType>(Value(tab, "Type")),
        SortOrder = (int)tab.Element("SortOrder")!,
    };

    internal static string Value(XElement element, string name) => element.Element(name)?.Value.Trim() ?? "";
}
