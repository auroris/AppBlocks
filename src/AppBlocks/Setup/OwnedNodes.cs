using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AppBlocks.Blocks;
using AppBlocks.DataSources;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Services;

namespace AppBlocks.Setup;

/// <summary>The name and property values of one culture of an <see cref="OwnedNode"/> whose document type varies by culture.</summary>
public sealed record OwnedNodeCulture(string Name, IReadOnlyDictionary<string, object?> Values);

/// <summary>
/// A content node a package generates from code and owns, such as an API endpoint or a list of values whose SQL the package writes: its
/// key, name, document type alias and property values as the backoffice stores them, by property alias. A value of null clears the
/// property; a property the set does not name keeps whatever it holds.
/// </summary>
/// <param name="Parent">The key of the node it sits under; null for the root of the content tree.</param>
public sealed record OwnedNode(Guid Key, string Name, string ContentType, IReadOnlyDictionary<string, object?> Values, Guid? Parent = null)
{
    /// <summary>For a document type that varies by culture, each culture's name and values by culture code; <see cref="Values"/> then holds the invariant properties.</summary>
    public IReadOnlyDictionary<string, OwnedNodeCulture>? Cultures { get; init; }
}

/// <summary>What <see cref="OwnedNodeInstaller"/> did with one node.</summary>
public enum OwnedNodeChange
{
    /// <summary>The node is there and its definition has not changed since it was last published.</summary>
    Unchanged,
    /// <summary>The node was created and published.</summary>
    Created,
    /// <summary>The definition changed, or the node was in the recycle bin, so the node was updated and published.</summary>
    Updated,
    /// <summary>The node was installed before and has since been deleted or put in the recycle bin, and the caller asked to leave it so.</summary>
    LeftDeleted,
}

/// <summary>A node an <see cref="OwnedNodeInstaller"/> could not publish, with the reasons Umbraco and AppBlocks' publish checks gave.</summary>
public sealed class OwnedNodeException(string message) : InvalidOperationException(message);

/// <summary>
/// Keeps nodes a package generates from code current without rewriting them on every start. Each node's definition has a SHA-256
/// fingerprint, stored in Umbraco's key-value table under <c>AppBlocks:OwnedNode:&lt;key&gt;</c> once the node is published. A start
/// whose definition has the stored fingerprint, and whose node is there, changes nothing; a changed definition updates the node and
/// publishes it, which runs the publish checks of the node's type, such as an endpoint's SQL against its database. Call it from the
/// package's own startup code, in a request or service scope.
/// </summary>
/// <remarks>
/// An editor's change to an owned node lasts until the package's definition next changes, when the generated values replace the
/// properties the definition names. Keep a node an editor should own out of the package's definitions.
/// </remarks>
public sealed class OwnedNodeInstaller(IContentService contents, IKeyValueService keyValues, ICoreScopeProvider scopes, ILogger<OwnedNodeInstaller> logger)
{
    public const string KeyPrefix = "AppBlocks:OwnedNode:";

    /// <summary>The key-value entry that holds a node's fingerprint.</summary>
    public static string StoreKey(Guid node) => KeyPrefix + node.ToString("D");

    /// <summary>
    /// The SHA-256 of a definition, over its name, type, parent and values with property aliases and cultures in ordinal order, so the
    /// order a package builds its dictionaries in does not matter.
    /// </summary>
    public static string Fingerprint(OwnedNode node)
    {
        static SortedDictionary<string, object?> Sorted(IReadOnlyDictionary<string, object?> values) => new(values.ToDictionary(), StringComparer.Ordinal);
        var canonical = new {
            node.Name, node.ContentType, node.Parent, Values = Sorted(node.Values),
            Cultures = node.Cultures is null ? null
                : new SortedDictionary<string, object>(node.Cultures.ToDictionary(c => c.Key, c => (object)new { c.Value.Name, Values = Sorted(c.Value.Values) }), StringComparer.Ordinal),
        };
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical))));
    }

    /// <summary>Installs each node in order, so a parent listed before its children is there for them.</summary>
    public IReadOnlyList<OwnedNodeChange> Install(IEnumerable<OwnedNode> nodes, bool recreateDeleted = true)
        => nodes.Select(node => Install(node, recreateDeleted)).ToArray();

    /// <summary>
    /// Brings one node to its definition. A node not yet installed is created and published. A node whose definition changed is updated
    /// and published. A node installed before and since deleted, or in the recycle bin, is put back when <paramref name="recreateDeleted"/>
    /// is true, and otherwise left as the editor left it.
    /// </summary>
    /// <exception cref="OwnedNodeException">Publishing failed, such as a publish check refusing the definition; nothing is recorded, so the next call tries again.</exception>
    public OwnedNodeChange Install(OwnedNode node, bool recreateDeleted = true)
    {
        ArgumentNullException.ThrowIfNull(node);
        var fingerprint = Fingerprint(node);
        using var scope = scopes.CreateCoreScope();
        // Before any read, as the schema steps do, so a SQLite read transaction is never upgraded while startup writes elsewhere.
        scope.EagerWriteLock(Constants.Locks.KeyValues);
        var stored = keyValues.GetValue(StoreKey(node.Key));
        var existing = contents.GetById(node.Key);
        var live = existing is { Trashed: false };
        if (live && stored == fingerprint) { scope.Complete(); return OwnedNodeChange.Unchanged; }
        if (!live && stored is not null && !recreateDeleted) { scope.Complete(); return OwnedNodeChange.LeftDeleted; }

        var parent = node.Parent is { } key ? contents.GetById(key) ?? throw new OwnedNodeException($"The parent {key} of {node.Name} does not exist.") : null;
        var parentId = parent?.Id ?? Constants.System.Root;
        var content = existing ?? contents.Create(node.Name, parentId, node.ContentType);
        if (content.ContentType.Alias != node.ContentType)
            throw new OwnedNodeException($"The node {node.Key} is a {content.ContentType.Alias}, not a {node.ContentType}; delete it, or give the definition another key.");
        content.Key = node.Key;
#pragma warning disable CS0618 // The content service's synchronous Save, Move and Publish, as the package's own installers use them.
        if (existing is { Trashed: true } || existing is not null && existing.ParentId != parentId) contents.Move(content, parentId);
        if (content.ContentType.VariesByCulture())
        {
            if (node.Cultures is not { Count: > 0 } cultures) throw new OwnedNodeException($"The document type {node.ContentType} varies by culture, so {node.Name} needs Cultures.");
            foreach (var (culture, variant) in cultures)
            {
                content.SetCultureName(variant.Name, culture);
                foreach (var (alias, value) in variant.Values) content.SetValue(alias, value, culture);
            }
        }
        else content.Name = node.Name;
        foreach (var (alias, value) in node.Values) content.SetValue(alias, value);
#pragma warning restore CS0618
        Publish(content, node.Name);
        keyValues.SetValue(StoreKey(node.Key), fingerprint);
        scope.Complete();
        var change = existing is null ? OwnedNodeChange.Created : OwnedNodeChange.Updated;
        logger.LogInformation("{Change} the owned node {Name} ({Key}) to its definition {Fingerprint}.", change, node.Name, node.Key, fingerprint[..12]);
        return change;
    }

    /// <summary>The key <see cref="EnsureDataSourcesRoot"/> gives the Data sources node it creates.</summary>
    public static readonly Guid DataSourcesRootKey = AppBlockSchema.Key("dataSourcesRoot");

    /// <summary>
    /// The key of a published Data sources node (<c>dataSources</c>) at the content root, the parent an <see cref="OwnedNode"/> data source,
    /// list of values or API endpoint names. A site that has one keeps it, and it is published if it was not. A site with none gets one
    /// named <paramref name="name"/> under <see cref="DataSourcesRootKey"/>, created and published. Call it before installing those nodes.
    /// </summary>
    /// <exception cref="OwnedNodeException">The node could not be published.</exception>
    public Guid EnsureDataSourcesRoot(string name = "Data sources")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        using var scope = scopes.CreateCoreScope();
        // Before any read, as in Install, so a SQLite read transaction is never upgraded.
        scope.EagerWriteLock(Constants.Locks.ContentTree);
        var roots = contents.GetRootContent().Where(content => !content.Trashed && content.ContentType.Alias == DataSourceSchema.RootAlias)
            .OrderByDescending(content => content.Published).ThenByDescending(content => content.Key == DataSourcesRootKey).ThenBy(content => content.SortOrder).ToArray();
        var root = roots.FirstOrDefault();
        if (root is { Published: true }) { scope.Complete(); return root.Key; }
        if (root is null)
        {
#pragma warning disable CS0618 // The content service's synchronous Move, as Install uses it.
            if (contents.GetById(DataSourcesRootKey) is { } trashed) { root = trashed; contents.Move(root, Constants.System.Root); }
#pragma warning restore CS0618
            else root = contents.Create(name, Constants.System.Root, DataSourceSchema.RootAlias);
            root.Key = DataSourcesRootKey;
        }
        Publish(root, root.Name ?? name);
        scope.Complete();
        logger.LogInformation("Published the Data sources node {Name} ({Key}).", root.Name, root.Key);
        return root.Key;
    }

    private void Publish(IContent content, string name)
    {
#pragma warning disable CS0618 // The content service's synchronous Save and Publish, as the package's own installers use them.
        contents.Save(content);
        var result = contents.Publish(content, ["*"]);
#pragma warning restore CS0618
        if (!result.Success)
            throw new OwnedNodeException($"{name} could not be published: {result.Result}. "
                + string.Join(" ", result.EventMessages?.GetAll().Select(message => message.Message) ?? []));
    }
}
