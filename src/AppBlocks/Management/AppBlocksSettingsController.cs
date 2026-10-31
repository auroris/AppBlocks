using AppBlocks.Connections;
using AppBlocks.Databases;
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
    IBackOfficeSecurityAccessor security, IEnumerable<IDatabaseSetupFeature> setup) : ManagementApiControllerBase
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

    /// <summary>Where each feature's own tables stand. Looking changes nothing in any database.</summary>
    [HttpGet("database-setup")]
    public async Task<IActionResult> DatabaseSetup(CancellationToken ct)
    {
        if (!Admin) return Forbid();
        Response.Headers.CacheControl = "no-store";
        var statuses = new List<DatabaseFeatureStatus>();
        foreach (var feature in setup) statuses.Add(await feature.StatusAsync(ct));
        return Ok(statuses);
    }

    [HttpGet("database-setup/{feature}/script")]
    public async Task<IActionResult> DatabaseSetupScript(string feature, CancellationToken ct)
    {
        if (!Admin) return Forbid();
        Response.Headers.CacheControl = "no-store";
        return setup.FirstOrDefault(item => item.Alias.Equals(feature, StringComparison.OrdinalIgnoreCase)) is { } found && await found.ScriptAsync(ct) is { } script
            ? Content(script, "text/plain; charset=utf-8") : NotFound();
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
