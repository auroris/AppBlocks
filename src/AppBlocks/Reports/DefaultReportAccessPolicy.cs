using System.Security.Claims;
using AppBlocks;
using InteractiveReport.AspNetCore;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Services;
using UmbracoAppHost.Authentication;
using UmbracoAppHost.Blocks;
using UmbracoAppHost.DataSources;

namespace UmbracoAppHost.Reports;

/// <summary>Uses native backoffice groups; member identities never acquire administrator access by matching email.</summary>
public sealed class DefaultReportAccessPolicy(IUserService users, IOptions<AppBlocksOptions> settings,
    PublishedDataCatalog definitions) : IReportAccessPolicy
{
    public async Task<bool> IsAdministratorAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true || !ApplicationAccessPolicy.IsBackOfficeIdentity(principal)) return false;
        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var user = Guid.TryParse(id, out var key) ? await users.GetAsync(key)
            : int.TryParse(id, out var legacy) ? users.GetUserById(legacy) : null;
        return user is { IsApproved: true, IsLockedOut: false }
            && user.Groups.Any(group => settings.Value.ReportAdministratorGroups.Contains(group.Alias, StringComparer.OrdinalIgnoreCase));
    }

    public async ValueTask<bool> AuthorizeAsync(InteractiveReportAuthorizationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.User.Identity?.IsAuthenticated != true) return false;
        if (AppBlockContent.ParseReportName(request.Resource.ReportName, out _, out _)
            && request.Action == InteractiveReportAction.SelectDefaultReport) return false;
        var admin = await IsAdministratorAsync(request.User);
        var saved = request.Resource.SavedReport;
        var id = request.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var owner = id is not null && saved is not null && saved.Owner == id;
        var name = request.Resource.SourceReportName ?? request.Resource.ReportName;
        var restricted = name.StartsWith("__", StringComparison.Ordinal)
            || definitions.Reports.TryGetValue(name, out var report) && report.Authorization?.Policy == ReportAdministratorRequirement.Policy;
        return request.Action switch
        {
            InteractiveReportAction.ViewReport or InteractiveReportAction.Query or InteractiveReportAction.Export
                or InteractiveReportAction.ListSavedReports or InteractiveReportAction.CreateSavedReport => admin || !restricted,
            InteractiveReportAction.ReadSavedReport => admin || owner || saved is { IsGlobal: true } or { IsDefault: true },
            InteractiveReportAction.UpdateSavedReport or InteractiveReportAction.DeleteSavedReport => admin || owner,
            _ => admin,
        };
    }
}
