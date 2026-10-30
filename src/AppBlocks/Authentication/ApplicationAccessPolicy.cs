using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Services;

namespace UmbracoAppHost.Authentication;

/// <summary>Allows current backoffice Administrators to use the application without a member account.</summary>
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
        foreach (var scheme in new[] { Constants.Security.BackOfficeExposedAuthenticationType, Constants.Security.BackOfficeAuthenticationType })
        {
            var result = await context.AuthenticateAsync(scheme);
            if (result.Succeeded && result.Principal is { } principal && await IsBackOfficeAdministratorAsync(principal))
                return principal;
        }
        return null;
    }

    // Member and backoffice IDs belong to separate account stores. Keep their browser state and
    // submission receipts separate, preserving existing member keys.
    public static string UserKey(ClaimsPrincipal user) =>
        (IsBackOfficeIdentity(user) ? "backoffice:" : "") + user.FindFirstValue(ClaimTypes.NameIdentifier);
}
