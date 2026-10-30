using AppBlocks.Connections;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Web.Common.Authorization;

namespace AppBlocks.Management;

[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("appblocks")]
[ApiExplorerSettings(GroupName = "AppBlocks")]
public sealed class AppBlocksSettingsController(ConnectionCatalog connections, ConnectionTables tables,
    IBackOfficeSecurityAccessor security) : ManagementApiControllerBase
{
    private bool Admin => security.BackOfficeSecurity?.CurrentUser?.IsAdmin() == true;

    [HttpGet("guide")]
    public IActionResult Guide()
    {
        using var stream = typeof(AppBlocksSettingsController).Assembly.GetManifestResourceStream("AppBlocks.BusinessRules.md")!;
        using var reader = new StreamReader(stream);
        return Content(reader.ReadToEnd(), "text/markdown; charset=utf-8");
    }

    [HttpGet("connections")]
    public IActionResult Connections()
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(connections.List());
    }

    [HttpGet("tables")]
    public async Task<IActionResult> Tables(string connection = ConnectionCatalog.ApplicationConnection, CancellationToken ct = default)
    {
        Response.Headers.CacheControl = "no-store";
        try { return Ok(await tables.ListAsync(connection, ct)); }
        catch (Exception error) when (error is not OperationCanceledException)
        { return Problem("The database tables could not be read. Check the connection and its database permissions.", statusCode: 422); }
    }

    [HttpPost("connections")]
    [Authorize(Policy = AuthorizationPolicies.SectionAccessSettings)]
    public async Task<IActionResult> Save(SaveConnection request, CancellationToken ct)
    {
        if (!Admin) return Forbid();
        Response.Headers.CacheControl = "no-store";
        try { await connections.SaveAsync(request, ct); return Ok(connections.List()); }
        catch (ArgumentException error) { return Problem(error.Message, statusCode: 422); }
    }

    [HttpPost("connections/test")]
    [Authorize(Policy = AuthorizationPolicies.SectionAccessSettings)]
    public async Task<IActionResult> Test(SaveConnection request, CancellationToken ct)
    {
        if (!Admin) return Forbid();
        Response.Headers.CacheControl = "no-store";
        try { await connections.TestAsync(request, ct); return Ok(new { message = "Connection successful." }); }
        catch (Exception) when (!ct.IsCancellationRequested)
        { return Problem("Connection failed or timed out. Check the database type, connection string, network access and database permissions.", statusCode: 422); }
    }
}
