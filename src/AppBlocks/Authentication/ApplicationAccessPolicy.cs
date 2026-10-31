using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Services;

namespace AppBlocks.Authentication;

/// <summary>Backoffice authentication helpers. The application decides how to use the resulting identity.</summary>
public sealed class ApplicationAccessPolicy(IUserService users)
{
    public static bool IsBackOfficeIdentity(ClaimsPrincipal user) => user.Identity?.AuthenticationType
        is Constants.Security.BackOfficeAuthenticationType or Constants.Security.BackOfficeExposedAuthenticationType;

    public async Task<bool> IsBackOfficeAdministratorAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true || !IsBackOfficeIdentity(principal)) return false;
        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var user = Guid.TryParse(id, out var key) ? await users.GetAsync(key)
            : int.TryParse(id, out var legacyId) ? users.GetUserById(legacyId) : null;
        return user is { IsApproved: true, IsLockedOut: false }
            && user.Groups.Any(group => group.Alias.Equals(Constants.Security.AdminGroupAlias, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ClaimsPrincipal?> BackOfficeAdministratorAsync(HttpContext context)
    {
        var principal = await BackOfficeUserAsync(context);
        return principal is not null && await IsBackOfficeAdministratorAsync(principal) ? principal : null;
    }

    /// <summary>Authenticates a current backoffice User, without requiring a particular User Group.</summary>
    public async Task<ClaimsPrincipal?> BackOfficeUserAsync(HttpContext context)
    {
        foreach (var scheme in new[] { Constants.Security.BackOfficeExposedAuthenticationType, Constants.Security.BackOfficeAuthenticationType })
        {
            var result = await context.AuthenticateAsync(scheme);
            if (result.Succeeded && result.Principal is { Identity.IsAuthenticated: true } principal && IsBackOfficeIdentity(principal)) {
                var id = principal.FindFirstValue(ClaimTypes.NameIdentifier);
                var user = Guid.TryParse(id, out var key) ? await users.GetAsync(key)
                    : int.TryParse(id, out var legacy) ? users.GetUserById(legacy) : null;
                return user is { IsApproved: true, IsLockedOut: false } ? principal : null;
            }
        }
        return null;
    }

    // Member and backoffice IDs belong to separate account stores. Keep their browser state and
    // submission receipts separate, preserving existing member keys.
    public static string UserKey(ClaimsPrincipal user) =>
        (IsBackOfficeIdentity(user) ? "backoffice:" : "") + user.FindFirstValue(ClaimTypes.NameIdentifier);
}
