using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Umbraco.Cms.Core.Services;
using AppBlocks.Authentication;

namespace AppBlocks.DataSources;

/// <summary>
/// The current Umbraco Member or backoffice User. These identity facts do not grant application permissions.
/// </summary>
/// <param name="Key">The application's key for the account (<see cref="ApplicationAccessPolicy.UserKey"/>): the member id, or <c>backoffice:</c> and the user id.</param>
/// <param name="Sid">The Windows SID the account is linked to, its external login key; null for an account created with a password.</param>
/// <param name="Groups">Member Group names for a Member, or User Group names for a backoffice User.</param>
/// <param name="IsBackOfficeUser">True for a backoffice User; false for an Umbraco Member.</param>
public sealed record DataSourceUser(string Key, string? Sid, string? Name, string? Email, IReadOnlyList<string> Groups, bool IsBackOfficeUser)
{
    public bool IsMember => !IsBackOfficeUser;
    /// <summary>Stable User Group aliases, such as "admin". Empty for Members.</summary>
    public IReadOnlyList<string> UserGroupAliases { get; init; } = [];
    public bool IsInMemberGroup(string name) => IsMember && Groups.Contains(name, StringComparer.OrdinalIgnoreCase);
    public bool IsInUserGroup(string alias) => IsBackOfficeUser && UserGroupAliases.Contains(alias, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Resolves an authenticated Umbraco Member or backoffice User. Null means no valid account; authorization belongs to the application.</summary>
public interface IDataSourceUserResolver
{
    Task<DataSourceUser?> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct);
}

/// <summary>
/// Reads current account status and groups from Umbraco on every operation. Both Members and backoffice Users
/// must be approved and unlocked. User Group membership never decides whether an identity can be resolved.
/// </summary>
public sealed class UmbracoDataSourceUsers(IMemberService members, IUserService users, IExternalLoginWithKeyService logins) : IDataSourceUserResolver
{
    /// <summary>Enough groups for any real account, few enough to stay far below a statement's parameter limit.</summary>
    public const int MaxGroups = 500;

    public async Task<DataSourceUser?> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (principal.Identity?.IsAuthenticated != true || principal.FindFirstValue(ClaimTypes.NameIdentifier) is not { Length: > 0 } id) return null;
        if (ApplicationAccessPolicy.IsBackOfficeIdentity(principal))
        {
            var user = Guid.TryParse(id, out var key) ? await users.GetAsync(key) : int.TryParse(id, out var legacy) ? users.GetUserById(legacy) : null;
            return user is not { IsApproved: true, IsLockedOut: false } ? null
                : new(ApplicationAccessPolicy.UserKey(principal), Sid(user.Key), user.Username, user.Email,
                    Bounded(user.Groups.Select(g => g.Name ?? g.Alias)), IsBackOfficeUser: true) {
                    UserGroupAliases = Bounded(user.Groups.Select(g => g.Alias)),
                };
        }
        if (principal.Identity.AuthenticationType != IdentityConstants.ApplicationScheme) return null;
        var member = int.TryParse(id, out var memberId) ? members.GetById(memberId)
            : Guid.TryParse(id, out var memberKey) ? (await members.GetByKeysAsync(memberKey)).FirstOrDefault() : null;
        if (member is not { IsApproved: true, IsLockedOut: false }) return null;
        return new(ApplicationAccessPolicy.UserKey(principal), Sid(member.Key), member.Username, member.Email,
            Bounded(members.GetAllRoles(member.Username)), IsBackOfficeUser: false);
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
