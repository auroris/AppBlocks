using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Web.Common.Authorization;

namespace AppBlocks.Documents.Management;

[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("appblocks/documents")]
[ApiExplorerSettings(GroupName = "AppBlocks.Documents")]
[Authorize(Policy = AuthorizationPolicies.SectionAccessSettings)]
public sealed class DocumentsSettingsController(DocumentSettingsStore settings, IBackOfficeSecurityAccessor security)
    : ManagementApiControllerBase
{
    private bool Admin => security.BackOfficeSecurity?.CurrentUser?.IsAdmin() == true;

    [HttpGet("settings")]
    public IActionResult Get()
    {
        if (!Admin) return Forbid();
        Response.Headers.CacheControl = "no-store";
        return Ok(settings.Get());
    }

    [HttpPost("settings")]
    public async Task<IActionResult> Save(SaveDocumentSettings request, CancellationToken ct)
    {
        if (!Admin) return Forbid();
        Response.Headers.CacheControl = "no-store";
        try { await settings.SaveAsync(request, ct); return Ok(settings.Get()); }
        catch (ArgumentException error) { return Problem(error.Message, statusCode: 422); }
    }
}
