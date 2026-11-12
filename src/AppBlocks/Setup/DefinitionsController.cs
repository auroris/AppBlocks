using AppBlocks.Setup;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using AppBlocks.Authentication;
using Umbraco.Cms.Web.Common.Authorization;

// Public namespace retained for source compatibility; the file lives with its owning feature.
namespace AppBlocks.Management;

/// <summary>The definitions each package installs in Umbraco: where they stand, and the reset and removal an administrator can ask for.</summary>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("appblocks")]
[ApiExplorerSettings(GroupName = "AppBlocks")]
[Authorize(Policy = AppBlocksAuthorization.AdministratorPolicy)]
public sealed class DefinitionsController(SchemaInstaller installer) : ManagementApiControllerBase
{
    [HttpGet("definitions")]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await installer.StatusAsync(ct));
    }

    [HttpPost("definitions/reinstall")]
    [Authorize(Policy = AuthorizationPolicies.SectionAccessSettings)]
    public async Task<IActionResult> Reinstall(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        try { return Ok(await installer.ReinstallAsync(ct)); }
        catch (InvalidOperationException error) { return Problem(error.Message, statusCode: 422); }
    }

    [HttpPost("definitions/uninstall")]
    [Authorize(Policy = AuthorizationPolicies.SectionAccessSettings)]
    public async Task<IActionResult> Uninstall(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        try { return Ok(await installer.UninstallAsync(ct)); }
        catch (InvalidOperationException error) { return Problem(error.Message, statusCode: 422); }
    }
}
