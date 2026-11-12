using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using AppBlocks.Authentication;
using Umbraco.Cms.Web.Common.Authorization;

// Public namespace retained for source compatibility; the file lives with its owning feature.
namespace AppBlocks.Documents.Management;

[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("appblocks/documents")]
[ApiExplorerSettings(GroupName = "AppBlocks.Documents")]
[Authorize(Policy = AuthorizationPolicies.SectionAccessSettings)]
[Authorize(Policy = AppBlocksAuthorization.AdministratorPolicy)]
public sealed class DocumentsSettingsController(DocumentSettingsStore settings, DocumentOutputService outputs)
    : ManagementApiControllerBase
{
    [HttpGet("settings")]
    public IActionResult Get()
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(settings.List());
    }

    [HttpPost("configurations")]
    public async Task<IActionResult> Save(SavePressroomConfiguration request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            if (request.Output?.StartsWith("printer:", StringComparison.Ordinal) == true)
            {
                var available = await outputs.DiscoverPrintersAsync(new(request.Id, request.Url, request.ApiKey, request.ClearApiKey, request.TimeoutSeconds), ct);
                if (!available.Printers.Contains(request.Output[8..], StringComparer.OrdinalIgnoreCase))
                    return Problem("That printer is no longer exposed by Pressroom. Refresh the printer list and select an available output.", statusCode: 422);
            }
            return Ok(await settings.SaveConfigurationAsync(request, ct));
        }
        catch (ArgumentException error) { return Problem(error.Message, statusCode: 422); }
        catch (KeyNotFoundException error) { return Problem(error.Message, statusCode: 404); }
        catch (Exception error) when (DiscoveryFailure(error, ct)) { return DiscoveryProblem(error); }
    }

    [HttpDelete("configurations/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        try { await settings.DeleteAsync(id, ct); return Ok(settings.List()); }
        catch (KeyNotFoundException error) { return Problem(error.Message, statusCode: 404); }
    }

    [HttpPost("printers")]
    public async Task<IActionResult> Printers(DiscoverPressroomPrinters request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        try { return Ok(await outputs.DiscoverPrintersAsync(request, ct)); }
        catch (ArgumentException error) { return Problem(error.Message, statusCode: 422); }
        catch (KeyNotFoundException error) { return Problem(error.Message, statusCode: 404); }
        catch (Exception error) when (DiscoveryFailure(error, ct)) { return DiscoveryProblem(error); }
    }

    private static bool DiscoveryFailure(Exception error, CancellationToken ct)
        => error is HttpRequestException or System.Text.Json.JsonException or IOException or System.Security.Cryptography.CryptographicException
            || error is OperationCanceledException && !ct.IsCancellationRequested;
    private IActionResult DiscoveryProblem(Exception error)
        => Problem(error is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden }
            ? "Pressroom rejected the API key. Check the key and try loading printers again."
            : "The printer list could not be loaded from Pressroom. Check the URL, API key and service availability. PDF remains available as an output.", statusCode: 502);
}
