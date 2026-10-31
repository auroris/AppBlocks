using System.Security.Claims;
using InteractiveReport.AspNetCore;
using Microsoft.AspNetCore.Authorization;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Actions;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Services;

namespace AppBlocks.InteractiveReports.Reports;

/// <summary>Limits the Welcome report to the content available to the current backoffice user.</summary>
public sealed class RecentPagesReport(IUserService users, IEntityService entities, AppCaches caches)
{
    public const string Name = "umbraco-recent-pages";
    public const string Policy = "UmbracoBackOfficeContentReports";

    // Aggregate the ancestors in tree order without imposing a maximum content depth.
    internal static string PrepareSql(string sql, IConfiguration configuration)
    {
        var sqlite = configuration["ConnectionStrings:umbracoDbDSN_ProviderName"]
            ?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true;
        var aggregate = sqlite
            ? "group_concat(a.text, ' / ' ORDER BY a.level)"
            : "STRING_AGG(CAST(a.text AS nvarchar(max)), N' / ') WITHIN GROUP (ORDER BY a.level)";
        var path = $"(SELECT {aggregate} FROM umbracoNode a WHERE a.id > 0 AND CONCAT(',', n.path, ',') LIKE CONCAT('%,', a.id, ',%'))";
        // SQLite exposes Umbraco's saved dates as text. Keep the original precision for sorting,
        // but display seconds; SQL Server exposes a date that the report's format mask handles.
        var editedAt = sqlite ? "strftime('%Y-%m-%d %H:%M:%S', v.versionDate)" : "v.versionDate";
        return sql.Replace("{ContentPath}", path, StringComparison.Ordinal)
            .Replace("{EditedAt}", editedAt, StringComparison.Ordinal);
    }

    public async Task<IUser?> GetUserAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true
            || principal.Identity.AuthenticationType is not (Constants.Security.BackOfficeAuthenticationType
                or Constants.Security.BackOfficeExposedAuthenticationType)) return null;

        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var user = Guid.TryParse(id, out var key) ? await users.GetAsync(key)
            : int.TryParse(id, out var legacyId) ? users.GetUserById(legacyId) : null;
        return user is { IsApproved: true, IsLockedOut: false }
            && user.AllowedSections.Contains(Constants.Applications.Content) ? user : null;
    }

    public async ValueTask<RowRestriction> RestrictAsync(InteractiveReportRowRestrictionRequest request, CancellationToken ct)
    {
        if ((request.SourceReportName ?? request.ReportName) != Name) return RowRestriction.NotApplicable;
        ct.ThrowIfCancellationRequested();
        if (await GetUserAsync(request.User) is not { } user) return RowRestriction.Deny;

        var values = new List<object?>();
        var clauses = new List<string>();
        var startNodes = user.CalculateContentStartNodeIds(entities, caches);
        if (startNodes is not { Length: > 0 }) return RowRestriction.Where("1 = 0");
        if (!startNodes.Contains(Constants.System.Root))
        {
            clauses.Add("(" + string.Join(" OR ", startNodes.Select(id => {
                values.Add($"%,{id},%");
                return "CONCAT(',', n.path, ',') LIKE ?";
            })) + ")");
        }

        // Permissions only change at explicitly assigned nodes. Ask Umbraco for the effective
        // permission at each boundary, then apply the deepest matching boundary to each row.
        // This keeps paging, counts, search and exports inside the same access boundary.
        var boundaryIds = users.GetPermissions(user).Where(p => !p.IsDefaultPermissions && p.EntityId > 0)
            .Select(p => p.EntityId).Distinct().ToArray();
        var defaultAccess = CanBrowse(Constants.System.RootString) ? 1 : 0;
        if (boundaryIds.Length == 0) clauses.Add(defaultAccess == 1 ? "1 = 1" : "1 = 0");
        else
        {
            var branches = new List<string>();
            foreach (var node in entities.GetAll(UmbracoObjectTypes.Document, boundaryIds).OrderByDescending(n => n.Level))
            {
                ct.ThrowIfCancellationRequested();
                values.Add(node.Path);
                values.Add(node.Path + ",%");
                branches.Add($"WHEN n.path = ? OR n.path LIKE ? THEN {(CanBrowse(node.Path) ? 1 : 0)}");
            }
            clauses.Add(branches.Count == 0 ? $"{defaultAccess} = 1"
                : $"CASE {string.Join(" ", branches)} ELSE {defaultAccess} END = 1");
        }
        return RowRestriction.Where(string.Join(" AND ", clauses), values.ToArray());

        bool CanBrowse(string path) => users.GetPermissionsForPath(user, path).GetAllPermissions().Contains(ActionBrowse.ActionLetter);
    }
}

public sealed class BackOfficeContentReportRequirement : IAuthorizationRequirement;

public sealed class BackOfficeContentReportHandler(IHttpContextAccessor http)
    : AuthorizationHandler<BackOfficeContentReportRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, BackOfficeContentReportRequirement requirement)
    {
        if (http.HttpContext is { } request
            && await request.RequestServices.GetRequiredService<RecentPagesReport>().GetUserAsync(context.User) is not null)
            context.Succeed(requirement);
    }
}
