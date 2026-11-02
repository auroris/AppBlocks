using System.Text.Json;
using AppBlocks.Blocks;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace AppBlocks.Setup;

/// <summary>Checks saved values too, including drafts, published values, the recycle bins and the element library.</summary>
public sealed class SchemaUsage(IEntityService entities, IContentService documents, IMediaService media,
    IMemberService members, IElementService elements, IContentTypeService types)
{
    public IEnumerable<string> Find(ISet<Guid> definitions, CancellationToken ct)
    {
        foreach (var kind in new[] { UmbracoObjectTypes.Document, UmbracoObjectTypes.Media, UmbracoObjectTypes.Member, UmbracoObjectTypes.Element })
        for (var index = 0; ; index++)
        {
            var batch = entities.GetPagedDescendants(kind, index, 100, out var total, includeTrashed: true).ToArray();
            foreach (var entity in batch)
            {
                ct.ThrowIfCancellationRequested();
                IContentBase? content = kind switch {
                    UmbracoObjectTypes.Document => documents.GetById(entity.Key),
                    UmbracoObjectTypes.Media => media.GetById(entity.Key),
                    UmbracoObjectTypes.Member => members.GetById(entity.Key),
                    _ => elements.GetById(entity.Key)
                };
                if (content is null) continue;
                if (kind == UmbracoObjectTypes.Element && types.Get(content.ContentTypeId) is { } type && definitions.Contains(type.Key))
                    yield return $"The element library contains '{content.Name}' of type '{type.Name}'. Delete it first, from the recycle bin too.";
                foreach (var property in content.Properties.Where(p => AppBlockSchema.IsBlockEditor(p.PropertyType.PropertyEditorAlias)
                    || p.PropertyType.PropertyEditorAlias == "Umbraco.RichText"))
                foreach (var value in property.Values.SelectMany(v => new[] { v.EditedValue, v.PublishedValue }).OfType<string>().Distinct())
                {
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    bool used;
                    try { using var json = JsonDocument.Parse(value); used = References(json.RootElement, definitions); }
                    catch (JsonException) { used = true; } // An unreadable block value cannot be proved safe to discard.
                    if (used) {
                        yield return $"'{content.Name}' ({entity.Key}) has AppBlocks elements or unreadable blocks in '{property.Alias}'. Remove them from its draft and published values first.";
                        break;
                    }
                }
            }
            if (batch.Length == 0 || (index + 1L) * 100 >= total) break;
        }
    }

    // Inspect only block properties. A nested editor may store its value as JSON text, including in a host-owned element.
    private static bool References(JsonElement value, ISet<Guid> definitions, int depth = 0)
    {
        if (depth > 32) return true;
        if (value.ValueKind == JsonValueKind.Object)
            return value.EnumerateObject().Any(p => p.Name == "contentTypeKey" && p.Value.ValueKind == JsonValueKind.String
                && p.Value.TryGetGuid(out var key) && definitions.Contains(key) || References(p.Value, definitions, depth + 1));
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().Any(v => References(v, definitions, depth + 1));
        if (value.ValueKind == JsonValueKind.String && value.GetString() is { } nested && nested.TrimStart().StartsWith('{'))
        {
            try { using var json = JsonDocument.Parse(nested); return References(json.RootElement, definitions, depth + 1); }
            catch (JsonException) { return false; } // Ordinary text inside an otherwise valid block.
        }
        return false;
    }
}
