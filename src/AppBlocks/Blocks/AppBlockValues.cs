using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.Extensions.FileProviders;

namespace AppBlocks.Blocks;

/// <summary>
/// One block placed on a grid or in an area, as <see cref="AppBlockValues"/> made it. <see cref="Place"/> puts other blocks in one of
/// its areas, by the area's alias in the Forms container's grid, such as a form's <c>fields</c> and <c>actions</c>.
/// </summary>
public sealed class AppBlockItem
{
    private readonly Dictionary<string, List<AppBlockItem>> areas = new(StringComparer.Ordinal);

    internal AppBlockItem(Guid key, string alias, int columnSpan)
    {
        Key = key;
        Alias = alias;
        ColumnSpan = columnSpan;
    }

    public Guid Key { get; }
    public string Alias { get; }
    public int ColumnSpan { get; }

    /// <summary>Places <paramref name="items"/> at the end of the area <paramref name="area"/>; an area the block's grid entry lacks is refused.</summary>
    public AppBlockItem Place(string area, params AppBlockItem[] items)
    {
        if (!AppBlockValues.AreaKeys(Alias).ContainsKey(area))
            throw new ArgumentException($"The Forms container's grid gives {Alias} no area '{area}'.", nameof(area));
        if (!areas.TryGetValue(area, out var list)) areas[area] = list = [];
        list.AddRange(items);
        return this;
    }

    internal JsonObject Layout() => new() {
        ["contentKey"] = Key.ToString(), ["settingsKey"] = null, ["columnSpan"] = ColumnSpan, ["rowSpan"] = 1,
        // Every area the grid entry declares, empty or not, as the backoffice saves it.
        ["areas"] = new JsonArray(AppBlockValues.AreaKeys(Alias).Select(area => (JsonNode)new JsonObject {
            ["key"] = area.Value.ToString(),
            ["items"] = new JsonArray(areas.GetValueOrDefault(area.Key)?.Select(item => (JsonNode)item.Layout()).ToArray() ?? []),
        }).ToArray()),
    };
}

/// <summary>
/// A Block Grid value built from aliases, for a plugin that installs pages from code: fields, a form with its Form Interface and
/// actions, and any other block, with every element type and area key taken from the package's schema, so no caller writes a GUID.
/// A block's own key derives from the <c>id</c> it is given, so installing the same page again writes the same keys; without one
/// it is new each time. <see cref="ToJson"/> gives the value to set on a Block Grid property.
/// </summary>
public sealed class AppBlockValues
{
    private readonly List<JsonObject> contentData = [];
    private readonly List<AppBlockItem> layout = [];
    private readonly HashSet<Guid> keys = [];

    /// <summary>
    /// Adds a block of element type <paramref name="alias"/> with <paramref name="values"/>, an object or dictionary whose members are
    /// the element's property aliases, and returns its item for the caller to place: on the page with <see cref="Place"/> or in an area.
    /// </summary>
    /// <param name="id">A stable name for the block, unique in the value, which its key derives from.</param>
    /// <param name="columnSpan">The columns it spans of the grid or area it is placed in, which are twelve in the Forms container.</param>
    public AppBlockItem Add(string alias, object? values = null, string? id = null, int columnSpan = 12)
    {
        var key = id is null ? Guid.NewGuid() : AppBlockSchema.Key("block:" + id);
        if (!keys.Add(key)) throw new ArgumentException($"Another block in this value already has the id '{id}'.", nameof(id));
        contentData.Add(Element(alias, key, values));
        return new AppBlockItem(key, alias, columnSpan);
    }

    /// <summary>A field block, such as <c>appText</c> or <c>appSelect</c>, bound to the resource's <paramref name="field"/>, with any other of its properties in <paramref name="values"/>.</summary>
    public AppBlockItem Field(string alias, string field, object? values = null, string? id = null, int columnSpan = 12)
    {
        if (!AppBlockSchema.FieldAliases.Contains(alias)) throw new ArgumentException($"{alias} is not a field block.", nameof(alias));
        var properties = Properties(values);
        properties["field"] = field;
        return Add(alias, properties, id, columnSpan);
    }

    /// <summary>
    /// A form on <paramref name="resource"/>, a C# class or a published data source: its Form Interface names the resource, the query
    /// parameter that carries a record's key and the page Save and Cancel return to; <paramref name="fields"/> go in its fields area and
    /// an Actions block with <paramref name="actions"/> (its labels, say) in its actions area. <paramref name="values"/> are the form
    /// block's own, such as its heading; <paramref name="formInterface"/> adds to the interface's, such as <c>viewOnly</c>.
    /// </summary>
    public AppBlockItem Form(string resource, IEnumerable<AppBlockItem> fields, Guid? returnPage = null, string keyParameter = "id", object? actions = null,
        object? values = null, object? formInterface = null, string? id = null, int columnSpan = 12)
    {
        var settings = Properties(formInterface);
        settings["resource"] = resource;
        settings["keyParameter"] = keyParameter;
        if (returnPage is { } page) settings["returnPage"] = "umb://document/" + page.ToString("N");
        var interfaceKey = id is null ? Guid.NewGuid() : AppBlockSchema.Key("block:" + id + ":interface");
        var properties = Properties(values);
        properties["formInterface"] = new JsonObject {
            ["contentData"] = new JsonArray(Element("appFormInterface", interfaceKey, settings)), ["settingsData"] = new JsonArray(),
            ["expose"] = new JsonArray(Expose(interfaceKey)),
            ["layout"] = new JsonObject { ["Umbraco.SingleBlock"] = new JsonArray(new JsonObject { ["contentKey"] = interfaceKey.ToString(), ["settingsKey"] = null }) },
        };
        var form = Add("appForm", properties, id, columnSpan);
        form.Place("fields", [.. fields]);
        form.Place("actions", Add("appActions", actions, id is null ? null : id + ":actions"));
        return form;
    }

    /// <summary>Places <paramref name="items"/> on the page's own grid, in order.</summary>
    public AppBlockValues Place(params AppBlockItem[] items)
    {
        layout.AddRange(items);
        return this;
    }

    /// <summary>The Block Grid value as the backoffice saves it.</summary>
    public string ToJson() => new JsonObject {
        ["contentData"] = new JsonArray(contentData.Select(block => (JsonNode)block.DeepClone()).ToArray()),
        ["settingsData"] = new JsonArray(),
        ["expose"] = new JsonArray(contentData.Select(block => (JsonNode)Expose(Guid.Parse(block["key"]!.GetValue<string>()))).ToArray()),
        ["layout"] = new JsonObject { ["Umbraco.BlockGrid"] = new JsonArray(layout.Select(item => (JsonNode)item.Layout()).ToArray()) },
    }.ToJsonString();

    private static JsonObject Element(string alias, Guid key, object? values) => new() {
        ["key"] = key.ToString(), ["contentTypeKey"] = AppBlockSchema.Key(alias).ToString(),
        ["values"] = new JsonArray(Properties(values).Select(pair => (JsonNode)new JsonObject {
            ["alias"] = pair.Key, ["culture"] = null, ["segment"] = null, ["editorAlias"] = null, ["value"] = pair.Value?.DeepClone(),
        }).ToArray()),
    };

    private static JsonObject Expose(Guid key) => new() { ["contentKey"] = key.ToString(), ["culture"] = null, ["segment"] = null };

    /// <summary>The members of an object or dictionary as property values; a value that is not text is written as the editor stores it, as JSON.</summary>
    private static JsonObject Properties(object? values) => values switch {
        null => [],
        JsonObject json => (JsonObject)json.DeepClone(),
        _ => JsonSerializer.SerializeToNode(values) as JsonObject ?? throw new ArgumentException("Give the values as an object or a dictionary.", nameof(values)),
    };

    private static readonly Lazy<IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, Guid>>> Areas = new(ReadAreas);

    /// <summary>The areas the Forms container's grid gives an element type, by alias, in the grid's order; none for a block without areas.</summary>
    internal static IReadOnlyDictionary<string, Guid> AreaKeys(string alias)
        => Areas.Value.GetValueOrDefault(AppBlockSchema.Key(alias)) ?? new Dictionary<string, Guid>();

    // Read from the package's own grid definition, so the keys are the ones the schema installs.
    private static IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, Guid>> ReadAreas()
    {
        using var stream = new ManifestEmbeddedFileProvider(typeof(AppBlockSchema).Assembly).GetFileInfo("Schema/DataTypes/ApplicationApplicationBlocks.config").CreateReadStream();
        var configuration = JsonNode.Parse(XElement.Load(stream).Element("Config")!.Value)!;
        var areas = new Dictionary<Guid, IReadOnlyDictionary<string, Guid>>();
        foreach (var block in configuration["blocks"]!.AsArray())
        {
            var byAlias = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var area in block!["areas"]?.AsArray() ?? [])
                byAlias[area!["alias"]!.GetValue<string>()] = Guid.Parse(area["key"]!.GetValue<string>());
            areas[Guid.Parse(block["contentElementTypeKey"]!.GetValue<string>())] = byAlias;
        }
        return areas;
    }
}
