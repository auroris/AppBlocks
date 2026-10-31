using System.Text.Json;
using Umbraco.Cms.Core.Services;

namespace AppBlocks.Blocks;

/// <summary>Finds native block values, including block properties inside a site's own element types.</summary>
internal static class BlockValueSets
{
    public static IReadOnlyList<JsonElement> Read(string json, IContentTypeService? types = null)
    {
        using var document = JsonDocument.Parse(json);
        var result = new List<JsonElement>();
        var editors = new Dictionary<Guid, Dictionary<string, string>>();
        Visit(document.RootElement, 0);
        return result;

        void Visit(JsonElement root, int depth)
        {
            if (depth > 32) throw new InvalidOperationException("Block nesting is too deep.");
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("contentData", out var data)
                || !root.TryGetProperty("layout", out var layout)
                || (!layout.TryGetProperty("Umbraco.BlockGrid", out _) && !layout.TryGetProperty("Umbraco.BlockList", out _))) return;
            result.Add(root.Clone());
            foreach (var block in data.EnumerateArray())
            {
                var type = block.GetProperty("contentTypeKey").GetGuid();
                if (!editors.TryGetValue(type, out var properties))
                    editors[type] = properties = types?.Get(type)?.CompositionPropertyTypes
                        .ToDictionary(p => p.Alias, p => p.PropertyEditorAlias) ?? [];
                if (!block.TryGetProperty("values", out var values)) continue;
                foreach (var value in values.EnumerateArray())
                {
                    var alias = value.GetProperty("alias").GetString() ?? "";
                    if (type == AppBlockSchema.Key("appForm") && alias == "formInterface") continue;
                    if (!(type == AppBlockSchema.Key(AppBlockSchema.FormsContainerAlias) && alias == "forms")
                        && !AppBlockSchema.IsBlockEditor(properties.GetValueOrDefault(alias))) continue;
                    if (!value.TryGetProperty("value", out var nested) || nested.ValueKind == JsonValueKind.Null) continue;
                    if (nested.ValueKind == JsonValueKind.String)
                    {
                        if (string.IsNullOrWhiteSpace(nested.GetString())) continue;
                        using var parsed = JsonDocument.Parse(nested.GetString()!);
                        Visit(parsed.RootElement, depth + 1);
                    }
                    else Visit(nested, depth + 1);
                }
            }
        }
    }
}
