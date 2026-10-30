using Asp.Versioning;
using InteractiveReport.AspNetCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using UmbracoAppHost.DataSources;
using UmbracoAppHost.Forms;
using UmbracoAppHost.Reports;

namespace UmbracoAppHost.Blocks;

/// <summary>
/// The catalogue the back-office pickers read: report sources, and form resources with their fields. Data sources
/// are listed beside the C# resources, with the column behind each field and why one is not available, if it is not.
/// </summary>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("application-blocks")]
[ApiExplorerSettings(GroupName = "Application blocks")]
public sealed class AppBlocksController(FormResourceRegistry resources,
    IReportAccessPolicy access, PublishedDataCatalog definitions) : ManagementApiControllerBase
{
    [HttpGet("catalogue")]
    public async Task<IActionResult> Catalogue()
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(new {
            reports = definitions.Reports.Select(r => new { alias = r.Key, label = r.Value.Title ?? r.Key }).OrderBy(r => r.label),
            lists = definitions.Lists.Values.Select(list => new { alias = list.Alias, key = list.Key, label = list.Name }).OrderBy(list => list.label),
            resources = resources.Resources.Values.Select(r => new {
                alias = r.Alias, version = r.ContractVersion, fields = r.Fields,
                dataSource = r is SqlDataSource source ? source.Describe() : null,
            }).OrderBy(r => r.alias),
            canPreview = await access.IsAdministratorAsync(User),
        });
    }
}
