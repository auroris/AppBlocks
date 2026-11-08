using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using AppBlocks.Authentication;

namespace AppBlocks.Attachments;

/// <summary>What an administrator asks the purge for: how many days a removed file must have been gone. Empty means the configured retention.</summary>
public sealed record AttachmentPurgeRequest(int? RetentionDays);

/// <summary>
/// The administrator's purge, run by hand through the management API with a backoffice token: deletes the links
/// removed longer ago than the retention and every revision behind them. Nothing deletes removed files on its own.
/// </summary>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("attachments")]
[ApiExplorerSettings(GroupName = "Attachments")]
[Authorize(Policy = AppBlocksAuthorization.AdministratorPolicy)]
public sealed class AttachmentsManagementController(AttachmentStore store, IOptions<AttachmentSettings> settings) : ManagementApiControllerBase
{
    [HttpPost("purge")]
    public async Task<IActionResult> Purge([FromBody] AttachmentPurgeRequest? request, CancellationToken ct)
    {
        var days = request?.RetentionDays ?? settings.Value.RetentionDays;
        if (days < 0) return Problem("Retention days cannot be negative.", statusCode: 422);
        Response.Headers.CacheControl = "no-store";
        try
        {
            var purged = await store.PurgeAsync(DateTime.UtcNow.AddDays(-days), ct);
            return Ok(new { purged.Links, purged.Files, purged.Bytes, RetentionDays = days });
        }
        catch (AttachmentStoreException error) { return Problem(error.Message, statusCode: 503); }
    }
}
