using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;

namespace AppBlocks.Management;

/// <summary>A settings view contributed by an installed companion package.</summary>
public sealed record AppBlocksDashboardPage(string Alias, string Label, string Script, string Element);

[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("appblocks")]
[ApiExplorerSettings(GroupName = "AppBlocks")]
public sealed class AppBlocksDashboardPagesController(IEnumerable<AppBlocksDashboardPage> pages) : ManagementApiControllerBase
{
    [HttpGet("dashboard-pages")]
    public IActionResult List() => Ok(pages);
}
