using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Services;

namespace AppBlocks.Authentication;

/// <summary>
/// Backoffice authentication helpers. The application decides how to use the resulting identity: nothing here admits anyone
/// to a page or grants an application permission, and a page Umbraco's Public Access protects stays protected unless the
/// host registers an <see cref="IProtectedPageAdmission"/>.
/// </summary>
public sealed class ApplicationAccessPolicy(IUserService users)
{
    /// <summary>Whether the primary identity is a backoffice User. Combined principals keep their Member first for ownership keys.</summary>
    public static bool IsBackOfficeIdentity(ClaimsPrincipal user) => user.Identity?.AuthenticationType
        is Constants.Security.BackOfficeAuthenticationType or Constants.Security.BackOfficeExposedAuthenticationType;

    public static ClaimsIdentity? MemberIdentity(ClaimsPrincipal principal) => principal.Identities.FirstOrDefault(identity =>
        identity.IsAuthenticated && identity.AuthenticationType == IdentityConstants.ApplicationScheme);

    public static ClaimsIdentity? BackOfficeIdentity(ClaimsPrincipal principal) => principal.Identities.FirstOrDefault(identity =>
        identity.IsAuthenticated && identity.AuthenticationType is Constants.Security.BackOfficeAuthenticationType or Constants.Security.BackOfficeExposedAuthenticationType);

    /// <summary>Whether the backoffice User is in Umbraco's Administrators group, for the backoffice's own management screens; no application permission follows from it.</summary>
    public async Task<bool> IsBackOfficeAdministratorAsync(ClaimsPrincipal principal)
    {
        var user = await ActiveUserAsync(principal);
        return user is not null
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
            if (result.Succeeded && result.Principal is { Identity.IsAuthenticated: true } principal && IsBackOfficeIdentity(principal))
                return await ActiveUserAsync(principal) is not null ? principal : null;
        }
        return null;
    }

    /// <summary>Whether the request carries a valid backoffice ticket at all, whatever the account's current standing.</summary>
    public async Task<bool> HasBackOfficeTicketAsync(HttpContext context)
    {
        foreach (var scheme in new[] { Constants.Security.BackOfficeExposedAuthenticationType, Constants.Security.BackOfficeAuthenticationType })
        {
            var result = await context.AuthenticateAsync(scheme);
            if (result.Succeeded && result.Principal is { Identity.IsAuthenticated: true } principal && IsBackOfficeIdentity(principal)) return true;
        }
        return false;
    }

    // Member and backoffice IDs belong to separate account stores. Keep their browser state and
    // submission receipts separate, preserving existing member keys. An anonymous visitor has no
    // account and so no key: this is empty for them, which must never be used as anyone's identity.
    public static string UserKey(ClaimsPrincipal user) =>
        (IsBackOfficeIdentity(user) ? "backoffice:" : "") + user.FindFirstValue(ClaimTypes.NameIdentifier);

    /// <summary>
    /// The approved, not locked-out User a backoffice principal names, read from the store so its
    /// current standing and groups count rather than the ticket's claims.
    /// </summary>
    private async Task<IUser?> ActiveUserAsync(ClaimsPrincipal principal)
    {
        if (BackOfficeIdentity(principal) is not { } identity) return null;
        var id = identity.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var user = Guid.TryParse(id, out var key) ? await users.GetAsync(key)
            : int.TryParse(id, out var legacyId) ? users.GetUserById(legacyId) : null;
        return user is { IsApproved: true, IsLockedOut: false } ? user : null;
    }
}
