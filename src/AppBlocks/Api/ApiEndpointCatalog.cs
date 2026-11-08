using AppBlocks.DataSources;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.Navigation;
using Umbraco.Cms.Core.Web;

namespace AppBlocks.Api;

/// <summary>Whether an endpoint answers with every row or with the first one.</summary>
public enum ApiEndpointShape { Collection, Single }

/// <summary>
/// A published <c>apiEndpoint</c> node: a route below the base path that serves a SQL data source's rows as JSON, the way an Oracle
/// APEX RESTful service serves a query. The source is a published data source, or the node's own connection and SQL with its own access.
/// </summary>
/// <param name="PicksSource">Whether <paramref name="Source"/> is a published data source the node selects, rather than its own SQL.</param>
/// <param name="Order">Columns of the row source to sort by, as the schema spells them.</param>
/// <param name="QueryParameters">The <c>:PARAM.</c> names the query string may fill, besides the route's own parameters.</param>
public sealed record ApiEndpointDefinition(Guid Key, string Alias, string Name, ApiRouteTemplate Route, DataSourceDefinition Source, bool PicksSource,
    ApiEndpointShape Shape, int MaximumRows, IReadOnlyList<(string Column, bool Descending)> Order, IReadOnlyList<string> QueryParameters);

/// <summary>
/// The published API endpoints, wherever in the content tree editors put them, read once per request scope from Umbraco's published cache, so publishing, unpublishing or deleting
/// an endpoint changes what the next request finds. A definition that does not read is logged and left out; of two endpoints with one
/// alias or overlapping routes the first in tree order wins. Requests are matched most literal segments first.
/// </summary>
public sealed class ApiEndpointCatalog(IUmbracoContextFactory contexts, IDocumentNavigationQueryService navigation, IMemberGroupService groups,
    DataSourceCatalog sources, PublishedDataCatalog data, DataSourceRuntime runtime, PublishedDefinitionWarnings warnings, ILogger<ApiEndpointCatalog> logger)
{
    /// <summary>A root that keeps API endpoints together, for editors who want one: no template, allowed at root, never routed.</summary>
    public const string RootAlias = "apiEndpoints";
    public const string Alias = "apiEndpoint";
    public const int DefaultMaximumRows = 1000;

    private List<ApiEndpointDefinition>? endpoints;
    private Task? loading;
    public Task LoadAsync() => loading ??= LoadCoreAsync();

    /// <summary>The endpoints in the order requests are matched against them.</summary>
    public IReadOnlyList<ApiEndpointDefinition> Endpoints => endpoints ?? throw new InvalidOperationException("Call LoadAsync before reading the API endpoints.");

    /// <summary>The endpoint a request path's segments reach, with its route parameter values; null for none.</summary>
    public (ApiEndpointDefinition Endpoint, IReadOnlyDictionary<string, string> Values)? Find(IReadOnlyList<string> path)
    {
        foreach (var endpoint in Endpoints)
            if (endpoint.Route.Match(path) is { } values) return (endpoint, values);
        return null;
    }

    /// <summary>Reads an endpoint from property values, published or being published, reporting every mistake; checks its source's schema too.</summary>
    /// <param name="memberGroups">Turns a member group picker value into group names, or null when a picked group no longer exists.</param>
    public ApiEndpointDefinition? Read(Guid key, string name, Func<string, object?> value, Func<string, IReadOnlyList<string>?> memberGroups, ICollection<string> errors)
    {
        var start = errors.Count;
        var alias = Text(value, "alias");
        if (!DataSourceDefinition.IsAlias(alias)) errors.Add("Enter an endpoint alias starting with a letter, using letters, digits, '.', '-' or '_'.");
        var route = ApiRouteTemplate.Parse(Text(value, "route"), errors);
        ApiEndpointShape? shape = Text(value, "shape").ToLowerInvariant() switch {
            "" or "collection" => ApiEndpointShape.Collection, "single" => ApiEndpointShape.Single, _ => null,
        };
        if (shape is null) errors.Add("Select a shape: Collection or Single.");
        var maximum = DefaultMaximumRows;
        if (Text(value, "maximumRows") is { Length: > 0 } raw && (!int.TryParse(raw, out maximum) || maximum < 1)) errors.Add("Maximum rows must be a positive whole number.");
        var query = QueryNames(Text(value, "queryParameters"), errors);
        var source = Source(key, name, alias, value, memberGroups, errors, out var picks);
        var order = new List<(string Column, bool Descending)>();
        var ordered = ReadOrder(Text(value, "orderBy"), errors);
        if (errors.Count == start && source is not null)
        {
            var entry = data.PlanOf(source).Entry;
            if (entry.Plan is not { } plan)
                errors.Add(picks ? "The selected data source cannot read its database schema." : "The endpoint's SQL cannot be read: " + entry.Problem);
            else
            {
                foreach (var (column, descending) in ordered)
                    if (plan.Columns.FirstOrDefault(c => c.Column.Equals(column, StringComparison.OrdinalIgnoreCase) || c.Field.Equals(column, StringComparison.OrdinalIgnoreCase)) is { } found)
                        order.Add((found.Column, descending));
                    else errors.Add($"Order by names {column}, which is not a column of the source.");
                // A request parameter nobody can send would bind NULL silently, so every one the SQL uses must have a way in.
                var bound = route!.Parameters.Concat(query).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var variable in new[] { plan.Query, plan.Restriction }.OfType<SqlTemplate>().SelectMany(t => t.Variables)
                    .Where(v => v.Kind == BindVariableKind.Param).DistinctBy(v => v.Name!.ToUpperInvariant()))
                    if (!bound.Contains(variable.Name!))
                        errors.Add($"The SQL uses {variable}, which is neither a route parameter nor listed under Query parameters.");
            }
        }
        return errors.Count == start ? new(key, alias, name, route!, source!, picks, shape!.Value, maximum, order, query) : null;
    }

    /// <summary>The data source the node selects, or one made of its own connection, SQL, access and row restriction.</summary>
    private DataSourceDefinition? Source(Guid key, string name, string alias, Func<string, object?> value, Func<string, IReadOnlyList<string>?> memberGroups,
        ICollection<string> errors, out bool picks)
    {
        var selection = Text(value, "dataSource");
        picks = selection.Length > 0;
        if (picks)
        {
            // The data source's own settings apply; settings of the node's own beside them would only mislead.
            foreach (var (property, label) in new[] { ("connection", "Connection"), ("sql", "SQL"), ("access", "Access"), ("readGroups", "Read groups"), ("rowRestriction", "Row restriction") })
                if (Text(value, property).Length > 0) errors.Add($"The selected data source brings its own settings: clear the endpoint's {label}, or clear the data source to use the endpoint's own SQL.");
            var source = data.ResolveSource(selection);
            if (source is null) errors.Add("The selected data source is not published.");
            return source;
        }
        var sql = Text(value, "sql");
        if (sql.Length == 0) { errors.Add("Select a published SQL data source, or enter the endpoint's own SQL."); return null; }
        if (sql.Length > DataSourceDefinition.MaxSourceLength) errors.Add($"The SQL is longer than {DataSourceDefinition.MaxSourceLength} characters.");
        var restriction = Text(value, "rowRestriction");
        if (restriction.Length > DataSourceDefinition.MaxRestrictionLength) errors.Add($"The row restriction is longer than {DataSourceDefinition.MaxRestrictionLength} characters.");
        var readGroups = Text(value, "readGroups");
        IReadOnlyList<string> names = [];
        if (readGroups.Length > 0 && (names = memberGroups(readGroups)!) is null) { errors.Add("One of the read groups no longer exists. Pick the groups again."); names = []; }
        var connection = Text(value, "connection");
        var own = new DataSourceDefinition {
            Key = key, Name = name, Alias = alias, Source = sql,
            Connection = connection.Length == 0 ? AppBlocks.Connections.ConnectionCatalog.ApplicationConnection : connection,
            RowRestriction = restriction.Length == 0 ? null : restriction,
            Access = DataSourceDefinition.ReadAccess(Text(value, "access")), ReadGroups = names,
        };
        own.ValidateAccess(errors);
        return own;
    }

    /// <summary>Names separated by commas, spaces or lines, each one a <c>:PARAM.</c> name.</summary>
    private static IReadOnlyList<string> QueryNames(string text, ICollection<string> errors)
    {
        var names = new List<string>();
        foreach (var raw in text.Split([',', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var name = raw.StartsWith(":PARAM.", StringComparison.OrdinalIgnoreCase) ? raw[7..] : raw;
            if (!SqlTemplate.IsIdentifier(name)) errors.Add($"'{raw}' is not a query parameter name: use letters, digits and '_', starting with a letter or '_'.");
            else if (names.Contains(name, StringComparer.OrdinalIgnoreCase)) errors.Add($"Query parameters name {name} twice.");
            else names.Add(name);
        }
        return names;
    }

    /// <summary>Columns separated by commas, each optionally followed by ASC or DESC.</summary>
    private static IReadOnlyList<(string Column, bool Descending)> ReadOrder(string text, ICollection<string> errors)
    {
        var order = new List<(string Column, bool Descending)>();
        foreach (var raw in text.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var column = raw;
            var descending = false;
            var space = raw.LastIndexOf(' ');
            if (space > 0 && raw[(space + 1)..].ToUpperInvariant() is var direction && direction is "ASC" or "DESC")
                (column, descending) = (raw[..space].Trim(), direction == "DESC");
            column = DataSourceDefinition.Unquote(column);
            if (column.Length is 0 or > 128 || column.Any(char.IsControl)) errors.Add($"'{raw}' in Order by is not a column name.");
            else if (order.Any(o => o.Column.Equals(column, StringComparison.OrdinalIgnoreCase))) errors.Add($"Order by names {column} twice.");
            else order.Add((column, descending));
        }
        return order;
    }

    /// <summary>What a node reads besides itself, its source's definition and whether its schema could be read, so republishing or recovering the source reads the node again.</summary>
    private string Dependencies(Guid key, string name, Func<string, object?> value, Func<string, IReadOnlyList<string>?> memberGroups)
        => data.SourceVersion(Source(key, name, Text(value, "alias"), value, memberGroups, new List<string>(), out _));

    private async Task LoadCoreAsync()
    {
        await sources.LoadAsync();
        var found = new List<ApiEndpointDefinition>();
        endpoints = found;
        using var reference = contexts.EnsureUmbracoContext();
        if (reference.UmbracoContext.Content is not { } cache || !navigation.TryGetRootKeys(out var roots)) return;
        MemberGroupNames? names = null;
        // Where an endpoint sits is the editors' choice: any published endpoint whose ancestors are all published answers.
        foreach (var root in roots)
        {
            if (!navigation.TryGetDescendantsKeysOfType(root, Alias, out var keys)) continue;
            foreach (var key in keys)
            {
                if (await cache.GetByIdAsync(key, false) is not { } node || !await PublishedPathAsync(cache, key)) continue;
                names ??= new MemberGroupNames((await groups.GetAllAsync()).ToArray());
                Func<string, object?> value = property => node.GetProperty(property)?.GetValue();
                var endpoint = runtime.Definition(node.Key, node.UpdateDate, () => Logged(node, errors => Read(node.Key, node.Name, value, names.Resolve, errors)),
                    Dependencies(node.Key, node.Name, value, names.Resolve));
                if (endpoint is null) continue;
                if (found.FirstOrDefault(other => other.Alias.Equals(endpoint.Alias, StringComparison.OrdinalIgnoreCase)) is not null) { warnings.Hidden(node, endpoint.Alias); continue; }
                if (found.FirstOrDefault(other => other.Route.Overlaps(endpoint.Route)) is { } earlier)
                {
                    logger.LogWarning("API endpoint {Name} ({Key}) was ignored: the earlier endpoint {Other} has the route {Route}.", node.Name, node.Key, earlier.Alias, earlier.Route);
                    continue;
                }
                found.Add(endpoint);
            }
        }
        found.Sort((a, b) => ApiRouteTemplate.Precedence(a.Route, b.Route));
    }

    private async Task<bool> PublishedPathAsync(Umbraco.Cms.Core.PublishedCache.IPublishedContentCache cache, Guid key)
    {
        if (!navigation.TryGetAncestorsKeys(key, out var ancestors)) return false;
        foreach (var ancestor in ancestors)
            if (await cache.GetByIdAsync(ancestor, false) is null) return false;
        return true;
    }

    private T? Logged<T>(IPublishedContent node, Func<List<string>, T?> read) where T : class
    {
        var errors = new List<string>();
        var definition = read(errors);
        if (errors.Count > 0) logger.LogWarning("API endpoint {Name} ({Key}) was ignored: {Errors}", node.Name, node.Key, string.Join(" ", errors));
        return definition;
    }

    private static string Text(Func<string, object?> value, string property) => value(property)?.ToString()?.Trim() ?? "";
}

/// <summary>
/// Publishing an endpoint checks it as a request would read it, against its source's schema, and refuses a route another published
/// endpoint, or another endpoint in the same publish, already answers, and an alias another one has.
/// </summary>
public sealed class ApiEndpointValidator(ApiEndpointCatalog catalog, IMemberGroupService groups, BindVariableProviders? providers = null)
    : INotificationAsyncHandler<ContentPublishingNotification>
{
    public async Task HandleAsync(ContentPublishingNotification notification, CancellationToken cancellationToken)
    {
        var nodes = notification.PublishedEntities.Where(n => n.ContentType.Alias == ApiEndpointCatalog.Alias).ToArray();
        if (nodes.Length == 0) return;
        await catalog.LoadAsync();
        var names = new MemberGroupNames((await groups.GetAllAsync()).ToArray());
        var batch = new List<ApiEndpointDefinition>();
        foreach (var node in nodes)
        {
            var errors = new List<string>();
            if (catalog.Read(node.Key, node.Name ?? "API endpoint", alias => node.GetValue(alias), names.Resolve, errors) is { } endpoint)
            {
                var others = catalog.Endpoints.Where(other => other.Key != node.Key && !nodes.Any(n => n.Key == other.Key)).Concat(batch).ToArray();
                if (others.FirstOrDefault(other => other.Route.Overlaps(endpoint.Route)) is { } route)
                    errors.Add($"The endpoint {route.Name} already answers the route {route.Route}.");
                if (others.Any(other => other.Alias.Equals(endpoint.Alias, StringComparison.OrdinalIgnoreCase)))
                    errors.Add("Another published endpoint already uses this alias.");
                batch.Add(endpoint);
                // A selected data source was checked when it was published; the endpoint's own SQL is checked here.
                if (errors.Count == 0 && !endpoint.PicksSource && providers?.UndeclaredWarning(endpoint.Source.ApplicationNames()) is { } warning)
                    notification.Messages.Add(new EventMessage("API endpoint", node.Name + ": " + warning, EventMessageType.Warning));
            }
            if (errors.Count > 0) notification.CancelOperation(new EventMessage("API endpoint", node.Name + ": " + string.Join(" ", errors), EventMessageType.Error));
        }
    }
}
