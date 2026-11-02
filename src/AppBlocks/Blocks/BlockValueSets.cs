using System.Globalization;
using System.Text.Json;
using Umbraco.Cms.Core.Models.Blocks;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Serialization;

namespace AppBlocks.Blocks;

/// <summary>Native block values, including block properties inside a site's own element types.</summary>
internal static class BlockValueSets
{
    // Also used by standalone sample generation, where no Umbraco service scope exists.
    private static readonly IJsonSerializer Serializer = new SystemTextJsonSerializer(new DefaultJsonSerializerEncoderFactory());
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static BlockValue Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var layout = document.RootElement.GetProperty("layout");
        if (layout.TryGetProperty("Umbraco.BlockGrid", out _)) return new BlockGridEditorDataConverter(Serializer).Deserialize(json).BlockValue;
        if (layout.TryGetProperty("Umbraco.BlockList", out _)) return new BlockListEditorDataConverter(Serializer).Deserialize(json).BlockValue;
        if (layout.TryGetProperty("Umbraco.SingleBlock", out _)) return new SingleBlockEditorDataConverter(Serializer).Deserialize(json).BlockValue;
        throw new JsonException("Unsupported block editor layout.");
    }

    public static IEnumerable<IBlockLayoutItem> Layout(BlockValue value) => value.Layout.TryGetValue(value.PropertyEditorAlias, out var layout) ? layout : [];
    public static IEnumerable<IBlockLayoutItem> Children(IBlockLayoutItem item) => item is BlockGridLayoutItem grid
        ? grid.Areas.SelectMany(area => area.Items) : [];
    public static string Text(BlockItemData block, string alias) => ValueText(block.Values.FirstOrDefault(v => v.Alias == alias)?.Value);
    private static string ValueText(object? input) => input switch
    {
        null or JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => "",
        JsonElement value => value.ToString(),
        var value => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
    };
    // Keep the public plugin validation contract while AppBlocks itself works with typed values.
    public static JsonElement Element(BlockItemData block) => JsonSerializer.SerializeToElement(block, Json);

    public static IReadOnlyList<BlockValue> Read(string json, IContentTypeService? types = null)
    {
        var result = new List<BlockValue>();
        var editors = new Dictionary<Guid, Dictionary<string, string>>();
        Visit(Parse(json), 0);
        return result;

        void Visit(BlockValue value, int depth)
        {
            if (depth > 32) throw new InvalidOperationException("Block nesting is too deep.");
            result.Add(value);
            foreach (var block in value.ContentData.Concat(value.SettingsData))
            {
                if (!editors.TryGetValue(block.ContentTypeKey, out var properties))
                    editors[block.ContentTypeKey] = properties = types?.Get(block.ContentTypeKey)?.CompositionPropertyTypes
                        .ToDictionary(p => p.Alias, p => p.PropertyEditorAlias) ?? [];
                foreach (var property in block.Values)
                {
                    if (block.ContentTypeKey == AppBlockSchema.Key("appForm") && property.Alias == "formInterface") continue;
                    if (!(block.ContentTypeKey == AppBlockSchema.Key(AppBlockSchema.FormsContainerAlias) && property.Alias == "forms")
                        && !AppBlockSchema.IsBlockEditor(properties.GetValueOrDefault(property.Alias))) continue;
                    var nested = ValueText(property.Value);
                    if (!string.IsNullOrWhiteSpace(nested)) Visit(Parse(nested), depth + 1);
                }
            }
        }
    }
}
