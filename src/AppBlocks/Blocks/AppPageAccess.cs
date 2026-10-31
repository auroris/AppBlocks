using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Models.PublishedContent;
using AppBlocks.Authentication;

namespace AppBlocks.Blocks;

public sealed class AppPageAccess(AppBlockContent content, IPublicAccessChecker access, IHttpContextAccessor http,
    ApplicationAccessPolicy applicationAccess)
{
    /// <summary>Retains member-only access for hosts that explicitly require it.</summary>
    public async Task<ClaimsPrincipal?> MemberAsync(Guid pageKey)
    {
        var user = await UserAsync(pageKey);
        return user?.Identity?.AuthenticationType == IdentityConstants.ApplicationScheme ? user : null;
    }

    public async Task<ClaimsPrincipal?> UserAsync(Guid pageKey)
    {
        if (content.Page(pageKey) is not { } page) return null;
        return await UserAsync(page);
    }

    /// <summary>Checks the same member/Public Access boundary for another published application page, such as a paper report.</summary>
    public async Task<ClaimsPrincipal?> UserAsync(IPublishedContent page)
    {
        if (http.HttpContext is not { } context) return null;
        var member = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (member.Succeeded && member.Principal?.Identity?.IsAuthenticated == true)
            return await access.HasMemberAccessToContentAsync(page.Id, member.Principal) == PublicAccessStatus.AccessAccepted ? member.Principal : null;
        return await applicationAccess.BackOfficeAdministratorAsync(context);
    }
}

public sealed record AppPageRequirement(Guid PageKey) : IAuthorizationRequirement;

public sealed class AppPageAuthorizationHandler(IHttpContextAccessor http) : AuthorizationHandler<AppPageRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AppPageRequirement requirement)
    {
        if (http.HttpContext is not { } request) return;
        var member = await request.RequestServices.GetRequiredService<AppPageAccess>().UserAsync(requirement.PageKey);
        if (member is not null && context.User.Identity?.AuthenticationType == member.Identity?.AuthenticationType
            && context.User.FindFirstValue(ClaimTypes.NameIdentifier) == member.FindFirstValue(ClaimTypes.NameIdentifier)) context.Succeed(requirement);
    }
}

/// <summary>Combines the host page boundary with the report source's original policy.</summary>
public sealed class AppPagePolicyProvider(IAuthorizationPolicyProvider fallback) : IAuthorizationPolicyProvider
{
    private const string Prefix = "AppHostPage:";
    public static string Name(Guid page, string? sourcePolicy) => Prefix + page.ToString("N") + ":" + Uri.EscapeDataString(sourcePolicy ?? "");
    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => fallback.GetDefaultPolicyAsync();
    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => fallback.GetFallbackPolicyAsync();
    public async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!policyName.StartsWith(Prefix, StringComparison.Ordinal)) return await fallback.GetPolicyAsync(policyName);
        var parts = policyName[Prefix.Length..].Split(':', 2);
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out var page)) return null;
        var builder = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(new AppPageRequirement(page));
        if (parts[1].Length > 0) {
            var source = await fallback.GetPolicyAsync(Uri.UnescapeDataString(parts[1]));
            if (source is null) return null;
            builder.Combine(source);
        }
        return builder.Build();
    }
}
