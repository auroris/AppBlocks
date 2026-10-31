using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using AppBlocks.InteractiveReports.DataSources;

namespace AppBlocks.InteractiveReports.Reports;

public sealed record ReportSourcePreviewRequest(string DataSource);

[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("appblocks")]
[ApiExplorerSettings(GroupName = "AppBlocks")]
public sealed class ReportSourcePreviewController(PublishedReportCatalog definitions, ReportSourcePreviews previews,
    IReportAccessPolicy access) : ManagementApiControllerBase
{
    [HttpPost("report-preview")]
    public async Task<IActionResult> Create(ReportSourcePreviewRequest request)
    {
        Response.Headers.CacheControl = "no-store";
        if (!await access.IsAdministratorAsync(User)) return Forbid();
        var errors = new List<string>();
        var report = definitions.ReadBlockReport(Guid.NewGuid(), property => property == "dataSource" ? request.DataSource : null, errors);
        return report is null ? Problem(string.Join(" ", errors), statusCode: 422) : Ok(new { name = previews.Add(report, User) });
    }
}
