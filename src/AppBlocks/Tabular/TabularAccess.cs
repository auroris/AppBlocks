using System.Security.Claims;
using AppBlocks.Blocks;
using AppBlocks.DataSources;
using AppBlocks.Forms;

namespace AppBlocks.Tabular;

/// <summary>
/// One request's standing with a Tabular form block: the block, the person, the child data source and the scope (the
/// link field and the owner's key) its rows are read and written in, which is null for a form's record not saved yet.
/// <see cref="Status"/> is 200 when everything resolved; otherwise the HTTP status to answer with and <see cref="Problem"/> says why.
/// </summary>
public sealed record TabularScope(int Status, string? Problem = null, TabularBlock? Block = null, ClaimsPrincipal? User = null, SqlDataSource? Source = null,
    DataSourceScope? Scope = null, string? OwnerAlias = null, IReadOnlyDictionary<string, string>? Parameters = null)
{
    public static TabularScope Refused(int status, string problem) => new(status, problem);
}

/// <summary>
/// Resolves a Tabular form block as Attachments is resolved: the page's access, the block, the record that owns the
/// rows (loaded by its own resource for this caller), then the child data source, which must be a published data
/// source because the rows are read and written through the engine. The data source's own groups, allow flags and
/// policies decide each row operation after that.
/// </summary>
public sealed class TabularAccess(BlockOwnerAccess owners, FormResourceRegistry resources)
{
    /// <summary>The revision string the owner's record is loaded under; the block never compares it with a form's.</summary>
    public const string Definition = "tabular";

    private static readonly BlockOwnerWords Words = new("Tabular form", "these rows", "rows");

    public async Task<TabularScope> ResolveAsync(Guid pageKey, Guid blockKey, CancellationToken ct)
    {
        await resources.LoadAsync();
        var owner = await owners.ResolveAsync(pageKey, blockKey, AppBlockSchema.TabularAlias, Words, element => {
            var settings = TabularBlock.Read(element);
            return (settings.OwnerResource, settings.OwnerKey);
        }, Definition, ct, allowAnonymous: true);
        if (owner.Status != 200) return TabularScope.Refused(owner.Status, owner.Problem!);
        if (Source(owner.Item!.Content, out var block) is not { } source) return Unresolved(block);
        return new(200, null, block, owner.User, source, new DataSourceScope(block.LinkField, owner.OwnerKey), owner.Resource!.Alias, owner.Parameters);
    }

    /// <summary>
    /// The block inside a form whose record is not saved yet, which the form will save with the block's new rows: the same checks,
    /// no owner to load, and a form that offers this caller a new record. Its scope is null.
    /// </summary>
    public async Task<TabularScope> ResolveNewAsync(Guid pageKey, Guid blockKey, CancellationToken ct)
    {
        await resources.LoadAsync();
        var owner = await owners.ResolveAsync(pageKey, blockKey, AppBlockSchema.TabularAlias, Words, element => {
            var settings = TabularBlock.Read(element);
            return (settings.OwnerResource, settings.OwnerKey);
        }, Definition, ct, allowAnonymous: true, newRecord: true);
        if (owner.Status != 200) return TabularScope.Refused(owner.Status, owner.Problem!);
        if (Source(owner.Item!.Content, out var block) is not { } source) return Unresolved(block);
        var form = owner.Form!;
        var context = new FormExecutionContext(owner.User!, pageKey, form.Block.ContentKey, form.Revision, Parameters: owner.Parameters);
        var initialized = await form.Resource.InitializeAsync(context, ct);
        if (initialized.Status == 403) return TabularScope.Refused(403, "You do not have access to these rows.");
        if (form.ViewOnly || initialized.Record?.Capabilities.Create != true) return TabularScope.Refused(404, "Save the record before adding rows.");
        return new(200, null, block, owner.User, source, null, owner.Resource!.Alias, owner.Parameters);
    }

    private SqlDataSource? Source(Umbraco.Cms.Core.Models.PublishedContent.IPublishedElement element, out TabularBlock block)
    {
        block = TabularBlock.Read(element);
        return block.DataSource.Length > 0 && block.LinkField.Length > 0 && resources.Find(block.DataSource) is SqlDataSource { Problem: null } source ? source : null;
    }

    private TabularScope Unresolved(TabularBlock block)
    {
        if (block.DataSource.Length == 0 || block.LinkField.Length == 0) return TabularScope.Refused(422, "This Tabular form block names no data source or no link field.");
        if (resources.Find(block.DataSource) is not SqlDataSource source) return TabularScope.Refused(422, $"The data source {block.DataSource} is not a published data source.");
        return TabularScope.Refused(503, $"The data source {block.DataSource} is not available: {source.Problem}");
    }
}
