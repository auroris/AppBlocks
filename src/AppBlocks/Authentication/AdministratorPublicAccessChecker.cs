using System.Security.Claims;
using Umbraco.Cms.Core.Security;

namespace UmbracoAppHost.Authentication;

/// <summary>
/// Where Umbraco's Public Access learns the application's rule, which stays in
/// <see cref="ApplicationAccessPolicy"/>: a protected page that finds no member session admits a
/// current backoffice Administrator, as the form, report and paper-report endpoints do. Any other
/// answer comes from a member session, whose page permissions decide.
/// </summary>
public sealed class AdministratorPublicAccessChecker(IPublicAccessChecker inner, IHttpContextAccessor http)
    : IPublicAccessChecker
{
    /// <summary>Umbraco's routing check, made after it authenticated only the member cookie.</summary>
    public async Task<PublicAccessStatus> HasMemberAccessToContentAsync(int publishedContentId)
    {
        var status = await inner.HasMemberAccessToContentAsync(publishedContentId);
        if (status != PublicAccessStatus.NotLoggedIn || http.HttpContext is not { } context)
            return status;
        return await Policy(context).BackOfficeAdministratorAsync(context) is not null
            ? PublicAccessStatus.AccessAccepted : status;
    }

    /// <summary>Judges only the given principal, so a member principal keeps Umbraco's answer.</summary>
    public async Task<PublicAccessStatus> HasMemberAccessToContentAsync(int publishedContentId, ClaimsPrincipal claimsPrincipal)
    {
        var status = await inner.HasMemberAccessToContentAsync(publishedContentId, claimsPrincipal);
        if (status != PublicAccessStatus.NotLoggedIn || http.HttpContext is not { } context
            || !ApplicationAccessPolicy.IsBackOfficeIdentity(claimsPrincipal))
            return status;
        return await Policy(context).IsBackOfficeAdministratorAsync(claimsPrincipal)
            ? PublicAccessStatus.AccessAccepted : status;
    }

    // A singleton like Umbraco's checker, so the scoped policy comes from the request.
    private static ApplicationAccessPolicy Policy(HttpContext context) =>
        context.RequestServices.GetRequiredService<ApplicationAccessPolicy>();
}
