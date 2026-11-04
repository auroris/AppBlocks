using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Web.Common.Authorization;

namespace AppBlocks.InteractiveReports.Reports;

/// <summary>The backoffice user groups, by alias and name, that a report block's group pickers offer to an editor of content.</summary>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("appblocks")]
[ApiExplorerSettings(GroupName = "AppBlocks")]
[Authorize(Policy = AuthorizationPolicies.SectionAccessContent)]
public sealed class ReportUserGroupsController(IUserGroupService userGroups) : ManagementApiControllerBase
{
    [HttpGet("report-user-groups")]
    public async Task<IActionResult> Groups()
    {
        Response.Headers.CacheControl = "no-store";
        return Ok((await userGroups.GetAllAsync(0, 1000)).Items.Select(g => new { alias = g.Alias, name = g.Name })
            .OrderBy(g => g.name, StringComparer.OrdinalIgnoreCase));
    }
}
