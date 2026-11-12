using AppBlocks.InteractiveReports.Blocks;
using AppBlocks.Blocks;
using AppBlocks.InteractiveReports.DataSources;
using Asp.Versioning;
using InteractiveReport.AspNetCore;
using InteractiveReport.AspNetCore.Definitions;
using InteractiveReport.Core.SavedReports;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Core.Services.Navigation;
using Umbraco.Cms.Core.Web;

namespace AppBlocks.InteractiveReports.Reports;

[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("appblocks/report-administration")]
[ApiExplorerSettings(GroupName = "AppBlocks")]
public sealed class ReportAdministrationController(IReportAccessPolicy access, PublishedReportCatalog definitions,
    IUmbracoContextFactory contexts, IDocumentNavigationQueryService navigation, ISavedReportStore saved,
    SavedViewStore readiness, ILogger<ReportAdministrationController> logger) : ManagementApiControllerBase
{
    [HttpGet("access")]
    public async Task<IActionResult> Access()
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(new { allowed = await access.IsAdministratorAsync(User) });
    }

    [HttpGet("reports")]
    public Task<IActionResult> Reports(CancellationToken ct) => Manage(async () => {
        if (!await Allowed(InteractiveReportAction.ListAllSavedReports, "__saved-reports", null, null, ct)) return Forbid();
        if (!await readiness.ReadyAsync(ct)) return StorageUnavailable();
        // Only metadata leaves this endpoint. Source-page access still governs the report's data and saved state.
        return Ok((await saved.ListAll(ct)).OrderBy(report => report.Title).Select(report => new {
            id = report.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), report.ReportName, report.Title, report.Owner,
            isPublic = report.IsPublic, report.ModifiedUtc, readOnly = report.Origin != SavedReportOrigin.User,
        }));
    }, ct);

    [HttpPut("reports/{id:long}/owner")]
    public Task<IActionResult> Reassign(long id, ReassignReport request, CancellationToken ct) => Manage(async () => {
        if (!await access.IsAdministratorAsync(User)) return Forbid();
        if (!await readiness.ReadyAsync(ct)) return StorageUnavailable();
        if (await saved.Get(id, ct) is not { } report) return NotFound();
        var owner = request.Owner?.Trim();
        if (string.IsNullOrEmpty(owner) || owner.Length > 400) return Problem("Choose an owner or enter an account identity of at most 400 characters.", statusCode: 400);
        if (report.Origin != SavedReportOrigin.User) return Problem("Configured reports are read-only.", statusCode: 403);
        var candidate = new SavedReportCandidate { Id = report.Id, ReportName = report.ReportName, Title = report.Title, Owner = owner, Public = report.IsGlobal, Default = report.IsDefault };
        if (!await Allowed(InteractiveReportAction.ChangeSavedReportOwner, report.ReportName, report.Metadata(), candidate, ct)
            || !await Allowed(InteractiveReportAction.UpdateSavedReport, report.ReportName, report.Metadata(), candidate, ct)) return Forbid();
        if (report.ModifiedUtc != request.ModifiedUtc) return Changed();
        return await saved.Update(report with { Owner = owner }, report, ct) ? NoContent() : Changed();
    }, ct);

    [HttpDelete("reports/{id:long}")]
    public Task<IActionResult> Delete(long id, [FromQuery] DateTime modifiedUtc, CancellationToken ct) => Manage(async () => {
        if (!await access.IsAdministratorAsync(User)) return Forbid();
        if (!await readiness.ReadyAsync(ct)) return StorageUnavailable();
        if (await saved.Get(id, ct) is not { } report) return NotFound();
        if (report.Origin != SavedReportOrigin.User) return Problem("Configured reports are read-only.", statusCode: 403);
        if (!await Allowed(InteractiveReportAction.DeleteSavedReport, report.ReportName, report.Metadata(), null, ct)) return Forbid();
        if (report.ModifiedUtc != modifiedUtc) return Changed();
        return await saved.Delete(report, ct) ? NoContent() : Changed();
    }, ct);

    private async Task<bool> Allowed(InteractiveReportAction action, string name, SavedReportMetadata? report, SavedReportCandidate? candidate, CancellationToken ct)
        => await access.IsAdministratorAsync(User) && await access.AuthorizeAsync(new() {
            User = User, Action = action, RequestServices = HttpContext.RequestServices,
            Resource = new() { ReportName = name, SavedReport = report, Candidate = candidate },
        }, ct);

    private IActionResult Changed() => Problem("This report changed. Refresh the list before trying again.", statusCode: 409);
    private IActionResult StorageUnavailable() => Problem("Saved reports are unavailable. Ask an administrator to check Settings → AppBlocks → Database setup.", statusCode: 503);
    private async Task<IActionResult> Manage(Func<Task<IActionResult>> operation, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        try { return await operation(); }
        catch (SavedReportTitleConflictException) { return Problem("The new owner already has a report with this title.", statusCode: 409); }
        catch (Exception error) when (error is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogError(error, "Report administration storage operation failed.");
            return StorageUnavailable();
        }
    }

    /// <summary>Sources for new public reports, including published report blocks and definition nodes.</summary>
    [HttpGet("sources")]
    public async Task<IActionResult> Sources(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (!await access.IsAdministratorAsync(User)) return Forbid();
        await definitions.LoadAsync();
        var sources = definitions.Reports.Where(pair => !pair.Key.StartsWith("__", StringComparison.Ordinal))
            .Select(pair => new AdministrationSource(pair.Key, pair.Value.Title ?? pair.Key, null)).ToList();
        using var reference = contexts.EnsureUmbracoContext();
        if (navigation.TryGetRootKeys(out var roots))
        foreach (var root in roots)
        {
            var keys = navigation.TryGetDescendantsKeys(root, out var descendants) ? new[] { root }.Concat(descendants) : [root];
            foreach (var key in keys)
            {
                ct.ThrowIfCancellationRequested();
                if (reference.UmbracoContext.Content is not { } cache || await cache.GetByIdAsync(key, false) is not { } page) continue;
                var cultures = page.Cultures.Count > 0 ? page.Cultures.Keys.Cast<string?>() : [null];
                foreach (var culture in cultures)
                foreach (var block in AppBlockContent.Flatten(AppBlockContent.Blocks(page, culture), culture)
                    .Where(block => block.Content.ContentType.Alias == ReportBlocks.Alias))
                {
                    var heading = block.Content.Value<string>("heading", culture: culture);
                    sources.Add(new(ReportBlocks.Name(page.Key, block.Content.Key),
                        $"{page.Name(culture)} / {(string.IsNullOrWhiteSpace(heading) ? "Report" : heading)}", culture));
                }
            }
        }
        return Ok(sources.DistinctBy(source => (source.Name, source.Culture)).OrderBy(source => source.Title));
    }
}

public sealed record AdministrationSource(string Name, string Title, string? Culture);
public sealed record ReassignReport(string? Owner, DateTime ModifiedUtc);
