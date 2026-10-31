using System.Text.Json;
using InteractiveReport.AspNetCore;
using InteractiveReport.Core.Model;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services.Navigation;
using Umbraco.Cms.Core.Web;
using AppBlocks.InteractiveReports.Reports;
using AppBlocks.DataSources;

namespace AppBlocks.InteractiveReports.DataSources;

public sealed record PublishedReport(Guid Key, string Alias, ReportDefinition Definition, DataSourceDefinition? DataSource);

/// <summary>
/// Published definitions take precedence over the configured seed. Assembled once in each request scope from
/// nodes read and compiled once per published version of the node and of the data source it reads.
/// </summary>
public sealed class PublishedReportCatalog(IUmbracoContextFactory contexts, IDocumentNavigationQueryService navigation,
    DataSourceCatalog sources, IOptionsMonitor<InteractiveReportOptions> configured, IConfiguration configuration,
    DataSourceRuntime runtime, ReportDefinitionCompiler compiler, PublishedDefinitionWarnings warnings,
    ILogger<PublishedReportCatalog> logger, AppBlocks.Connections.ConnectionCatalog? managed = null)
{
    public const string ReportAlias = "reportDefinition";
    private const string RowRestrictionMarker = "{{RowRestriction}}";
    private Dictionary<string, PublishedReport>? nodes;
    private Dictionary<string, ReportDefinition>? reports;
    private readonly HashSet<string> unavailableReports = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Guid> reportOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DataSourceRuntime.PlanEntry> plans = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, ReportDefinition> Reports { get { Ensure(); return reports!; } }
    public IReadOnlyDictionary<string, PublishedReport> ReportNodes { get { Ensure(); return nodes!; } }
    public bool IsUnavailableReport(string alias) { Ensure(); return unavailableReports.Contains(alias); }
    public Guid? ReportOwner(string alias) { Ensure(); return reportOwners.TryGetValue(alias.Trim(), out var key) ? key : null; }
    internal static Guid? PickedKey(string value) => Guid.TryParse(value.Replace("umb://document/", "", StringComparison.OrdinalIgnoreCase), out var key) ? key : null;

    public DataSourceDefinition? ResolveSource(string selection) => sources.DataSources.FirstOrDefault(source =>
        source.Alias == selection || source.Definition.Key == PickedKey(selection))?.Definition;

    public DataSourceDefinition? SourceForReport(string name) => ReportNodes.GetValueOrDefault(name)?.DataSource;

    public DataSourceDefinition? ResolveBlockSource(Guid key, string selection, ICollection<string> errors) =>
        ReportBlockSource.Read(key, selection, name => managed?.List().Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) == true, errors);

    /// <summary>Builds a report from a block-owned table or SQL query.</summary>
    public PublishedReport? ReadBlockReport(Guid key, Func<string, object?> value, ICollection<string> errors)
    {
        var selection = Text(value, "dataSource");
        var source = ResolveBlockSource(key, selection, errors);
        if (source is null) { if (errors.Count == 0) errors.Add("Select a report table or SQL query."); return null; }
        return ReadReport(key, Text(value, "heading"), property => property switch {
            "alias" => "appblocks-block-" + key.ToString("N"),
            "title" => value("heading"), "defaultPageSize" => value("pageSize"),
            "dataSource" or "controls" => value(property), _ => null,
        }, errors, source);
    }

    /// <summary>A published report's columns when its data source's schema gives them; null for SQL that only a query would reveal.</summary>
    public IReadOnlyList<string>? ReportColumns(string alias) => ReportNodes.TryGetValue(alias, out var node) && node.DataSource is { } source
        ? SourceColumns(source) : null;

    public IReadOnlyList<string>? SourceColumns(DataSourceDefinition source) => PlanOf(source).Entry.Plan is { } plan
        ? plan.Columns.Select(column => column.Column).ToArray() : null;

    public PublishedReport? ReadReport(Guid key, string name, Func<string, object?> value, ICollection<string> errors,
        DataSourceDefinition? blockSource = null)
    {
        var start = errors.Count;
        var alias = Text(value, "alias");
        if (!DataSourceDefinition.IsAlias(alias)) errors.Add("Enter a report alias starting with a letter, using letters, digits, '.', '-' or '_'.");
        ReportDefinition report;
        try { report = JsonSerializer.Deserialize<ReportDefinition>(Text(value, "options") is { Length: > 0 } json ? json : "{}", IrJson.Options) ?? new(); }
        catch (JsonException error) { errors.Add("Report options are not valid JSON: " + error.Message); return null; }
        // Connections are registered in code. Options cannot smuggle connection strings into content.
        if (!string.IsNullOrEmpty(report.DataSource) || !string.IsNullOrEmpty(report.Provider)) errors.Add("Use a registered connection; report options cannot contain database connection strings or providers.");
        // Context parameters bind server-owned values: a data source's bind variables supply them, never content.
        if (report.ContextParams is { Count: > 0 }) errors.Add("Report options cannot contain contextParams; a data source's row restriction binds the signed-in person.");
        // The source name is the server's too: a node naming another report would borrow that report's access decisions.
        report.DataSource = null; report.Provider = null; report.ContextParams = null; report.SourceName = null;
        report.Name = alias;
        if (Text(value, "title") is { Length: > 0 } title) report.Title = title;
        if (Text(value, "controls") is { Length: > 0 } controls)
            report.Features = controls.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // The names report blocks accept too: Interactive Reports' feature tokens.
        if (report.Features?.FirstOrDefault(control => ReportFeatures.Canonical(control) is null) is { } unknown)
            errors.Add($"'{unknown}' is not a control name. Use {string.Join(", ", ReportFeatures.All)}.");
        ReadSize("defaultPageSize", size => report.DefaultPageSize = size);
        ReadSize("maxPageSize", size => report.MaxPageSize = size);
        if (report.DefaultPageSize < 1 || report.MaxPageSize < 1) errors.Add("Page sizes must be positive whole numbers.");
        else if (report.DefaultPageSize > report.MaxPageSize)
            errors.Add($"The default page size ({report.DefaultPageSize}) cannot be larger than the maximum page size ({report.MaxPageSize}).");
        DataSourceDefinition? source = null;
        if (Text(value, "dataSource") is { Length: > 0 } selection)
        {
            source = blockSource ?? ResolveSource(selection);
            if (source is null) errors.Add("Select a published SQL data source.");
            else if (PlanOf(source).Entry.Plan is not { } plan) errors.Add("The selected data source cannot read its database schema.");
            else
            {
                report.Connection = source.Connection;
                var parameters = new SqlParameters(plan.Dialect);
                report.Sql = plan.Dialect.Select(plan.From(parameters, _ => null), plan.Columns.Select(c => c.Column), [],
                    "{{RowRestriction}}" + (plan.Restriction is null ? "" : " AND (" + plan.Restriction.Render(parameters, _ => null) + ")"));
                report.ContextParams = parameters.Bindings.ToDictionary(p => p.Name, _ => new ContextParamSpec { Claim = "apphost-preview" });
            }
        }
        else
        {
            report.Connection = Text(value, "connection"); report.Sql = Text(value, "sql");
            if (report.Connection.Length == 0 || report.Sql.Length == 0) errors.Add("Select a data source, or enter both a registered connection and SQL.");
            // Only a callback in code decides {{RowRestriction}}; without one, Interactive Reports refuses the report when it runs.
            else if (report.Sql.Contains(RowRestrictionMarker, StringComparison.Ordinal) && !HasRowRestrictionCallback(alias))
                errors.Add("Inline SQL can use {{RowRestriction}} only where a row-access callback in code decides it, and none decides this report. Select a data source instead, whose row restriction and read groups apply to every request.");
            if (alias == RecentPagesReport.Name) report.Sql = RecentPagesReport.PrepareSql(report.Sql, configuration);
        }
        if (errors.Count != start) return null;
        var virtualPath = configuration["Umbraco:CMS:Hosting:ApplicationVirtualPath"]?.Trim('/') ?? "";
        ReportLinksUnderVirtualPath.Apply(report, virtualPath.Length == 0 ? "" : "/" + virtualPath);
        try { AppBlocks.InteractiveReports.Connections.ManagedReportCompiler.CompileManaged(compiler, alias, report, managed); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException) { errors.Add(error.Message); }
        return errors.Count == start ? new(key, alias, report, source) : null;

        void ReadSize(string property, Action<int> set)
        {
            if (Text(value, property) is not { Length: > 0 } raw) return;
            if (int.TryParse(raw, out var size) && size > 0) set(size); else errors.Add($"{property} must be a positive whole number.");
        }
    }

    /// <summary>
    /// Whether a callback in code decides a report's {{RowRestriction}}: the Welcome report's own, or the one a site
    /// registered for a configured report that uses the marker, which also decides a node overriding that alias.
    /// </summary>
    private bool HasRowRestrictionCallback(string alias) => alias == RecentPagesReport.Name
        || configured.CurrentValue.Reports.Any(seed => seed.Key == alias && seed.Value.Sql?.Contains(RowRestrictionMarker, StringComparison.Ordinal) == true);

    /// <summary>
    /// What a node reads besides itself, for the definition cache: the data source's published definition and
    /// whether its schema could be read, so republishing, unpublishing or recovering the source reads the node again.
    /// </summary>
    private string SourceVersion(DataSourceDefinition? source)
    {
        if (source is null) return "";
        var (fingerprint, entry) = PlanOf(source);
        return entry.Plan is null ? fingerprint + ":unavailable" : fingerprint;
    }

    /// <summary>One look at a source's plan per scope, so a cached definition and the version it is cached under agree.</summary>
    private (string Fingerprint, DataSourceRuntime.PlanEntry Entry) PlanOf(DataSourceDefinition source)
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

    private void Ensure()
    {
        if (reports is not null) return;
        reports = new(configured.CurrentValue.Reports, StringComparer.OrdinalIgnoreCase);
        nodes = new(StringComparer.OrdinalIgnoreCase);
        using var reference = contexts.EnsureUmbracoContext();
        if (reference.UmbracoContext.Content is not { } cache || !navigation.TryGetRootKeysOfType(DataSourceSchema.RootAlias, out var roots)) return;
        foreach (var root in roots.Where(key => cache.GetById(false, key) is not null))
            foreach (var type in new[] { ReportAlias })
            {
                if (!navigation.TryGetDescendantsKeysOfType(root, type, out var keys)) continue;
                foreach (var node in keys.Select(key => cache.GetById(false, key)).OfType<IPublishedContent>())
                {
                    Func<string, object?> value = property => node.GetProperty(property)?.GetValue();
                    var alias = Text(value, "alias");
                    if (!reportOwners.TryAdd(alias, node.Key)) { warnings.Hidden(node, alias); continue; }
                    var selected = Text(value, "dataSource") is { Length: > 0 } selection ? ResolveSource(selection) : null;
                    var report = runtime.Definition(node.Key, node.UpdateDate, () => Logged(node, errors => ReadReport(node.Key, node.Name, value, errors)), SourceVersion(selected));
                    // An unavailable override cannot expose the configured seed's potentially broader dataset.
                    if (report is null) { reports.Remove(alias); unavailableReports.Add(alias); }
                    else if (nodes.TryAdd(report.Alias, report))
                    {
                        if (configured.CurrentValue.Reports.ContainsKey(report.Alias)) warnings.Replaced(report.Alias);
                        reports[report.Alias] = report.Definition;
                    }
                }
            }
    }

    private static string Text(Func<string, object?> value, string property) => value(property)?.ToString()?.Trim() ?? "";
}

