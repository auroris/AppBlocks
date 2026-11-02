using System.Security.Claims;
using InteractiveReport.AspNetCore;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Services;
using AppBlocks.Authentication;
using AppBlocks.Blocks;
using AppBlocks.InteractiveReports.DataSources;
using AppBlocks.InteractiveReports.Setup;
using Umbraco.Cms.Core;

namespace AppBlocks.InteractiveReports.Reports;

/// <summary>
/// What an anonymous visitor may do with a report, for a policy to apply first: a report block on a page that let them in may
/// be viewed, queried and exported, and its saved views list as nothing. Everything else is refused, including every
/// report that is not a block, whatever its name or policy. The definition store has already refused the block for a page with Public Access.
/// </summary>
public static class AnonymousReportAccess
{
    public static bool Allows(InteractiveReportAuthorizationRequest request)
        => AppBlockContent.ParseReportName(request.Resource.SourceReportName ?? request.Resource.ReportName, out _, out _)
            && request.Action is InteractiveReportAction.ViewReport or InteractiveReportAction.Query or InteractiveReportAction.Export or InteractiveReportAction.ListSavedReports;
}

/// <summary>Uses native backoffice groups; member identities never acquire administrator access by matching email.</summary>
public sealed class DefaultReportAccessPolicy(IUserService users, IOptions<AppBlocksOptions> settings,
    PublishedReportCatalog definitions) : IReportAccessPolicy
{
    public async Task<bool> IsAdministratorAsync(ClaimsPrincipal principal)
    {
        if (ApplicationAccessPolicy.BackOfficeIdentity(principal) is not { } identity) return false;
        var id = identity.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var user = Guid.TryParse(id, out var key) ? await users.GetAsync(key)
            : int.TryParse(id, out var legacy) ? users.GetUserById(legacy) : null;
        return user is { IsApproved: true, IsLockedOut: false }
            && user.Groups.Any(group => group.Alias.Equals(Constants.Security.AdminGroupAlias, StringComparison.OrdinalIgnoreCase)
                || group.Alias.Equals(ReportAdministratorGroup.Alias, StringComparison.OrdinalIgnoreCase)
                || settings.Value.ReportAdministratorGroups.Contains(group.Alias, StringComparer.OrdinalIgnoreCase));
    }

    public async ValueTask<bool> AuthorizeAsync(InteractiveReportAuthorizationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.User.Identity?.IsAuthenticated != true) return AnonymousReportAccess.Allows(request);
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
