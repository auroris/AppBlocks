using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services.Navigation;
using Umbraco.Cms.Core.Web;

namespace AppBlocks.DataSources;

public sealed record ListOfValuesDefinition(Guid Key, string Alias, string Name, DataSourceDefinition DataSource,
    string DisplayColumn, string ValueColumn, int MaximumRows = 1000);

/// <summary>Published data sources and lists of values shared by forms and companion packages.</summary>
public sealed class PublishedDataCatalog(IUmbracoContextFactory contexts, IDocumentNavigationQueryService navigation,
    DataSourceCatalog sources, DataSourceRuntime runtime, PublishedDefinitionWarnings warnings,
    ILogger<PublishedDataCatalog> logger, AppBlocks.Connections.ConnectionCatalog? managed = null)
{
    public const string ListAlias = "listOfValues";
    private Dictionary<string, ListOfValuesDefinition>? lists;
    private readonly Dictionary<string, DataSourceRuntime.PlanEntry> plans = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, ListOfValuesDefinition> Lists => lists ?? throw new InvalidOperationException("Call LoadAsync before reading lists of values.");
    private Task? loading;
    public Task LoadAsync() => loading ??= LoadCoreAsync();
    public ListOfValuesDefinition? FindList(string? selection) => string.IsNullOrWhiteSpace(selection) ? null
        : Lists.GetValueOrDefault(selection) ?? Lists.Values.FirstOrDefault(list => PickedKey(selection) == list.Key);
    internal static Guid? PickedKey(string value) => Guid.TryParse(value.Replace("umb://document/", "", StringComparison.OrdinalIgnoreCase), out var key) ? key : null;
    public DataSourceDefinition? ResolveSource(string selection) => sources.DataSources.FirstOrDefault(source =>
        source.Alias == selection || source.Definition.Key == PickedKey(selection))?.Definition;

    public ListOfValuesDefinition? ReadList(Guid key, string name, Func<string, object?> value, ICollection<string> errors)
    {
        var start = errors.Count;
        var alias = Text(value, "alias");
        if (!DataSourceDefinition.IsAlias(alias)) errors.Add("Enter a list alias starting with a letter, using letters, digits, '.', '-' or '_'.");
        var source = ListSource(key, name, value);
        var display = Text(value, "displayColumn"); var stored = Text(value, "valueColumn");
        var maximum = 1000;
        if (Text(value, "maximumRows") is { Length: > 0 } raw && (!int.TryParse(raw, out maximum) || maximum < 1)) errors.Add("Maximum rows must be a positive whole number.");
        if (source is null) errors.Add("Select a published SQL data source.");
        else if (PlanOf(source).Entry.Plan is not { } plan) errors.Add("The list's data source cannot read its database schema.");
        else
        {
            if (!plan.Columns.Any(c => c.Column.Equals(display, StringComparison.OrdinalIgnoreCase))) errors.Add("Select an existing display column.");
            if (!plan.Columns.Any(c => c.Column.Equals(stored, StringComparison.OrdinalIgnoreCase))) errors.Add("Select an existing value column.");
        }
        return errors.Count == start ? new(key, alias, name, source!, display, stored, maximum) : null;
    }

    /// <summary>The source a list reads: the data source it selects, or its own connection and SQL.</summary>
    private DataSourceDefinition? ListSource(Guid key, string name, Func<string, object?> value) => Text(value, "dataSource") is { Length: > 0 } selection
        ? ResolveSource(selection)
        : new DataSourceDefinition { Key = key, Name = name, Alias = Text(value, "alias"), Connection = Text(value, "connection"), Source = Text(value, "sql"), Access = DataSourceAccess.Public };


    /// <summary>
    /// What a node reads besides itself, for the definition cache: the data source's published definition and
    /// whether its schema could be read, so republishing, unpublishing or recovering the source reads the node again.
    /// </summary>
    internal string SourceVersion(DataSourceDefinition? source)
    {
        if (source is null) return "";
        var (fingerprint, entry) = PlanOf(source);
        return entry.Plan is null ? fingerprint + ":unavailable" : fingerprint;
    }

    /// <summary>One look at a source's plan per scope, so a cached definition and the version it is cached under agree.</summary>
    internal (string Fingerprint, DataSourceRuntime.PlanEntry Entry) PlanOf(DataSourceDefinition source)
    {
        var fingerprint = source.ComputeFingerprint() + ":" + (managed?.Revision(source.Connection) ?? "");
        if (!plans.TryGetValue(fingerprint, out var entry)) plans[fingerprint] = entry = runtime.Plan(source);
        return (fingerprint, entry);
    }

    /// <summary>A definition that does not read is logged as it is read, which is once per version, not per request.</summary>
    private T? Logged<T>(IPublishedContent node, Func<List<string>, T?> read) where T : class
    {
        var errors = new List<string>();
        var definition = read(errors);
        if (errors.Count > 0) logger.LogWarning("Published definition {Name} ({Key}) was ignored: {Errors}", node.Name, node.Key, string.Join(" ", errors));
        return definition;
    }


    private async Task LoadCoreAsync()
    {
        await sources.LoadAsync();
        lists = new(StringComparer.OrdinalIgnoreCase);
        using var reference = contexts.EnsureUmbracoContext();
        if (reference.UmbracoContext.Content is not { } cache || !navigation.TryGetRootKeysOfType(DataSourceSchema.RootAlias, out var roots)) return;
        foreach (var root in roots)
        {
            if (await cache.GetByIdAsync(root, false) is null) continue;
            if (!navigation.TryGetDescendantsKeysOfType(root, ListAlias, out var keys)) continue;
            foreach (var key in keys)
            {
                if (await cache.GetByIdAsync(key, false) is not { } node) continue;
                Func<string, object?> value = property => node.GetProperty(property)?.GetValue();
                if (runtime.Definition(node.Key, node.UpdateDate, () => Logged(node, errors => ReadList(node.Key, node.Name, value, errors)),
                    SourceVersion(ListSource(node.Key, node.Name, value))) is { } list && !lists.TryAdd(list.Alias, list)) warnings.Hidden(node, list.Alias);
            }
        }
    }

    private static string Text(Func<string, object?> value, string property) => value(property)?.ToString()?.Trim() ?? "";
}

/// <summary>
/// One message per configured alias replaced in this process, including the first request after startup, and one per
/// published version of a node left out because an earlier node has its alias.
/// </summary>
public sealed class PublishedDefinitionWarnings(ILogger<PublishedDefinitionWarnings> logger)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> logged = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid Key, DateTime Updated), byte> hidden = new();
    public void Replaced(string alias) { if (logged.TryAdd(alias, 0)) logger.LogInformation("Published report {Alias} replaces the configured report of the same name.", alias); }
    public void Hidden(IPublishedContent node, string alias)
    {
        if (hidden.TryAdd((node.Key, node.UpdateDate), 0))
            logger.LogWarning("Published definition {Name} ({Key}) was ignored: an earlier published definition has the alias {Alias}.", node.Name, node.Key, alias);
    }
}
