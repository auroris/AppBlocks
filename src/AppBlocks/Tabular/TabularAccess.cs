using System.Security.Claims;
using AppBlocks.Blocks;
using AppBlocks.DataSources;
using AppBlocks.Forms;

namespace AppBlocks.Tabular;

/// <summary>
/// One request's standing with a Tabular form block: the block, the person, the child data source and the scope (the
/// link field and the owner's key) its rows are read and written in. <see cref="Status"/> is 200 when everything
/// resolved; otherwise the HTTP status to answer with and <see cref="Problem"/> says why.
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
        var owner = await owners.ResolveAsync(pageKey, blockKey, AppBlockSchema.TabularAlias, Words, element => {
            var settings = TabularBlock.Read(element);
            return (settings.OwnerResource, settings.OwnerKey);
        }, Definition, ct, allowAnonymous: true);
        if (owner.Status != 200) return TabularScope.Refused(owner.Status, owner.Problem!);
        var block = TabularBlock.Read(owner.Item!.Content);
        if (block.DataSource.Length == 0 || block.LinkField.Length == 0) return TabularScope.Refused(422, "This Tabular form block names no data source or no link field.");
        if (resources.Find(block.DataSource) is not SqlDataSource source) return TabularScope.Refused(422, $"The data source {block.DataSource} is not a published data source.");
        if (source.Problem is { } problem) return TabularScope.Refused(503, $"The data source {block.DataSource} is not available: {problem}");
        return new(200, null, block, owner.User, source, new DataSourceScope(block.LinkField, owner.OwnerKey), owner.Resource!.Alias, owner.Parameters);
    }
}
