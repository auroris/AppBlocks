using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.Navigation;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Core.Web;
using AppBlocks.Forms;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AppBlocks.DataSources;

/// <summary>
/// The published data sources, beside the C# resources in <see cref="FormResourceRegistry"/>: every published
/// <c>sqlDataSource</c> under a published Data sources root, in tree order. Read once per request scope; a
/// definition that does not read is logged and left out, and of two nodes with one alias the first wins.
/// </summary>
public sealed class DataSourceCatalog(IUmbracoContextFactory contexts, IDocumentNavigationQueryService navigation, IMemberGroupService groups,
    DataSourceRuntime runtime, IDataSourceUserResolver users, IEnumerable<IDataSourcePolicy> policies, ILogger<DataSourceCatalog> logger,
    BindVariableProviders? providers = null, DataSourceHandlers? handlers = null) : IFormResourceProvider
{
    private IReadOnlyList<SqlDataSource>? sources;

    private Task? loading;
    public Task LoadAsync() => loading ??= LoadCoreAsync();
    public IReadOnlyList<SqlDataSource> DataSources => sources ?? throw new InvalidOperationException("Call LoadAsync before reading the data source catalogue.");
    IEnumerable<IFormResource> IFormResourceProvider.Resources => DataSources;

    async Task<IEnumerable<IFormResource>> IFormResourceProvider.GetResourcesAsync() { await LoadAsync(); return DataSources; }

    private async Task LoadCoreAsync()
    {
        var found = new List<SqlDataSource>();
        sources = found;
        using var reference = contexts.EnsureUmbracoContext();
        if (reference.UmbracoContext.Content is not { } cache || !navigation.TryGetRootKeysOfType(DataSourceSchema.RootAlias, out var roots)) return;
        var names = new MemberGroupNames((await groups.GetAllAsync()).ToArray());
        foreach (var root in roots)
        {
            if (await cache.GetByIdAsync(root, false) is null) continue;
            if (!navigation.TryGetDescendantsKeysOfType(root, DataSourceSchema.SqlAlias, out var keys)) continue;
            foreach (var key in keys)
            {
                if (await cache.GetByIdAsync(key, false) is not { } node) continue;
                var definition = runtime.Definition(node.Key, node.UpdateDate, () => {
                    var errors = new List<string>();
                    var read = DataSourceDefinition.Read(node.Key, node.Name, alias => node.GetProperty(alias)?.GetValue(), names.Resolve, errors);
                    if (read is null) logger.LogWarning("Data source {Name} ({Key}) is left out: {Errors}", node.Name, node.Key, string.Join(" ", errors));
                    return read;
                });
                if (definition is null) continue;
                if (found.Any(source => source.Alias == definition.Alias))
                {
                    logger.LogWarning("Data source {Name} ({Key}) is left out: an earlier data source has the alias {Alias}.", node.Name, node.Key, definition.Alias);
                    continue;
                }
                found.Add(new SqlDataSource(definition, runtime, users, policies, providers, handlers));
            }
        }
    }
}

/// <summary>Turns a member group picker value, which Umbraco stores as group ids, into group names; null when a picked group is gone.</summary>
internal sealed class MemberGroupNames(IReadOnlyList<IMemberGroup> all)
{

    public IReadOnlyList<string>? Resolve(string picked)
    {
        var names = new List<string>();
        foreach (var item in picked.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var group = int.TryParse(item, out var id) ? all.FirstOrDefault(g => g.Id == id) : Guid.TryParse(item, out var key) ? all.FirstOrDefault(g => g.Key == key) : null;
            if (group?.Name is not { } name) return null;
            names.Add(name);
        }
        return names;
    }
}

/// <summary>
/// Publishing a data source checks it against its database: a definition that would fail at run time, such as an
/// unregistered connection, a column that is not there, a key that does not identify one row or an unknown bind
/// variable, is refused with every reason at once. Its alias must also be free among the C# resources and the
/// other published data sources, and the backing class it names must be registered.
/// </summary>
public sealed class DataSourcePublishValidator(DataSourceRuntime runtime, IMemberGroupService groups, DataSourceCatalog catalog,
    IEnumerable<IFormResource> code, DataSourceHandlers? handlers = null, Code.CompiledTypes? compiled = null, BindVariableProviders? providers = null)
    : INotificationAsyncHandler<ContentPublishingNotification>
{
    public async Task HandleAsync(ContentPublishingNotification notification, CancellationToken cancellationToken)
    {
        foreach (var content in notification.PublishedEntities.Where(c => c.ContentType.Alias == DataSourceSchema.SqlAlias))
        {
            var warnings = new List<string>();
            var errors = await ValidateAsync(content.Key, content.Name ?? "", alias => content.GetValue(alias), warnings);
            if (errors.Count > 0)
            {
                notification.CancelOperation(new EventMessage("Data source", (content.Name ?? "Data source") + ": " + string.Join(" ", errors), EventMessageType.Error));
                continue;
            }
            if (Warning(content.Key, content.Name ?? "", alias => content.GetValue(alias)) is { } warning) warnings.Insert(0, warning);
            if (warnings.Count > 0)
                notification.Messages.Add(new EventMessage("Data source", (content.Name ?? "Data source") + ": " + string.Join(" ", warnings), EventMessageType.Warning));
        }
    }

    /// <summary>
    /// What publishing warns about without refusing: <c>:APP.</c> names no bind variable provider declares. A warning, because the
    /// providers are code, which may arrive after the content that names their values; until then such a name binds NULL.
    /// </summary>
    public string? Warning(Guid key, string name, Func<string, object?> value)
        => providers is not null && DataSourceDefinition.Read(key, name, value, _ => [], new List<string>()) is { } definition
            ? providers.UndeclaredWarning(definition.ApplicationNames()) : null;

    /// <param name="value">Returns the value of a property of the node being published.</param>
    public Task<IReadOnlyList<string>> ValidateAsync(Guid key, string name, Func<string, object?> value) => ValidateAsync(key, name, value, new List<string>());

    /// <param name="value">Returns the value of a property of the node being published.</param>
    /// <param name="warnings">Receives what the database check reports without refusing, such as writes through a view.</param>
    public async Task<IReadOnlyList<string>> ValidateAsync(Guid key, string name, Func<string, object?> value, ICollection<string> warnings)
    {
        var errors = new List<string>();
        var names = new MemberGroupNames((await groups.GetAllAsync()).ToArray());
        var definition = DataSourceDefinition.Read(key, name, value, names.Resolve, errors);
        await catalog.LoadAsync();
        if (definition is null) return errors;
        if (code.Concat(compiled?.Create<IFormResource>() ?? []).Any(resource => resource.Alias == definition.Alias))
            errors.Add($"The alias {definition.Alias} belongs to a form resource registered in code; choose another.");
        if (catalog.DataSources.FirstOrDefault(source => source.Alias == definition.Alias && source.Definition.Key != key) is { } other)
            errors.Add($"The alias {definition.Alias} is already used by the data source {other.Definition.Name}.");
        if (definition.Handler is { } handler && handlers?.Find(handler) is null)
            errors.Add($"The backing class {handler} is not registered in code; choose a registered one or clear it.");
        errors.AddRange(runtime.Validate(definition, warnings));
        return errors;
    }
}

/// <summary>
/// Gives data source, list of values and API endpoint nodes, and the companions' definition nodes they register with
/// <see cref="Connections.AppBlocksBuilder.AddKeySegmentContentType"/>, a URL segment of their own key, so a node named like a page
/// ("Orders") can never take that page's URL. The nodes have no template, so their own URLs show nothing.
/// </summary>
public sealed class DataSourceUrlSegmentProvider(Connections.AppBlocksBuilder? builder = null) : IUrlSegmentProvider
{
    private static readonly string[] Aliases = [DataSourceSchema.RootAlias, DataSourceSchema.SqlAlias, PublishedDataCatalog.ListAlias, Api.ApiEndpointCatalog.RootAlias, Api.ApiEndpointCatalog.Alias];

    public string? GetUrlSegment(IContentBase content, string? culture = null)
        => Aliases.Contains(content.ContentType.Alias) || builder?.KeySegmentContentTypes.Contains(content.ContentType.Alias) == true
            ? "data-source-" + content.Key.ToString("N") : null;
}

public static class DataSourceExtensions
{
    /// <summary>
    /// Registers the data source engine: published <c>sqlDataSource</c> nodes become form resources by alias,
    /// publishing validates them against their database, and their URLs stay out of the way of pages. Connections
    /// are the ones registered with AppBlocks. Add <see cref="IDataSourcePolicy"/> implementations for
    /// rules a row restriction cannot express, and <see cref="IDataSourceHandler"/> backing classes for data sources to pick.
    /// </summary>
    public static IUmbracoBuilder AddDataSources(this IUmbracoBuilder builder)
    {
        builder.Services.AddOptions<DataSourceOptions>().Bind(builder.Config.GetSection(DataSourceOptions.Section))
            .Validate(options => options.InRange(), "AppBlocks:DataSources: CommandTimeoutSeconds cannot be negative, and MaxChanges, ReceiptLifetimeMinutes and ReceiptCapacity must be at least 1.");
        // A load-balanced site registers a store its servers share; one registered before or after this one replaces it.
        builder.Services.TryAddSingleton<IDataSourceReceiptStore, DataSourceReceipts>();
        builder.Services.AddSingleton<DataSourceRuntime>();
        builder.Services.TryAddScoped<IDataSourceUserResolver, UmbracoDataSourceUsers>();
        builder.Services.TryAddScoped<BindVariableProviders>();
        builder.Services.TryAddScoped<DataSourceHandlers>();
        builder.Services.AddHostedService<DataSourceHandlerValidation>();
        builder.Services.AddScoped<DataSourceCatalog>();
        builder.Services.AddScoped<IFormResourceProvider>(services => services.GetRequiredService<DataSourceCatalog>());
        builder.AddNotificationAsyncHandler<ContentPublishingNotification, DataSourcePublishValidator>();
        builder.UrlSegmentProviders().Insert<DataSourceUrlSegmentProvider>();
        builder.AddPublishedDataDefinitions();
        return builder;
    }
}
