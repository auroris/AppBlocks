using AppBlocks.AntiSpam;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using AppBlocks.Authentication;
using Umbraco.Cms.Web.Common.Authorization;

// Public namespace retained for source compatibility; the file lives with its owning feature.
namespace AppBlocks.Management;

/// <summary>One row of the Anti-spam settings: where the secret key comes from, never what it is.</summary>
/// <param name="Secret">none, configuration, saved or unreadable.</param>
/// <param name="SiteKey">Public, so it is shown.</param>
public sealed record AntiSpamServiceStatus(string Alias, string Name, string Secret, string? SiteKey, bool SiteKeyManaged, string? Problem, DateTimeOffset? ProblemSince);

[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("appblocks")]
[ApiExplorerSettings(GroupName = "AppBlocks")]
[Authorize(Policy = AppBlocksAuthorization.AdministratorPolicy)]
public sealed class AntiSpamSettingsController(AntiSpamGate gate, AntiSpamSettings settings, AntiSpamHealth health) : ManagementApiControllerBase
{
    private AntiSpamServiceStatus[] Statuses() => gate.Services.Select(service =>
    {
        var problem = health.Current(service.Alias);
        return new AntiSpamServiceStatus(service.Alias, service.Name, settings.SecretState(service.Alias).ToString().ToLowerInvariant(), settings.SiteKey(service.Alias),
            settings.ConfiguredSiteKey(service.Alias) is not null, problem?.Message, problem?.Since);
    }).ToArray();

    [HttpGet("anti-spam")]
    public IActionResult List()
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(Statuses());
    }

    [HttpPost("anti-spam")]
    [Authorize(Policy = AuthorizationPolicies.SectionAccessSettings)]
    public async Task<IActionResult> Save(SaveAntiSpamKeys request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (gate.Find(request.Service) is not { } service) return Problem("Choose one of the services this site offers.", statusCode: 422);
        try { await settings.SaveAsync(request with { Service = service.Alias }, ct); }
        catch (ArgumentException error) { return Problem(error.Message, statusCode: 422); }
        // New keys deserve a fresh look; if they are wrong too, the next submission says so.
        health.Clear(service.Alias);
        return Ok(Statuses());
    }
}
