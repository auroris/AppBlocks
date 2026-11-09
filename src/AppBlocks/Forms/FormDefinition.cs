using System.Globalization;
using System.Text.Json;
using Umbraco.Cms.Core.Models.Blocks;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Extensions;
using AppBlocks.Blocks;
using AppBlocks.DataSources;

namespace AppBlocks.Forms;

public sealed record FormDefinition(IPublishedContent Page, BlockGridItem Block, IPublishedElement Interface,
    IFormResource Resource, IReadOnlyList<BlockGridItem> Fields)
{
    public string ListsRevision { get; init; } = "";
    public string? Culture { get; init; }
    /// <summary>The form's Anti-spam blocks, wherever they sit inside it. They come from the published page, so no request can leave them out.</summary>
    public IReadOnlyList<BlockGridItem> AntiSpam { get; init; } = [];
    public string Revision => $"{Page.UpdateDate.Ticks}:{Culture}{ListsRevision}:{Resource.ContractVersion}";
    public bool ViewOnly => Interface.Value<bool>("viewOnly");
    public string KeyParameter => Interface.Value<string>("keyParameter") is { Length: > 0 } key ? key : "id";
    /// <summary>Where Save and Cancel return to: the Form interface's return page, or the site's root under <paramref name="pathBase"/>.</summary>
    public string ReturnUrl(PathString pathBase) => Interface.Value<IPublishedContent>("returnPage")?.Url() ?? pathBase + "/";
    /// <summary>
    /// The form block <paramref name="key"/> of a page with the resource its Form Interface names: a registered C# class or a published
    /// data source, the class first when both have the alias. Null when the form does not resolve, including when it holds another form
    /// or a block of one of <paramref name="plugins"/> that acts for the user, which publishing refuses too.
    /// </summary>
    public static async Task<FormDefinition?> ResolveAsync(IPublishedContent page, Guid key, FormResourceRegistry resources, PublishedDataCatalog? definitions = null, string? culture = null,
        IEnumerable<IAppBlockPlugin>? plugins = null)
    {
        var blocks = AppBlockContent.Blocks(page, culture);
        var forms = AppBlockContent.Flatten(blocks, culture)
            .Where(b => b.Content.ContentType.Alias == "appForm").ToArray();
        var matches = forms.Where(b => b.ContentKey == key).ToArray();
        if (matches.Length != 1) return null;
        var block = matches[0];
        var configuration = block.Content.Value<BlockListItem>("formInterface")?.Content;
        if (configuration?.Value<string>("resource") is not { Length: > 0 } alias || await resources.FindAsync(alias) is not { } resource) return null;
        var children = AppBlockContent.Flatten(block.Areas.SelectMany(a => a)).ToArray();
        var userBlocks = plugins?.Where(p => p.RequiresUser).Select(p => p.Alias).ToHashSet(StringComparer.Ordinal) ?? [];
        if (children.Any(b => b.Content.ContentType.Alias == "appForm" || userBlocks.Contains(b.Content.ContentType.Alias))) return null;
        var fields = children.Where(b => AppBlockSchema.FieldAliases.Contains(b.Content.ContentType.Alias)).ToArray();
        var bindings = fields.Select(f => f.Content.Value<string>("field") ?? "").ToArray();
        if (bindings.Distinct(StringComparer.Ordinal).Count() != bindings.Length || bindings.Any(binding => !resource.Fields.Any(f => f.Key == binding))) return null;
        if (fields.Any(f => !AppBlocksValidator.Compatible(f.Content.ContentType.Alias, resource.Fields.First(r => r.Key == f.Content.Value<string>("field")), !string.IsNullOrWhiteSpace(f.Content.Value<string>("listOfValues"))))) return null;
        var selected = fields.Where(f => f.Content.ContentType.Alias is "appSelect" or AppBlockSchema.ShuttleAlias).Select(f => f.Content.Value<string>("listOfValues")).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        if (selected.Length > 0 && definitions is not null) await definitions.LoadAsync();
        var lists = selected.Select(s => definitions?.FindList(s)).ToArray();
        if (lists.Any(list => list is null)) return null;
        return new(page, block, configuration, resource, fields) { Culture = culture, AntiSpam = children.Where(b => b.Content.ContentType.Alias == AppBlockSchema.AntiSpamAlias).ToArray(), ListsRevision = string.Concat(lists.Select(list => ":" + list!.Key + ":" + list.DataSource.ComputeFingerprint() + ":" + list.DisplayColumn + ":" + list.ValueColumn + ":" + list.MaximumRows)) };
    }

    public Dictionary<string, string[]> Validate(JsonElement input, bool creating)
    {
        var errors = new Dictionary<string, string[]>();
        if (input.ValueKind != JsonValueKind.Object) { errors[""] = ["Form values must be an object."]; return errors; }
        var bindings = Fields.ToDictionary(f => f.Content.Value<string>("field")!, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in input.EnumerateObject()) {
            if (!seen.Add(property.Name)) { errors[property.Name] = ["Duplicate field."]; continue; }
            var field = Resource.Fields.FirstOrDefault(f => f.Key == property.Name);
            if (field is null || !bindings.TryGetValue(property.Name, out var block) || field.ReadOnly || block.Content.Value<bool>("readOnly") || block.Content.ContentType.Alias == "appDisplay") {
                errors[property.Name] = ["This field cannot be changed through this form."]; continue;
            }
            var value = property.Value;
            if (block.Content.ContentType.Alias == AppBlockSchema.ColorAlias && value.ValueKind != JsonValueKind.Null
                && (value.ValueKind != JsonValueKind.String || value.GetString() is { Length: > 0 } color
                    && (color.Length != 7 || color[0] != '#' || !color.Skip(1).All(char.IsAsciiHexDigit)))) {
                errors[field.Key] = ["Enter a color in #RRGGBB format, such as #2a5d8f."]; continue;
            }
            var shuttle = block.Content.ContentType.Alias == AppBlockSchema.ShuttleAlias;
            IReadOnlyList<string> selected = [];
            if (shuttle && value.ValueKind == JsonValueKind.String && !ShuttleValue.TryParse(value.GetString(), out selected)) {
                errors[field.Key] = ["Use a single line of CSV with unique, non-empty values."]; continue;
            }
            var empty = value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString());
            if (empty) {
                if (field.Required || block.Content.Value<bool>("required")) errors[field.Key] = ["A value is required."];
                continue;
            }
            var valid = field.Kind switch {
                "number" => Number(value, out _),
                "checkbox" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "date" => value.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                "time" => value.ValueKind == JsonValueKind.String && TimeOnly.TryParse(value.GetString(), CultureInfo.InvariantCulture, out _),
                "datetime" => value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                _ => value.ValueKind == JsonValueKind.String,
            };
            if (!valid) { errors[field.Key] = ["Enter a valid value."]; continue; }
            if (value.ValueKind == JsonValueKind.String && field.MaxLength is { } max && value.GetString()!.Length > max) errors[field.Key] = [$"Use at most {max} characters."];
            if (field.Kind == "number" && Number(value, out var numeric)
                && (field.Minimum is { } min && numeric < min || field.Maximum is { } upper && numeric > upper)) errors[field.Key] = ["The number is outside the allowed range."];
            if (shuttle) {
                if (field.Choices is not null && selected.Any(item => !field.Choices.Any(choice => choice.Value == item))) errors[field.Key] = ["Select available choices."];
            }
            else if (field.Choices is not null && (value.ValueKind != JsonValueKind.String || !field.Choices.Any(choice => choice.Value == value.GetString()))) errors[field.Key] = ["Select an available choice."];
        }
        if (creating) {
            foreach (var field in Resource.Fields.Where(f => !f.HasServerDefault && !f.ReadOnly))
                if ((field.Required || bindings.TryGetValue(field.Key, out var block) && block.Content.Value<bool>("required"))
                    && !input.TryGetProperty(field.Key, out _)) errors[field.Key] = ["A value is required."];
        }
        return errors;
    }

    private static bool Number(JsonElement value, out decimal number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number ? value.TryGetDecimal(out number)
            : value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }
}
