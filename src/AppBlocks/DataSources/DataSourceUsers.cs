using System.Security.Claims;
using Umbraco.Cms.Core.Actions;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services.AuthorizationStatus;
using AppBlocks.Authentication;
using AppBlocks.Forms;

namespace AppBlocks.DataSources;

/// <summary>The website Member account, with its own identity and Member Group names.</summary>
public sealed record DataSourceMemberAccount(string Key, string? Sid, string? Name, string? Email, IReadOnlyList<string> GroupNames);

/// <summary>The backoffice User account, with its own identity, User Groups and permissions on the calling page.</summary>
public sealed record DataSourceBackOfficeAccount(string Key, string? Sid, string? Name, string? Email,
    IReadOnlyList<string> GroupNames, IReadOnlyList<string> GroupAliases, bool CanEditPage)
{
    public bool CanPublishPage { get; init; }
    public bool CanDeletePage { get; init; }
}

/// <summary>
/// The current Umbraco Member, backoffice User, or both. The application decides how to use these account facts.
/// The top-level identity and Groups belong to the Member when both accounts are signed in.
/// </summary>
/// <param name="Key">The application's key for the account (<see cref="ApplicationAccessPolicy.UserKey"/>): the member id, or <c>backoffice:</c> and the user id.</param>
/// <param name="Sid">The external login provider's key; null when no external login is linked. It is not necessarily a Windows SID.</param>
/// <param name="Groups">Member Group names for a Member, or User Group names for a backoffice User.</param>
/// <param name="IsBackOfficeUser">True when a backoffice User is signed in, including alongside a Member.</param>
public sealed record DataSourceUser(string Key, string? Sid, string? Name, string? Email, IReadOnlyList<string> Groups, bool IsBackOfficeUser)
{
    private DataSourceBackOfficeAccount? AdditionalBackOfficeAccount { get; init; }

    /// <summary>
    /// Nobody: what a policy sees for an anonymous visitor using a public data source.
    /// It has no key, name, email or groups, and is neither a Member nor a backoffice User.
    /// </summary>
    public static DataSourceUser Anonymous { get; } = new("", null, null, null, [], false);

    public bool IsAnonymous => Key.Length == 0;
    public bool IsMember => !IsAnonymous && (!IsBackOfficeUser || AdditionalBackOfficeAccount is not null);
    /// <summary>The Member's own identity and groups; null without a valid Member account.</summary>
    public DataSourceMemberAccount? Member => IsMember ? new(Key, Sid, Name, Email, Groups) : null;
    /// <summary>The backoffice User's own identity and groups; null without a valid backoffice account.</summary>
    public DataSourceBackOfficeAccount? BackOfficeUser => !IsBackOfficeUser || IsAnonymous ? null
        : AdditionalBackOfficeAccount is { } account ? account with {
            GroupAliases = UserGroupAliases, CanEditPage = CanEditPage, CanPublishPage = CanPublishPage, CanDeletePage = CanDeletePage,
        } : new(Key, Sid, Name, Email, Groups, UserGroupAliases, CanEditPage) {
            CanPublishPage = CanPublishPage, CanDeletePage = CanDeletePage,
        };
    /// <summary>
    /// Whether this backoffice User has Umbraco's Update permission on the calling page, within their content start nodes.
    /// Resolve with a <see cref="FormExecutionContext"/> to supply that page. False without a backoffice account,
    /// or when no page is supplied. This does not itself grant permission to change application data.
    /// </summary>
    public bool CanEditPage { get; init; }
    /// <summary>
    /// Whether this backoffice User has Umbraco's Publish permission on the calling page, within their content start nodes.
    /// Uses the same page context and account requirements as <see cref="CanEditPage"/>; checked independently of edit permission.
    /// </summary>
    public bool CanPublishPage { get; init; }
    /// <summary>
    /// Whether this backoffice User has Umbraco's Delete permission on the calling page, within their content start nodes.
    /// Uses the same page context and account requirements as <see cref="CanEditPage"/>; checked independently of edit permission.
    /// </summary>
    public bool CanDeletePage { get; init; }
    /// <summary>Current Member Group names. Empty without a Member account.</summary>
    public IReadOnlyList<string> MemberGroupNames => IsMember ? Groups : [];
    /// <summary>Current User Group display names. Empty without a backoffice account.</summary>
    public IReadOnlyList<string> UserGroupNames => IsBackOfficeUser ? AdditionalBackOfficeAccount?.GroupNames ?? Groups : [];
    /// <summary>Current User Group aliases, such as "admin". Aliases can be deliberately changed in Umbraco. Empty without a backoffice account.</summary>
    public IReadOnlyList<string> UserGroupAliases { get; init; } = [];
    /// <summary>Checks a Member Group name, ignoring case. Does not accept a numeric ID or GUID.</summary>
    public bool IsInMemberGroup(string name) => IsMember && Groups.Contains(name, StringComparer.OrdinalIgnoreCase);
    /// <summary>Checks a backoffice User Group alias, ignoring case. Does not accept its display name, numeric ID or GUID.</summary>
    public bool IsInUserGroup(string alias) => IsBackOfficeUser && UserGroupAliases.Contains(alias, StringComparer.OrdinalIgnoreCase);

    internal static DataSourceUser Combine(DataSourceUser member, DataSourceUser backOfficeUser) => member with {
        IsBackOfficeUser = true,
        AdditionalBackOfficeAccount = backOfficeUser.BackOfficeUser,
        UserGroupAliases = backOfficeUser.UserGroupAliases,
        CanEditPage = backOfficeUser.CanEditPage,
        CanPublishPage = backOfficeUser.CanPublishPage,
        CanDeletePage = backOfficeUser.CanDeletePage,
    };
}

/// <summary>Resolves authenticated Umbraco Member and backoffice accounts. Null means neither account is valid; authorization belongs to the application.</summary>
public interface IDataSourceUserResolver
{
    /// <summary>Resolves account facts without a calling page; all page permission properties are false.</summary>
    Task<DataSourceUser?> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct);

    /// <summary>
    /// Resolves current account facts and page permissions for <see cref="FormExecutionContext.PageKey"/>.
    /// Custom resolvers should implement this overload to supply page permissions; existing implementations still resolve identity only.
    /// </summary>
    Task<DataSourceUser?> ResolveAsync(FormExecutionContext context, CancellationToken ct) => ResolveAsync(context.Member, ct);
}

/// <summary>
/// Reads current account status and groups from Umbraco on every operation. Both Members and backoffice Users
/// must be approved and unlocked. User Group membership never decides whether an identity can be resolved.
/// </summary>
public sealed class UmbracoDataSourceUsers(IMemberManager members, ApplicationAccessPolicy access, IExternalLoginWithKeyService logins,
    IContentPermissionService? permissions = null) : IDataSourceUserResolver
{
    /// <summary>Enough groups for any real account, few enough to stay far below a statement's parameter limit.</summary>
    public const int MaxGroups = 500;

    public Task<DataSourceUser?> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct) => ResolveAsync(principal, Guid.Empty, ct);

    public Task<DataSourceUser?> ResolveAsync(FormExecutionContext context, CancellationToken ct) => ResolveAsync(context.Member, context.PageKey, ct);

    private async Task<DataSourceUser?> ResolveAsync(ClaimsPrincipal principal, Guid pageKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var member = await ResolveMemberAsync(ApplicationAccessPolicy.MemberIdentity(principal));
        var backOfficeUser = await ResolveBackOfficeAsync(ApplicationAccessPolicy.BackOfficeIdentity(principal), pageKey);
        return member is null ? backOfficeUser : backOfficeUser is null ? member : DataSourceUser.Combine(member, backOfficeUser);
    }

    private async Task<DataSourceUser?> ResolveBackOfficeAsync(ClaimsIdentity? identity, Guid pageKey)
    {
        if (identity?.FindFirst(ClaimTypes.NameIdentifier)?.Value is { Length: > 0 } id)
        {
            var user = await access.ActiveUserAsync(new ClaimsPrincipal(identity));
            return user is null ? null
                : new("backoffice:" + id, Sid(user.Key), user.Username, user.Email,
                    Bounded(user.Groups.Select(g => g.Name ?? g.Alias)), IsBackOfficeUser: true) {
                    UserGroupAliases = Bounded(user.Groups.Select(g => g.Alias)),
                    CanEditPage = await HasPagePermissionAsync(user, pageKey, ActionUpdate.ActionLetter),
                    CanPublishPage = await HasPagePermissionAsync(user, pageKey, ActionPublish.ActionLetter),
                    CanDeletePage = await HasPagePermissionAsync(user, pageKey, ActionDelete.ActionLetter),
                };
        }
        return null;
    }

    private async Task<bool> HasPagePermissionAsync(IUser user, Guid pageKey, string permission)
        => pageKey != Guid.Empty && permissions is not null
            && await permissions.AuthorizeAccessAsync(user, pageKey, permission) == ContentAuthorizationStatus.Success;

    private async Task<DataSourceUser?> ResolveMemberAsync(ClaimsIdentity? identity)
    {
        if (identity?.FindFirst(ClaimTypes.NameIdentifier)?.Value is not { Length: > 0 } id) return null;
        var member = await members.FindByIdAsync(id);
        if (member is not { IsApproved: true, IsLockedOut: false }) return null;
        return new(id, Sid(member.Key), member.UserName, member.Email,
            Bounded(await members.GetRolesAsync(member)), IsBackOfficeUser: false);
    }

    private string? Sid(Guid key) => logins.GetExternalLogins(key).Select(login => login.ProviderKey).FirstOrDefault(sid => !string.IsNullOrWhiteSpace(sid));

    private static IReadOnlyList<string> Bounded(IEnumerable<string?> groups)
        => groups.OfType<string>().Where(g => g.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Take(MaxGroups).ToArray();
}

/// <summary>The operation a data source is asked to perform, as <see cref="IDataSourcePolicy"/> sees it.</summary>
public enum DataSourceAction { Initialize, Load, Create, Update, Delete, Query }

/// <summary>One operation, after the data source's own checks passed.</summary>
/// <param name="Current">The record as stored, by field alias in form values (Load, Update, Delete); null otherwise.</param>
/// <param name="Proposed">The record as it would be stored (Initialize's starting values, Create, Update); null otherwise.</param>
/// <param name="Connection">
/// The engine's open connection for Load, Create, Update, Delete and Query; null for Initialize, which opens nothing,
/// and for the check a report request makes before its own query. A policy may run its own commands on it to look at
/// other rows in the same snapshot, and must never close or dispose it.
/// </param>
/// <param name="Transaction">
/// The write transaction for Create, Update and Delete; null otherwise. A policy's commands set
/// <c>command.Transaction = request.Transaction</c>; it must never commit, roll back or dispose it.
/// </param>
public sealed record DataSourceRequest(string DataSource, DataSourceAction Action, DataSourceUser User, Forms.FormExecutionContext Context,
    string? Key, IReadOnlyDictionary<string, object?>? Current, IReadOnlyDictionary<string, object?>? Proposed,
    System.Data.Common.DbConnection? Connection = null, System.Data.Common.DbTransaction? Transaction = null);

/// <summary>
/// A rule in C# for what a predicate cannot say, such as "only the requester may cancel, and only while the order
/// is NEW". Every registered policy must allow an operation; a refusal returns 403 and nothing is written. Writes
/// call it inside their transaction with the row locked, so the current values it sees are the ones replaced, and
/// the request carries that connection and transaction so the policy can read other rows in the same snapshot.
/// </summary>
public interface IDataSourcePolicy
{
    ValueTask<bool> IsAllowedAsync(DataSourceRequest request, CancellationToken ct);
}

/// <summary>
/// A policy that governs only some requests, such as a plugin's own data sources. AppBlocks asks <see cref="Applies"/> first
/// and skips the policy for any request it answers false; for the others the policy decides as any policy does. A policy that
/// does not implement this interface is asked about every data source on every page.
/// </summary>
public interface IScopedDataSourcePolicy : IDataSourcePolicy
{
    /// <summary>Whether this policy decides the request. Answer from the request alone and quickly: it is asked for every operation.</summary>
    bool Applies(DataSourceRequest request);
}

/// <summary>
/// The requests a policy registered with <see cref="DataSourcePolicyExtensions.AddDataSourcePolicy{TPolicy}"/> governs: data sources
/// whose alias starts with <paramref name="AliasPrefix"/>, ignoring case, and, when <paramref name="Pages"/> is given, only operations
/// from those pages. Null for either matches everything.
/// </summary>
/// <remarks>
/// The page is the one the operation came from. An API endpoint call has no page, so a page-scoped policy never governs it;
/// scope by alias alone when the rule must hold wherever the data source is used.
/// </remarks>
public sealed record DataSourcePolicyScope(string? AliasPrefix = null, IReadOnlyCollection<Guid>? Pages = null)
{
    public bool Matches(DataSourceRequest request)
        => (string.IsNullOrEmpty(AliasPrefix) || request.DataSource.StartsWith(AliasPrefix, StringComparison.OrdinalIgnoreCase))
            && (Pages is null || Pages.Contains(request.Context.PageKey));
}

public static class DataSourcePolicyExtensions
{
    /// <summary>
    /// Registers <typeparamref name="TPolicy"/> as an <see cref="IDataSourcePolicy"/> that AppBlocks asks only about the requests
    /// <paramref name="scope"/> matches, so the class needs no early return for everything that is not its own. The class is made per
    /// request scope, through dependency injection.
    /// </summary>
    public static IServiceCollection AddDataSourcePolicy<TPolicy>(this IServiceCollection services, DataSourcePolicyScope scope) where TPolicy : class, IDataSourcePolicy
    {
        ArgumentNullException.ThrowIfNull(scope);
        services.AddScoped<IDataSourcePolicy>(provider => new ScopedPolicy(ActivatorUtilities.GetServiceOrCreateInstance<TPolicy>(provider), scope));
        return services;
    }

    private sealed class ScopedPolicy(IDataSourcePolicy inner, DataSourcePolicyScope scope) : IScopedDataSourcePolicy
    {
        public bool Applies(DataSourceRequest request) => scope.Matches(request) && (inner is not IScopedDataSourcePolicy narrower || narrower.Applies(request));
        public ValueTask<bool> IsAllowedAsync(DataSourceRequest request, CancellationToken ct) => inner.IsAllowedAsync(request, ct);
    }

    /// <summary>Whether every policy that governs the request allows it; a scoped policy whose scope does not match is skipped.</summary>
    internal static async ValueTask<bool> AllowAsync(this IEnumerable<IDataSourcePolicy> policies, DataSourceRequest request, CancellationToken ct)
    {
        foreach (var policy in policies)
        {
            if (policy is IScopedDataSourcePolicy scoped && !scoped.Applies(request)) continue;
            if (!await policy.IsAllowedAsync(request, ct)) return false;
        }
        return true;
    }
}
