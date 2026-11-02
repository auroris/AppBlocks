using System.Security.Claims;
using InteractiveReport.AspNetCore;
using Microsoft.Extensions.Options;
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
public sealed class DefaultReportAccessPolicy(ApplicationAccessPolicy access, IOptions<AppBlocksOptions> settings,
    PublishedReportCatalog definitions) : IReportAccessPolicy
{
    public async Task<bool> IsAdministratorAsync(ClaimsPrincipal principal)
    {
        var user = await access.ActiveUserAsync(principal);
        return user is not null
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
        var owner = saved is not null && OwnerKey(request.User) is { } id && saved.Owner == id;
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

    /// <summary>
    /// The one identity a saved view belongs to, the same the engine resolves: the Member when a Member is signed in, otherwise
    /// the backoffice User, whichever order the principal lists them in; another signed-in identity a host adds counts last.
    /// </summary>
    public static string? OwnerKey(ClaimsPrincipal principal)
        => (ApplicationAccessPolicy.MemberIdentity(principal) ?? ApplicationAccessPolicy.BackOfficeIdentity(principal)
            ?? principal.Identities.FirstOrDefault(identity => identity.IsAuthenticated))?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
}
