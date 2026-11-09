using AppBlocks.Blocks;
using AppBlocks.DataSources;
using AppBlocks.Setup;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Services;

namespace AppBlocks.Tests;

/// <summary>A package's generated nodes, kept current by <see cref="OwnedNodeInstaller"/>: created, left alone, updated when the definition changes.</summary>
[Collection("Standalone Umbraco")]
public sealed class OwnedNodeTests
{
    private static readonly Guid ListKey = AppBlockSchema.Key("owned-node-test:customers");

    private static OwnedNode Customers(string sql) => new(ListKey, "Customers", PublishedDataCatalog.ListAlias, new Dictionary<string, object?> {
        ["alias"] = "owned.customers", ["connection"] = AppBlocks.Connections.ConnectionCatalog.ApplicationConnection, ["sql"] = sql,
        ["valueColumn"] = "ORDER_ID", ["displayColumn"] = "CUSTOMER", ["maximumRows"] = "100",
    });

    [Fact]
    public void The_fingerprint_follows_the_definition_and_ignores_the_order_values_were_added_in()
    {
        var one = new OwnedNode(ListKey, "Customers", "listOfValues", new Dictionary<string, object?> { ["a"] = "1", ["b"] = 2 });
        var other = one with { Values = new Dictionary<string, object?> { ["b"] = 2, ["a"] = "1" } };
        Assert.Equal(OwnedNodeInstaller.Fingerprint(one), OwnedNodeInstaller.Fingerprint(other));
        Assert.NotEqual(OwnedNodeInstaller.Fingerprint(one), OwnedNodeInstaller.Fingerprint(one with { Name = "Clients" }));
        Assert.NotEqual(OwnedNodeInstaller.Fingerprint(one), OwnedNodeInstaller.Fingerprint(one with { Values = new Dictionary<string, object?> { ["a"] = "1", ["b"] = 3 } }));
        Assert.Equal(64, OwnedNodeInstaller.Fingerprint(one).Length);
    }

    [Fact]
    public async Task A_generated_node_is_published_once_and_again_only_when_its_definition_changes()
    {
        await using var site = await BlockSite.StartAsync();
        OwnedNodeChange Install(OwnedNode node, bool recreateDeleted = true)
        {
            using var scope = site.CreateScope();
            return scope.ServiceProvider.GetRequiredService<OwnedNodeInstaller>().Install(node, recreateDeleted);
        }
        T With<T>(Func<IServiceProvider, T> read) { using var scope = site.CreateScope(); return read(scope.ServiceProvider); }

        var first = Customers("SELECT ORDER_ID, CUSTOMER FROM ORDERS");
        Assert.Equal(OwnedNodeChange.Created, Install(first));
        var published = With(services => services.GetRequiredService<IContentService>().GetById(ListKey)!);
        Assert.True(published.Published);
        var version = published.VersionId;
        Assert.Equal(OwnedNodeInstaller.Fingerprint(first), With(services => services.GetRequiredService<IKeyValueService>().GetValue(OwnedNodeInstaller.StoreKey(ListKey))));

        // The same definition at the next start writes nothing.
        Assert.Equal(OwnedNodeChange.Unchanged, Install(Customers("SELECT ORDER_ID, CUSTOMER FROM ORDERS")));
        Assert.Equal(version, With(services => services.GetRequiredService<IContentService>().GetById(ListKey)!.VersionId));

        // A changed definition is published, through the list's own publish check.
        var changed = Customers("SELECT ORDER_ID, CUSTOMER FROM ORDERS WHERE ORDER_ID > 4");
        Assert.Equal(OwnedNodeChange.Updated, Install(changed));
        Assert.Equal(changed.Values["sql"], With(services => services.GetRequiredService<IContentService>().GetById(ListKey)!.GetValue<string>("sql", published: true)));

        // A definition the publish check refuses throws, records nothing, and leaves the published node as it was.
        var broken = Customers("SELECT ORDER_ID, CUSTOMER FROM NO_SUCH_TABLE");
        Assert.Throws<OwnedNodeException>(() => Install(broken));
        Assert.Equal(OwnedNodeInstaller.Fingerprint(changed), With(services => services.GetRequiredService<IKeyValueService>().GetValue(OwnedNodeInstaller.StoreKey(ListKey))));
        Assert.Equal(changed.Values["sql"], With(services => services.GetRequiredService<IContentService>().GetById(ListKey)!.GetValue<string>("sql", published: true)));

        // A node an editor put in the recycle bin stays there when the package asks, and comes back otherwise.
        With(services => {
            var contents = services.GetRequiredService<IContentService>();
#pragma warning disable CS0618
            return contents.MoveToRecycleBin(contents.GetById(ListKey)!);
#pragma warning restore CS0618
        });
        Assert.Equal(OwnedNodeChange.LeftDeleted, Install(changed, recreateDeleted: false));
        Assert.True(With(services => services.GetRequiredService<IContentService>().GetById(ListKey)!.Trashed));
        Assert.Equal(OwnedNodeChange.Updated, Install(changed));
        var restored = With(services => services.GetRequiredService<IContentService>().GetById(ListKey)!);
        Assert.False(restored.Trashed);
        Assert.True(restored.Published);

        // A list with its own SQL and no connection reads the AppBlocks database, as a data source does.
        var errors = new List<string>();
        var unnamed = new Dictionary<string, object?>(first.Values) { ["connection"] = null };
        var list = With(services => services.GetRequiredService<PublishedDataCatalog>().ReadList(Guid.NewGuid(), "Customers", alias => unnamed.GetValueOrDefault(alias), errors));
        Assert.True(list is not null, string.Join(" ", errors));
        Assert.Equal(AppBlocks.Connections.ConnectionCatalog.ApplicationConnection, list.DataSource.Connection);
        // Its access is Public unless the list chooses Not public; Specific groups needs read groups a list cannot name.
        Assert.Equal(DataSourceAccess.Public, list.DataSource.Access);
        ListOfValuesDefinition? WithAccess(string access, List<string> problems) => With(services => services.GetRequiredService<PublishedDataCatalog>()
            .ReadList(Guid.NewGuid(), "Customers", alias => new Dictionary<string, object?>(unnamed) { ["access"] = access }.GetValueOrDefault(alias), problems));
        Assert.Equal(DataSourceAccess.NotPublic, WithAccess("Not public", errors)!.DataSource.Access);
        var refused = new List<string>();
        Assert.Null(WithAccess("Specific groups", refused));
        Assert.Contains(refused, error => error.Contains("Specific groups needs read groups"));
    }
}
