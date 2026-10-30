using System.Security.Claims;
using InteractiveReport.AspNetCore;

namespace UmbracoAppHost.Reports;

/// <summary>A host may replace the package's native Umbraco report authorization.</summary>
public interface IReportAccessPolicy
{
    Task<bool> IsAdministratorAsync(ClaimsPrincipal user);
    ValueTask<bool> AuthorizeAsync(InteractiveReportAuthorizationRequest request, CancellationToken cancellationToken);
}
