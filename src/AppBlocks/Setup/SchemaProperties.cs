using System.Xml.Linq;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;

namespace AppBlocks.Setup;

/// <summary>
/// Builds properties and groups for newly installed definitions from their schema files.
/// </summary>
public sealed class SchemaProperties(IDataTypeService dataTypes, IShortStringHelper strings)
{
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
