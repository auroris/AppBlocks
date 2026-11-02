using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using AppBlocks.DataSources;
using AppBlocks.Forms;

namespace AppBlocks.Blocks;

/// <summary>
/// The catalogue the back-office pickers read: lists, form resources, data source backing classes and optional companion
/// choices. Data sources are listed beside the C# resources, with the column behind each field and why one is not available, if it is not.
/// </summary>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("application-blocks")]
[ApiExplorerSettings(GroupName = "Application blocks")]
public sealed class AppBlocksController(FormResourceRegistry resources, DataSourceHandlers handlers,
    PublishedDataCatalog definitions, IEnumerable<IAppBlocksCatalogueContributor> contributors) : ManagementApiControllerBase
{
    [HttpGet("catalogue")]
    public async Task<IActionResult> Catalogue()
    {
        Response.Headers.CacheControl = "no-store";
        var catalogue = new Dictionary<string, object> {
            ["formClasses"] = resources.RegisteredResources.Values.Select(r => new { alias = r.Alias, fields = r.Fields }).OrderBy(r => r.alias),
            ["dataSourceHandlers"] = handlers.All.Keys.Order(StringComparer.Ordinal).Select(alias => new { alias }),
            ["lists"] = definitions.Lists.Values.Select(list => new { alias = list.Alias, key = list.Key, label = list.Name }).OrderBy(list => list.label),
            ["resources"] = resources.Resources.Values.Select(r => new {
                alias = r.Alias, version = r.ContractVersion, fields = r.Fields,
                dataSource = r is SqlDataSource source ? source.Describe() : null,
            }).OrderBy(r => r.alias),
        };
        foreach (var contributor in contributors) await contributor.PopulateAsync(catalogue, User);
        return Ok(catalogue);
    }
}
