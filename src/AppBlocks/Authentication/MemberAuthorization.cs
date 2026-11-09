using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Options;
using AppBlocks.DataSources;

namespace AppBlocks.Authentication;

/// <summary>
/// Authorization for a companion's own controllers and endpoints that serve signed-in Umbraco Members. Mark an action or controller
/// <c>[Authorize(Policy = AppBlocksAuthorization.MemberPolicy)]</c>, or call <c>RequireAuthorization(AppBlocksAuthorization.MemberPolicy)</c>
/// on a mapped endpoint. The policy reads Umbraco's Member cookie and admits a Member whose account is approved and not locked out, read
/// afresh on every request. On an API-style request it answers 401 to a caller with no Member session and 403 to a Member it refuses,
/// where Umbraco's Member cookie would redirect to the login page; other requests keep that redirect.
/// <see cref="AdministratorPolicy"/> does the same for a backoffice management controller that only administrators may use.
/// </summary>
public static class AppBlocksAuthorization
{
    /// <summary>The policy name: a signed-in, approved, unlocked Umbraco Member.</summary>
    public const string MemberPolicy = "AppBlocks.Member";

    /// <summary>
    /// The policy name for a backoffice management controller or action that only administrators may use: an approved, unlocked
    /// backoffice User in Umbraco's Administrators group or a group <c>AppBlocks:AdministratorGroups</c> names, read afresh through
    /// <see cref="ApplicationAccessPolicy.ActiveUserAsync"/> on every request, so a User removed from the group or locked out is refused at once. It adds to the backoffice access that every
    /// management controller already requires, and answers 403 to anyone else.
    /// </summary>
    public const string AdministratorPolicy = "AppBlocks.Administrator";

    /// <summary>The authentication scheme the policy uses: Umbraco's Member cookie, with 401 and 403 for API-style requests.</summary>
    public const string MemberScheme = "AppBlocks.Member";

    /// <summary>
    /// Where a signed-in Member's script fetches an antiforgery token for a companion's own endpoints: <c>antiforgery/token</c> below
    /// <see cref="AppBlocksRoutes.Root"/>, so <c>/api/antiforgery/token</c> unless the site moves the root.
    /// </summary>
    public static string AntiforgeryTokenPath(IConfiguration? configuration) => AppBlocksRoutes.Under(configuration, "antiforgery/token");

    /// <summary>
    /// Whether a request expects an API answer rather than a page: its path is under the blocks' API root (<see cref="AppBlocksRoutes.Root"/>,
    /// <c>/api</c> by default), its endpoint is an <c>[ApiController]</c>, it accepts JSON, or it says it was made by script with
    /// <c>X-Requested-With: XMLHttpRequest</c>.
    /// </summary>
    public static bool IsApiRequest(HttpContext context)
    {
        var root = AppBlocksRoutes.Root(context.RequestServices?.GetService<IConfiguration>());
        if (context.Request.Path.StartsWithSegments(root, StringComparison.OrdinalIgnoreCase)) return true;
        if (context.GetEndpoint()?.Metadata.GetMetadata<IApiBehaviorMetadata>() is not null) return true;
        if (context.Request.Headers.XRequestedWith == "XMLHttpRequest") return true;
        return context.Request.Headers.Accept.Any(value => value?.Contains("json", StringComparison.OrdinalIgnoreCase) == true);
    }

    internal static void Add(IServiceCollection services)
    {
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, MemberAuthenticationHandler>(MemberScheme, null);
        services.AddAuthorization(options => {
            options.AddPolicy(MemberPolicy, policy => policy
                .AddAuthenticationSchemes(MemberScheme).RequireAuthenticatedUser().AddRequirements(new SignedInMemberRequirement()));
            // No scheme of its own: the management API's backoffice scheme, which its controllers require, authenticates the User.
            options.AddPolicy(AdministratorPolicy, policy => policy.RequireAuthenticatedUser().AddRequirements(new BackOfficeAdministratorRequirement()));
        });
        services.AddSingleton<IAuthorizationHandler, SignedInMemberHandler>();
        services.AddSingleton<IAuthorizationHandler, BackOfficeAdministratorHandler>();
        services.AddOptions<AdministratorOptions>().BindConfiguration(AdministratorOptions.Section);
    }

    /// <summary>
    /// Maps <see cref="AntiforgeryTokenPath"/>: for a signed-in Member, <c>{ token, headerName }</c> with <c>Cache-Control: no-store</c>,
    /// the token bound to that Member as the blocks' own tokens are. A script sends it in the named header with each change it posts.
    /// </summary>
    internal static void MapAntiforgeryToken(IEndpointRouteBuilder endpoints)
        => endpoints.MapGet(AntiforgeryTokenPath(endpoints.ServiceProvider.GetService<IConfiguration>()), (HttpContext http, IAntiforgery antiforgery) => {
            http.Response.Headers.CacheControl = "no-store";
            var tokens = antiforgery.GetAndStoreTokens(http);
            return Results.Json(new { token = tokens.RequestToken, headerName = tokens.HeaderName });
        }).RequireAuthorization(MemberPolicy);
}

/// <summary>A signed-in Umbraco Member whose account is approved and not locked out.</summary>
public sealed class SignedInMemberRequirement : IAuthorizationRequirement;

/// <summary>Reads the Member afresh through <see cref="IDataSourceUserResolver"/>, as the blocks do, so a locked-out or unapproved account is refused at once.</summary>
internal sealed class SignedInMemberHandler(IHttpContextAccessor accessor) : AuthorizationHandler<SignedInMemberRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, SignedInMemberRequirement requirement)
    {
        if (ApplicationAccessPolicy.MemberIdentity(context.User) is not { } identity) return;
        var http = context.Resource as HttpContext ?? accessor.HttpContext;
        if (http?.RequestServices.GetService<IDataSourceUserResolver>() is not { } users) return;
        if (await users.ResolveAsync(new ClaimsPrincipal(identity), http.RequestAborted) is { IsMember: true }) context.Succeed(requirement);
    }
}

/// <summary>An approved, unlocked backoffice User in Umbraco's Administrators or a configured AppBlocks administrator group.</summary>
public sealed class BackOfficeAdministratorRequirement : IAuthorizationRequirement;

/// <summary>Reads the User afresh through <see cref="ApplicationAccessPolicy.IsBackOfficeAdministratorAsync"/>, so the group and the account's standing count now, not when the token was issued.</summary>
internal sealed class BackOfficeAdministratorHandler(IHttpContextAccessor accessor) : AuthorizationHandler<BackOfficeAdministratorRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, BackOfficeAdministratorRequirement requirement)
    {
        var http = context.Resource as HttpContext ?? accessor.HttpContext;
        if (http?.RequestServices.GetService<ApplicationAccessPolicy>() is not { } access) return;
        if (await access.IsBackOfficeAdministratorAsync(context.User)) context.Succeed(requirement);
    }
}

/// <summary>
/// Umbraco's Member cookie under another name: it authenticates exactly as that cookie does, and answers a challenge with 401 and a
/// forbid with 403 on an API-style request (<see cref="AppBlocksAuthorization.IsApiRequest"/>), passing any other request to the cookie.
/// </summary>
internal sealed class MemberAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var member = await Context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (member.Succeeded && member.Principal.Identity?.IsAuthenticated == true)
            return AuthenticateResult.Success(new AuthenticationTicket(member.Principal, member.Properties, Scheme.Name));
        return member.Failure is { } failure ? AuthenticateResult.Fail(failure) : AuthenticateResult.NoResult();
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        => AppBlocksAuthorization.IsApiRequest(Context) ? Answer(StatusCodes.Status401Unauthorized) : Context.ChallengeAsync(IdentityConstants.ApplicationScheme, properties);

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
        => AppBlocksAuthorization.IsApiRequest(Context) ? Answer(StatusCodes.Status403Forbidden) : Context.ForbidAsync(IdentityConstants.ApplicationScheme, properties);

    private Task Answer(int status)
    {
        Response.StatusCode = status;
        Response.Headers.CacheControl = "no-store";
        return Task.CompletedTask;
    }
}
