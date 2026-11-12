using AppBlocks.InteractiveReports.Blocks;
using System.Security.Claims;
using InteractiveReport.AspNetCore;
using InteractiveReport.Core.Identity;
using Microsoft.Extensions.Options;
using AppBlocks.Authentication;
using AppBlocks.Blocks;
using AppBlocks.InteractiveReports.DataSources;
using AppBlocks.InteractiveReports.Setup;
using AppBlocks.DataSources;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Services;

namespace AppBlocks.InteractiveReports.Reports;

/// <summary>
/// What an anonymous visitor may do with a report, for a policy to apply first: a report block on a page that let them in may
/// be viewed, queried and exported, and its saved views list as nothing. Everything else is refused, including every
/// report that is not a block, whatever its name or policy. The definition store has already refused the block for a page with Public Access.
/// </summary>
public static class AnonymousReportAccess
{
    public static bool Allows(InteractiveReportAuthorizationRequest request)
        => ReportBlocks.ParseName(request.Resource.SourceReportName ?? request.Resource.ReportName, out _, out _)
            && request.Action is InteractiveReportAction.ViewReport or InteractiveReportAction.Query or InteractiveReportAction.Export or InteractiveReportAction.ListSavedReports;
}

/// <summary>Uses native backoffice groups; member identities never acquire administrator access by matching email.</summary>
public sealed class DefaultReportAccessPolicy(ApplicationAccessPolicy access, IOptions<AppBlocksOptions> settings,
    PublishedReportCatalog definitions, IOptionsMonitor<InteractiveReportOptions> reportOptions,
    IDataSourceUserResolver? users = null, IMemberGroupService? memberGroups = null) : IReportAccessPolicy
{
    /// <summary>
    /// The names of the Member groups a member group picker's value holds: group ids or keys, separated by commas. A group deleted
    /// since it was picked names nobody.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> MemberGroupNamesAsync(IMemberGroupService groups, string picked)
    {
        var all = (await groups.GetAllAsync()).ToArray();
        return picked.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => int.TryParse(item, out var id) ? all.FirstOrDefault(g => g.Id == id) : Guid.TryParse(item, out var key) ? all.FirstOrDefault(g => g.Key == key) : null)
            .Select(group => group?.Name).OfType<string>().ToArray();
    }

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
        if (ReportBlocks.ParseName(request.Resource.ReportName, out _, out _)
            && request.Action == InteractiveReportAction.SelectDefaultReport) return false;
        var admin = await IsAdministratorAsync(request.User);
        var saved = request.Resource.SavedReport;
        var owner = saved is not null && OwnerKey(request.User, reportOptions.CurrentValue.IdentityClaim) is { } id && saved.Owner == id;
        var name = request.Resource.SourceReportName ?? request.Resource.ReportName;
        if (request.Action is InteractiveReportAction.ViewReport or InteractiveReportAction.Query or InteractiveReportAction.Export
            or InteractiveReportAction.ListSavedReports or InteractiveReportAction.CreateSavedReport)
        {
            if (admin) return true;
            if (name.StartsWith("__", StringComparison.Ordinal)) return false;
            await definitions.LoadAsync();
            if (definitions.Reports.TryGetValue(name, out var report) && report.Authorization?.Policy == ReportAdministratorRequirement.Policy) return false;
            // A report definition node set to Specific groups admits the Members of the groups it picks; the rest admit anyone signed in.
            return definitions.ReportNodes.GetValueOrDefault(name)?.MemberGroups is not { } picked
                || memberGroups is not null && users is not null && await DataSourceQueries.ResolveReaderAsync(users, request.User, cancellationToken) is { IsAnonymous: false } reader
                    && (await MemberGroupNamesAsync(memberGroups, picked)).Any(reader.IsInMemberGroup);
        }
        return request.Action switch
        {
            InteractiveReportAction.ReadSavedReport => admin || owner || saved is { IsGlobal: true } or { IsDefault: true },
            InteractiveReportAction.UpdateSavedReport or InteractiveReportAction.DeleteSavedReport => admin || owner,
            _ => admin,
        };
    }

    /// <summary>
    /// The one identity a saved view belongs to, the same the engine resolves: the Member when a Member is signed in, otherwise
    /// the backoffice User, whichever order the principal lists them in; another signed-in identity a host adds counts last. The
    /// key is read through <paramref name="identityClaim"/> (<c>InteractiveReport:IdentityClaim</c>), as the engine stores owners.
    /// </summary>
    public static string? OwnerKey(ClaimsPrincipal principal, string? identityClaim = null)
        => ReportIdentity.Resolve(ApplicationAccessPolicy.MemberIdentity(principal) ?? ApplicationAccessPolicy.BackOfficeIdentity(principal)
            ?? principal.Identities.FirstOrDefault(identity => identity.IsAuthenticated), identityClaim);
}
