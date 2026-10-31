using System.Security.Claims;
using InteractiveReport.AspNetCore;

namespace AppBlocks.InteractiveReports.Reports;

/// <summary>
/// A host may replace the package's native Umbraco report authorization. It is asked about anonymous visitors too: a report
/// block on a page with no Public Access reaches it with an unauthenticated user. Return false to refuse them, or
/// <see cref="AnonymousReportAccess.Allows"/> for the package's own rule.
/// </summary>
public interface IReportAccessPolicy
{
    Task<bool> IsAdministratorAsync(ClaimsPrincipal user);
    ValueTask<bool> AuthorizeAsync(InteractiveReportAuthorizationRequest request, CancellationToken cancellationToken);
}
