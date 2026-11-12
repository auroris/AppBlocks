using AppBlocks.Authentication;
using AppBlocks.Connections;
using AppBlocks.Databases;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Web.Common.Authorization;

namespace AppBlocks.Management;

/// <summary>
/// The Settings screens' connections and database setup. The guide, the connection list, the database types and the table list are open
/// to every backoffice user, because the data source and report pickers read them, as is whether the User is an AppBlocks administrator;
/// everything else is for administrators.
/// </summary>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("appblocks")]
[ApiExplorerSettings(GroupName = "AppBlocks")]
public sealed class AppBlocksSettingsController(ConnectionCatalog connections, ConnectionTables tables, IEnumerable<IDatabaseSetupFeature> setup)
    : ManagementApiControllerBase
{
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

    /// <summary>
    /// Whether the signed-in User passes AppBlocks administration (<see cref="AppBlocksAuthorization.AdministratorPolicy"/>), so an
    /// editor screen can leave out what only administrators may use, such as a data source's Test. Every such endpoint still checks.
    /// </summary>
    [HttpGet("administrator")]
    public async Task<IActionResult> Administrator([FromServices] ApplicationAccessPolicy access)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(new { allowed = await access.IsBackOfficeAdministratorAsync(User) });
    }

    /// <summary>The database types a connection can have, in the order the connection form offers them.</summary>
    [HttpGet("database-kinds")]
    public IActionResult Kinds() => Ok(DatabaseKinds.All.Select(kind => new { provider = kind.ProviderName(), name = kind.DisplayName() }));

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
    [Authorize(Policy = AppBlocksAuthorization.AdministratorPolicy)]
    public async Task<IActionResult> DatabaseSetup(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var statuses = new List<DatabaseFeatureStatus>();
        foreach (var feature in setup) statuses.Add(await feature.StatusAsync(ct));
        return Ok(statuses);
    }

    [HttpGet("database-setup/{feature}/script")]
    [Authorize(Policy = AppBlocksAuthorization.AdministratorPolicy)]
    public async Task<IActionResult> DatabaseSetupScript(string feature, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return setup.FirstOrDefault(item => item.Alias.Equals(feature, StringComparison.OrdinalIgnoreCase)) is { } found && await found.ScriptAsync(ct) is { } script
            ? Content(script, "text/plain; charset=utf-8") : NotFound();
    }

    [HttpPost("connections")]
    [Authorize(Policy = AuthorizationPolicies.SectionAccessSettings)]
    [Authorize(Policy = AppBlocksAuthorization.AdministratorPolicy)]
    public async Task<IActionResult> Save(SaveConnection request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        try { await connections.SaveAsync(request, ct); return Ok(connections.List()); }
        catch (Exception error) when (error is ArgumentException or DatabaseDrivers.MissingDriverException) { return Problem(error.Message, statusCode: 422); }
    }

    [HttpPost("connections/test")]
    [Authorize(Policy = AuthorizationPolicies.SectionAccessSettings)]
    [Authorize(Policy = AppBlocksAuthorization.AdministratorPolicy)]
    public async Task<IActionResult> Test(SaveConnection request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        try { await connections.TestAsync(request, ct); return Ok(new { message = "Connection successful." }); }
        catch (DatabaseDrivers.MissingDriverException error) { return Problem(error.Message, statusCode: 422); }
        catch (Exception) when (!ct.IsCancellationRequested)
        { return Problem("Connection failed or timed out. Check the database type, connection string, network access and database permissions.", statusCode: 422); }
    }
}
