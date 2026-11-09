using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services;
using AppBlocks.Authentication;

namespace AppBlocks.Blocks;

public sealed class AppPageAccess(AppBlockContent content, IPublicAccessChecker access, IHttpContextAccessor http,
    ApplicationAccessPolicy applicationAccess, IPublicAccessService publicAccess, IEnumerable<IProtectedPageAdmission> admissions)
{
    /// <summary>The signed-in Member and/or backoffice User this page admits, Member first when both are present. Null for everyone else, including an anonymous visitor to an open page.</summary>
    public async Task<ClaimsPrincipal?> UserAsync(Guid pageKey)
    {
        if (await content.PageAsync(pageKey) is not { } page) return null;
        return await UserAsync(page);
    }

    /// <summary>Checks the same member/Public Access boundary for another published application page, such as a paper report. Null for anyone not signed in.</summary>
    public async Task<ClaimsPrincipal?> UserAsync(IPublishedContent page)
    {
        if (http.HttpContext is not { } context) return null;
        var member = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (member.Succeeded && member.Principal?.Identity?.IsAuthenticated == true)
        {
            if (await access.HasMemberAccessToContentAsync(page.Id, member.Principal) != PublicAccessStatus.AccessAccepted) return null;
            var backOfficeUser = await applicationAccess.BackOfficeUserAsync(context);
            // Keep the Member first for existing ownership, audit and antiforgery identities. The resolver reads
            // each authenticated identity's own identifier, so the accounts and their groups remain separate.
            return backOfficeUser is null ? member.Principal
                : new ClaimsPrincipal(member.Principal.Identities.Concat(backOfficeUser.Identities));
        }
        // Umbraco Public Access protects pages for Members. Without a Member session, Umbraco refuses the page, and only a rule
        // the host registers admits anyone else; a backoffice User is never looked up in the Member store using a coincidentally matching identifier.
        if (publicAccess.IsProtected(page.Path).Success) return await AdmittedAsync(context, page);
        return await applicationAccess.BackOfficeUserAsync(context);
    }

    /// <summary>
    /// Who the host's <see cref="IProtectedPageAdmission"/> rules admit to a page Umbraco's Public Access refuses: the first
    /// authenticated answer, or null when there is none or when a Member session is present, since Umbraco itself decides for a Member.
    /// </summary>
    public async Task<ClaimsPrincipal?> AdmittedAsync(HttpContext context, IPublishedContent page)
    {
        var member = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (member.Succeeded && member.Principal?.Identity?.IsAuthenticated == true) return null;
        foreach (var admission in admissions)
            if (await admission.AdmitAsync(context, page, context.RequestAborted) is { Identity.IsAuthenticated: true } admitted) return admitted;
        return null;
    }

    /// <summary>
    /// Who this request is to the page, for a block that lets its own class or engine decide about anonymous visitors.
    /// Null means refused. Otherwise it is the signed-in Member or backoffice User <see cref="UserAsync(IPublishedContent)"/>
    /// admits, or, on a page with no Public Access of its own or inherited, an unauthenticated principal for a visitor
    /// with no account. An account that presented itself and failed, such as a locked-out Member or a disabled User, stays refused.
    /// </summary>
    public async Task<ClaimsPrincipal?> VisitorAsync(IPublishedContent page)
    {
        if (await UserAsync(page) is { } user) return user;
        if (http.HttpContext is not { } context || !IsOpen(page)) return null;
        if ((await context.AuthenticateAsync(IdentityConstants.ApplicationScheme)).Succeeded) return null;
        return await applicationAccess.HasBackOfficeTicketAsync(context) ? null : Anonymous;
    }

    /// <inheritdoc cref="VisitorAsync(IPublishedContent)"/>
    public async Task<ClaimsPrincipal?> VisitorAsync(Guid pageKey)
        => await content.PageAsync(pageKey) is { } page ? await VisitorAsync(page) : null;

    /// <summary>Whether Umbraco's Public Access leaves this page open, on the page itself and on every ancestor.</summary>
    public bool IsOpen(IPublishedContent page) => !publicAccess.IsProtected(page.Path).Success;

    /// <summary>The principal an anonymous visitor is represented by: never null, never authenticated, with no claims.</summary>
    public static ClaimsPrincipal Anonymous => new(new ClaimsIdentity());
}

public sealed record AppPageRequirement(Guid PageKey) : IAuthorizationRequirement;

/// <summary>
/// Admits the request's user when one of its authenticated identities is an account the page admits. Identities are matched one by
/// one, by authentication type and identifier, because the caller may carry fewer of them than the page's principal: a backoffice-only
/// route keeps only the User while the page also finds the Member cookie on the request.
/// </summary>
public sealed class AppPageAuthorizationHandler(IHttpContextAccessor http) : AuthorizationHandler<AppPageRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AppPageRequirement requirement)
    {
        if (http.HttpContext is not { } request) return;
        if (await request.RequestServices.GetRequiredService<AppPageAccess>().UserAsync(requirement.PageKey) is not { } admitted) return;
        if (context.User.Identities.Any(identity => identity.IsAuthenticated && admitted.Identities.Any(page => page.IsAuthenticated
            && page.AuthenticationType == identity.AuthenticationType
            && identity.FindFirst(ClaimTypes.NameIdentifier)?.Value is { Length: > 0 } id && page.FindFirst(ClaimTypes.NameIdentifier)?.Value == id)))
            context.Succeed(requirement);
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
