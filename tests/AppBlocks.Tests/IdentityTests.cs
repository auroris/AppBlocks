using System.Reflection;
using System.Security.Claims;
using AppBlocks.Authentication;
using AppBlocks.Blocks;
using AppBlocks.DataSources;
using AppBlocks.Forms;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Actions;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.AuthorizationStatus;

namespace AppBlocks.Tests;

public sealed class IdentityTests
{
    private static ClaimsPrincipal Principal(string scheme, string id = "42") => new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, id)], scheme));

    [Fact]
    public async Task Resolver_returns_ordinary_backoffice_users_and_refreshes_their_groups()
    {
        var key = Guid.NewGuid();
        var approved = true; var locked = false;
        IReadOnlyUserGroup[] groups = [Group("Editorial team", "editor")];
        var user = Stub<IUser>((method, _) => method.Name switch {
            "get_Key" => key, "get_IsApproved" => approved, "get_IsLockedOut" => locked,
            "get_Username" => "editor", "get_Email" => "editor@example.test", "get_Groups" => groups,
            _ => throw new NotImplementedException(method.Name),
        });
        var users = Stub<IUserService>((method, _) => method.Name == "GetAsync" ? Task.FromResult<IUser?>(user) : throw new NotImplementedException(method.Name));
        // A resolver serves one request, so each new one reads the account afresh.
        UmbracoDataSourceUsers Request() => new(Stub<IMemberManager>(Never), new ApplicationAccessPolicy(users), Logins());
        var principal = Principal(Constants.Security.BackOfficeAuthenticationType, key.ToString());
        var account = (await Request().ResolveAsync(principal, default))!;
        Assert.True(account.IsBackOfficeUser); Assert.False(account.IsMember);
        Assert.True(account.IsInUserGroup("EDITOR")); Assert.False(account.IsInUserGroup("admin"));
        Assert.False(account.IsInMemberGroup("Editorial team"));
        Assert.Equal(["Editorial team"], account.Groups); Assert.Equal("backoffice:" + key, account.Key);
        groups = [Group("Site administrators", "admin")];
        account = (await Request().ResolveAsync(principal, default))!;
        Assert.True(account.IsInUserGroup("admin")); Assert.False(account.IsInUserGroup("editor"));
        locked = true; Assert.Null(await Request().ResolveAsync(principal, default));
        locked = false; approved = false; Assert.Null(await Request().ResolveAsync(principal, default));
    }

    [Theory]
    [InlineData("42")]
    [InlineData("ea833980-fb13-4c09-97db-e26739d6c9d2")]
    public async Task Member_groups_do_not_become_backoffice_user_groups(string id)
    {
        var member = new MemberIdentityUser { Key = Guid.Empty, IsApproved = true, UserName = "member", Email = "member@example.test" };
        var members = Stub<IMemberManager>((method, args) => method.Name switch {
            "FindByIdAsync" when Equals(args![0], id) => Task.FromResult<MemberIdentityUser?>(member),
            "GetRolesAsync" when ReferenceEquals(args![0], member) => Task.FromResult<IList<string>>(["Administrators", "Order editors"]),
            _ => throw new NotImplementedException(method.Name),
        });
        // A resolver serves one request, so each new one reads the account afresh.
        UmbracoDataSourceUsers Request() => new(members, new ApplicationAccessPolicy(Stub<IUserService>(Never)), Logins());
        var principal = Principal(IdentityConstants.ApplicationScheme, id);
        var account = (await Request().ResolveAsync(principal, default))!;
        Assert.True(account.IsMember); Assert.False(account.IsBackOfficeUser);
        Assert.True(account.IsInMemberGroup("order EDITORS"));
        Assert.False(account.IsInUserGroup("admin")); Assert.False(account.IsInUserGroup("Administrators"));
        Assert.Equal(id, account.Key); Assert.Empty(account.UserGroupAliases);
        member.LockoutEnd = DateTimeOffset.MaxValue; Assert.Null(await Request().ResolveAsync(principal, default));
        member.LockoutEnd = null; member.IsApproved = false; Assert.Null(await Request().ResolveAsync(principal, default));
        Assert.Null(await Request().ResolveAsync(new ClaimsPrincipal(), default));
        Assert.Null(await Request().ResolveAsync(Principal("unrecognised"), default));
    }

    [Fact]
    public async Task A_request_reads_the_account_once_however_many_operations_ask()
    {
        var member = new MemberIdentityUser { Key = Guid.NewGuid(), IsApproved = true, UserName = "member", Email = "member@example.test" };
        var reads = 0;
        Task<MemberIdentityUser?> Read() { reads++; return Task.FromResult<MemberIdentityUser?>(member); }
        var members = Stub<IMemberManager>((method, _) => method.Name switch {
            "FindByIdAsync" => Read(),
            "GetRolesAsync" => Task.FromResult<IList<string>>(["Order editors"]),
            _ => throw new NotImplementedException(method.Name),
        });
        var resolver = new UmbracoDataSourceUsers(members, new ApplicationAccessPolicy(Stub<IUserService>(Never)), Logins());
        var page = Guid.NewGuid();
        var context = new FormExecutionContext(Principal(IdentityConstants.ApplicationScheme), page, Guid.NewGuid(), "1");
        // A form update asks for the account in the endpoint, the load before the change, the change itself and each policy.
        foreach (var asked in new[] { context, context with { SubmissionKey = "a" }, context with { FormKey = Guid.NewGuid() } })
            Assert.True((await resolver.ResolveAsync(asked, default))!.IsInMemberGroup("Order editors"));
        Assert.Equal(1, reads);
        // Another page has page permissions of its own, so it is read for that page; the next request reads everything afresh.
        await resolver.ResolveAsync(context with { PageKey = Guid.NewGuid() }, default);
        Assert.Equal(2, reads);
        member.IsApproved = false;
        Assert.NotNull(await resolver.ResolveAsync(context, default));
        Assert.Null(await new UmbracoDataSourceUsers(members, new ApplicationAccessPolicy(Stub<IUserService>(Never)), Logins()).ResolveAsync(context, default));
    }

    [Theory]
    [InlineData(false, ActionUpdate.ActionLetter)]
    [InlineData(true, ActionUpdate.ActionLetter)]
    [InlineData(false, ActionPublish.ActionLetter)]
    [InlineData(true, ActionPublish.ActionLetter)]
    [InlineData(false, ActionDelete.ActionLetter)]
    [InlineData(true, ActionDelete.ActionLetter)]
    public async Task Page_permissions_use_independent_current_Umbraco_permissions_for_the_calling_page(bool legacyId, string action)
    {
        var key = Guid.NewGuid();
        var pageKey = Guid.NewGuid();
        var approved = true; var locked = false;
        var status = ContentAuthorizationStatus.Success;
        var permissionCalls = 0;
        var user = Stub<IUser>((method, _) => method.Name switch {
            "get_Key" => key, "get_IsApproved" => approved, "get_IsLockedOut" => locked,
            "get_Username" => "editor", "get_Email" => "editor@example.test", "get_Groups" => new[] { Group("Editors", "editor") },
            _ => throw new NotImplementedException(method.Name),
        });
        var users = Stub<IUserService>((method, args) => {
            Assert.Equal(legacyId ? "GetUserById" : "GetAsync", method.Name);
            Assert.Equal(legacyId ? (object)42 : key, args![0]);
            return legacyId ? user : Task.FromResult<IUser?>(user);
        });
        var permissions = Stub<IContentPermissionService>((method, args) => {
            Assert.Equal("AuthorizeAccessAsync", method.Name);
            Assert.Same(user, args![0]);
            Assert.Contains((string)args[2]!, new[] { ActionUpdate.ActionLetter, ActionPublish.ActionLetter, ActionDelete.ActionLetter });
            permissionCalls++;
            return Task.FromResult((Guid)args[1]! != pageKey ? ContentAuthorizationStatus.UnauthorizedMissingPathAccess
                : (string)args[2]! == action ? status : ContentAuthorizationStatus.UnauthorizedMissingPermissionAccess);
        });
        // A resolver serves one request, so each new one reads the account and its permissions afresh.
        IDataSourceUserResolver Request() => new UmbracoDataSourceUsers(Stub<IMemberManager>(Never), new ApplicationAccessPolicy(users), Logins(), permissions);
        var principal = Principal(Constants.Security.BackOfficeExposedAuthenticationType, legacyId ? "42" : key.ToString());
        var context = new FormExecutionContext(principal, pageKey, Guid.NewGuid(), "1");
        Check((await Request().ResolveAsync(context, default))!, true);
        Check((await Request().ResolveAsync(context with { PageKey = Guid.NewGuid() }, default))!, false);
        // No cached grant: missing nodes, denied path access and denied permissions all fail closed on the next request.
        foreach (var result in Enum.GetValues<ContentAuthorizationStatus>()) {
            status = result;
            Check((await Request().ResolveAsync(context, default))!, result == ContentAuthorizationStatus.Success);
        }
        var checkedCalls = permissionCalls;
        Check((await Request().ResolveAsync(principal, default))!, false);
        Check((await Request().ResolveAsync(context with { PageKey = Guid.Empty }, default))!, false);
        locked = true; Assert.Null(await Request().ResolveAsync(context, default));
        locked = false; approved = false; Assert.Null(await Request().ResolveAsync(context, default));
        Assert.Null(await Request().ResolveAsync(context with { Member = new ClaimsPrincipal() }, default));
        Assert.Equal(checkedCalls, permissionCalls);

        void Check(DataSourceUser account, bool allowed)
        {
            Assert.Equal(allowed && action == ActionUpdate.ActionLetter, account.CanEditPage);
            Assert.Equal(allowed && action == ActionPublish.ActionLetter, account.CanPublishPage);
            Assert.Equal(allowed && action == ActionDelete.ActionLetter, account.CanDeletePage);
            Assert.Equal(account.CanEditPage, account.BackOfficeUser!.CanEditPage);
            Assert.Equal(account.CanPublishPage, account.BackOfficeUser.CanPublishPage);
            Assert.Equal(account.CanDeletePage, account.BackOfficeUser.CanDeletePage);
        }
    }

    [Fact]
    public async Task Members_do_not_receive_page_permissions_even_with_a_matching_backoffice_id()
    {
        var member = new MemberIdentityUser { Key = Guid.NewGuid(), IsApproved = true, UserName = "member", Email = "member@example.test" };
        var members = Stub<IMemberManager>((method, _) => method.Name switch {
            "FindByIdAsync" => Task.FromResult<MemberIdentityUser?>(member), "GetRolesAsync" => Task.FromResult<IList<string>>(["Administrators"]),
            _ => throw new NotImplementedException(method.Name),
        });
        var resolver = new UmbracoDataSourceUsers(members, new ApplicationAccessPolicy(Stub<IUserService>(Never)), Logins(), Stub<IContentPermissionService>(Never));
        var context = new FormExecutionContext(Principal(IdentityConstants.ApplicationScheme), Guid.NewGuid(), Guid.NewGuid(), "1");
        var account = (await resolver.ResolveAsync(context, default))!;
        Assert.False(account.CanEditPage);
        Assert.False(account.CanPublishPage);
        Assert.False(account.CanDeletePage);
        Assert.False(DataSourceUser.Anonymous.CanEditPage);
        Assert.False(DataSourceUser.Anonymous.CanPublishPage);
        Assert.False(DataSourceUser.Anonymous.CanDeletePage);
    }

    [Theory]
    [InlineData(false, null, true)]
    [InlineData(false, true, true)]
    [InlineData(true, null, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task Backoffice_users_reach_open_pages_and_pass_Public_Access_only_when_a_host_admission_admits_them(bool protectedPage, bool? admits, bool allowed)
    {
        var key = Guid.NewGuid();
        var pageKey = Guid.NewGuid();
        var principal = Principal(Constants.Security.BackOfficeAuthenticationType, key.ToString());
        var admitted = Principal("HostAdmission", "7");
        var user = Stub<IUser>((method, _) => method.Name switch {
            "get_IsApproved" => true, "get_IsLockedOut" => false,
            _ => throw new NotImplementedException(method.Name),
        });
        var users = Stub<IUserService>((method, _) => method.Name == "GetAsync" ? Task.FromResult<IUser?>(user) : throw new NotImplementedException(method.Name));
        var authentication = Stub<IAuthenticationService>((method, args) => method.Name == "AuthenticateAsync"
            ? Task.FromResult((string)args![1]! == Constants.Security.BackOfficeAuthenticationType
                ? AuthenticateResult.Success(new AuthenticationTicket(principal, Constants.Security.BackOfficeAuthenticationType)) : AuthenticateResult.NoResult())
            : throw new NotImplementedException(method.Name));
        using var services = new ServiceCollection().AddSingleton(authentication).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        var protection = Stub<IPublicAccessService>((method, _) => method.Name == "IsProtected"
            ? (protectedPage ? Attempt<PublicAccessEntry>.Succeed(new(Guid.NewGuid(), 42, 43, 44, [])) : Attempt<PublicAccessEntry>.Fail())
            : throw new NotImplementedException(method.Name));
        var page = Stub<IPublishedContent>((method, _) => method.Name switch {
            "get_Path" => "-1,42", "get_Key" => pageKey, _ => throw new NotImplementedException(method.Name),
        });
        // A host's rule is asked only once Umbraco has refused: a protected page and no Member session.
        var asked = 0;
        IProtectedPageAdmission Admission(ClaimsPrincipal? answer) => Stub<IProtectedPageAdmission>((method, args) => {
            Assert.Equal("AdmitAsync", method.Name); Assert.True(protectedPage);
            Assert.Same(context, args![0]); Assert.Same(page, args[1]);
            asked++; return Task.FromResult(answer);
        });
        // No rule at all is AppBlocks' own default. An unauthenticated answer counts as no answer, and the first
        // authenticated answer decides without asking the rules after it.
        IProtectedPageAdmission[] admissions = admits switch {
            null => [],
            false => [Admission(AppPageAccess.Anonymous)],
            true => [Admission(null), Admission(admitted), Stub<IProtectedPageAdmission>(Never)],
        };
        var access = new AppPageAccess(null!, Stub<IPublicAccessChecker>(Never), new HttpContextAccessor { HttpContext = context },
            new ApplicationAccessPolicy(users), protection, admissions);
        var result = await access.UserAsync(page);
        Assert.Equal(allowed, result is not null);
        if (!protectedPage) { Assert.Equal(0, asked); Assert.Same(principal, result); }
        else Assert.Equal(admits switch { null => 0, false => 1, true => 2 }, asked);
        if (protectedPage && allowed) Assert.Same(admitted, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Combined_accounts_keep_each_identity_and_group_store_separate_and_refresh_both(bool backOfficeFirst)
    {
        var userApproved = true;
        var memberKey = Guid.NewGuid(); var userKey = Guid.NewGuid(); var pageKey = Guid.NewGuid();
        var member = new MemberIdentityUser { Key = memberKey, IsApproved = true, UserName = "website-member", Email = "member@example.test" };
        var user = Stub<IUser>((method, _) => method.Name switch {
            "get_Key" => userKey, "get_IsApproved" => userApproved, "get_IsLockedOut" => false,
            "get_Username" => "backoffice-editor", "get_Email" => "editor@example.test", "get_Groups" => new[] { Group("Editors", "editor") },
            _ => throw new NotImplementedException(method.Name),
        });
        var members = Stub<IMemberManager>((method, args) => method.Name switch {
            "FindByIdAsync" when Equals(args![0], "42") => Task.FromResult<MemberIdentityUser?>(member),
            "GetRolesAsync" when ReferenceEquals(args![0], member) => Task.FromResult<IList<string>>(["Customers"]),
            _ => throw new NotImplementedException(method.Name),
        });
        var users = Stub<IUserService>((method, args) => method.Name == "GetUserById" && Equals(args![0], 42)
            ? user : throw new NotImplementedException(method.Name));
        var permissions = Stub<IContentPermissionService>((method, args) => {
            Assert.Equal("AuthorizeAccessAsync", method.Name); Assert.Same(user, args![0]);
            Assert.Equal(pageKey, args[1]);
            Assert.Contains((string)args[2]!, new[] { ActionUpdate.ActionLetter, ActionPublish.ActionLetter, ActionDelete.ActionLetter });
            return Task.FromResult((string)args[2]! == ActionDelete.ActionLetter
                ? ContentAuthorizationStatus.UnauthorizedMissingPermissionAccess : ContentAuthorizationStatus.Success);
        });
        // A resolver serves one request, so each new one reads both accounts afresh.
        UmbracoDataSourceUsers Request() => new(members, new ApplicationAccessPolicy(users), Logins(), permissions);
        // Matching numeric IDs in separate stores still identify different accounts.
        var identities = new[] { (ClaimsIdentity)Principal(IdentityConstants.ApplicationScheme).Identity!,
            (ClaimsIdentity)Principal(Constants.Security.BackOfficeAuthenticationType).Identity! };
        var principal = new ClaimsPrincipal(backOfficeFirst ? identities.Reverse() : identities);
        var context = new FormExecutionContext(principal, pageKey, Guid.NewGuid(), "1");
        var account = (await Request().ResolveAsync(context, default))!;
        Assert.True(account.IsMember && account.IsBackOfficeUser); Assert.False(account.IsAnonymous);
        Assert.Equal("42", account.Key); Assert.Equal("website-member", account.Name);
        Assert.Equal(new DataSourceMemberAccount("42", null, "website-member", "member@example.test", account.Groups), account.Member);
        Assert.Equal("backoffice:42", account.BackOfficeUser!.Key);
        Assert.Equal("backoffice-editor", account.BackOfficeUser.Name); Assert.Equal("editor@example.test", account.BackOfficeUser.Email);
        Assert.Equal(["Customers"], account.MemberGroupNames); Assert.Equal(["Editors"], account.UserGroupNames);
        Assert.Equal(["editor"], account.BackOfficeUser.GroupAliases);
        Assert.True(account.IsInMemberGroup("customers")); Assert.False(account.IsInMemberGroup("Editors"));
        Assert.True(account.IsInUserGroup("EDITOR")); Assert.False(account.IsInUserGroup("Customers"));
        Assert.True(account.CanEditPage && account.BackOfficeUser.CanEditPage);
        Assert.True(account.CanPublishPage && account.BackOfficeUser.CanPublishPage);
        Assert.False(account.CanDeletePage); Assert.False(account.BackOfficeUser.CanDeletePage);
        var noPage = (await Request().ResolveAsync(principal, default))!;
        Assert.False(noPage.CanEditPage); Assert.False(noPage.CanPublishPage); Assert.False(noPage.CanDeletePage);

        userApproved = false;
        account = (await Request().ResolveAsync(context, default))!;
        Assert.True(account.IsMember); Assert.False(account.IsBackOfficeUser);
        Assert.Null(account.BackOfficeUser); Assert.False(account.CanEditPage); Assert.Empty(account.UserGroupAliases);
        Assert.False(account.CanPublishPage); Assert.False(account.CanDeletePage);
        userApproved = true; member.LockoutEnd = DateTimeOffset.MaxValue;
        account = (await Request().ResolveAsync(context, default))!;
        Assert.False(account.IsMember); Assert.True(account.IsBackOfficeUser);
        Assert.Null(account.Member); Assert.Equal("backoffice:42", account.Key); Assert.Empty(account.MemberGroupNames);
        userApproved = false;
        Assert.Null(await Request().ResolveAsync(context, default));
    }

    [Theory]
    [InlineData(PublicAccessStatus.AccessAccepted)]
    [InlineData(PublicAccessStatus.AccessDenied)]
    public async Task The_page_policy_admits_a_backoffice_only_caller_when_the_Member_cookie_is_also_on_the_request(PublicAccessStatus memberAccess)
    {
        var pageKey = Guid.NewGuid(); var userKey = Guid.NewGuid();
        var member = Principal(IdentityConstants.ApplicationScheme, "7");
        var backOffice = Principal(Constants.Security.BackOfficeAuthenticationType, userKey.ToString());
        var authentication = Stub<IAuthenticationService>((method, args) => method.Name == "AuthenticateAsync" ? Task.FromResult((string)args![1]! is var scheme
            && scheme == IdentityConstants.ApplicationScheme ? AuthenticateResult.Success(new AuthenticationTicket(member, scheme))
            : scheme == Constants.Security.BackOfficeAuthenticationType ? AuthenticateResult.Success(new AuthenticationTicket(backOffice, scheme))
            : AuthenticateResult.NoResult()) : throw new NotImplementedException(method.Name));
        var user = Stub<IUser>((method, _) => method.Name switch {
            "get_IsApproved" => true, "get_IsLockedOut" => false, _ => throw new NotImplementedException(method.Name),
        });
        var users = Stub<IUserService>((method, _) => method.Name == "GetAsync" ? Task.FromResult<IUser?>(user) : throw new NotImplementedException(method.Name));
        var blocks = Stub<IPublishedPropertyType>((method, _) => method.Name == "get_EditorAlias" ? "Umbraco.BlockGrid" : throw new NotImplementedException(method.Name));
        var type = Stub<IPublishedContentType>((method, _) => method.Name == "get_PropertyTypes" ? new[] { blocks } : throw new NotImplementedException(method.Name));
        var page = Stub<IPublishedContent>((method, _) => method.Name switch {
            "get_Id" => 42, "get_Key" => pageKey, "get_Path" => "-1,42", "get_ContentType" => type, _ => throw new NotImplementedException(method.Name),
        });
        var cache = Stub<Umbraco.Cms.Core.PublishedCache.IPublishedContentCache>((method, args) => method.Name == "GetByIdAsync" && Equals(args![0], pageKey)
            ? Task.FromResult<IPublishedContent?>(page) : throw new NotImplementedException(method.Name));
        var umbraco = Stub<Umbraco.Cms.Core.Web.IUmbracoContext>((method, _) => method.Name == "get_Content" ? cache : throw new NotImplementedException(method.Name));
        var factory = Stub<Umbraco.Cms.Core.Web.IUmbracoContextFactory>((method, _) => method.Name == "EnsureUmbracoContext"
            ? new Umbraco.Cms.Core.UmbracoContextReference(umbraco, false, Stub<Umbraco.Cms.Core.Web.IUmbracoContextAccessor>(Never)) : throw new NotImplementedException(method.Name));
        var checker = Stub<IPublicAccessChecker>((method, _) => method.Name == "HasMemberAccessToContentAsync" ? Task.FromResult(memberAccess) : throw new NotImplementedException(method.Name));
        var http = new HttpContextAccessor();
        var access = new AppPageAccess(new AppBlockContent(factory, http), checker, http, new ApplicationAccessPolicy(users), Stub<IPublicAccessService>((method, _) => method.Name == "IsProtected" ? Attempt<PublicAccessEntry>.Fail() : throw new NotImplementedException(method.Name)), []);
        using var services = new ServiceCollection().AddSingleton(authentication).AddSingleton(access).BuildServiceProvider();
        http.HttpContext = new DefaultHttpContext { RequestServices = services };
        var handler = new AppPageAuthorizationHandler(http);
        async Task<bool> Admits(ClaimsPrincipal caller)
        {
            var context = new Microsoft.AspNetCore.Authorization.AuthorizationHandlerContext([new AppPageRequirement(pageKey)], caller, null);
            await handler.HandleAsync(context);
            return context.HasSucceeded;
        }
        var accepted = memberAccess == PublicAccessStatus.AccessAccepted;
        // A backoffice-only route strips the Member on purpose, so the Member cookie is not the caller there: the page answers for the User alone, whatever Public Access would say about the Member.
        Assert.True(await Admits(backOffice));
        Assert.Equal(accepted, await Admits(new ClaimsPrincipal(member.Identities.Concat(backOffice.Identities))));
        Assert.Equal(accepted, await Admits(member));
        // Another account, or the same identifier from another store, is not the page's person.
        Assert.False(await Admits(Principal(Constants.Security.BackOfficeAuthenticationType, Guid.NewGuid().ToString())));
        Assert.False(await Admits(Principal(Constants.Security.BackOfficeAuthenticationType, "7")));
        Assert.False(await Admits(AppPageAccess.Anonymous));
    }

    [Fact]
    public async Task Administrator_groups_named_in_settings_pass_the_administrator_check_beside_Umbracos_own()
    {
        IReadOnlyUserGroup[] groups = [Group("Site operations", "siteOperations")];
        var user = Stub<IUser>((method, _) => method.Name switch {
            "get_IsApproved" => true, "get_IsLockedOut" => false, "get_Groups" => groups, _ => throw new NotImplementedException(method.Name),
        });
        var users = Stub<IUserService>((method, _) => method.Name == "GetAsync" ? Task.FromResult<IUser?>(user) : throw new NotImplementedException(method.Name));
        var principal = Principal(Constants.Security.BackOfficeAuthenticationType, Guid.NewGuid().ToString());
        // By default only Umbraco's Administrators group counts.
        Assert.False(await new ApplicationAccessPolicy(users).IsBackOfficeAdministratorAsync(principal));
        Assert.False(await new ApplicationAccessPolicy(users, Microsoft.Extensions.Options.Options.Create(new AdministratorOptions())).IsBackOfficeAdministratorAsync(principal));
        Assert.True(await new ApplicationAccessPolicy(users, Microsoft.Extensions.Options.Options.Create(new AdministratorOptions { AdministratorGroups = ["SITEOPERATIONS"] }))
            .IsBackOfficeAdministratorAsync(principal));
        groups = [Group("Administrators", Constants.Security.AdminGroupAlias)];
        Assert.True(await new ApplicationAccessPolicy(users).IsBackOfficeAdministratorAsync(principal));
    }

    [Theory]
    [InlineData(PublicAccessStatus.AccessAccepted, true)]
    [InlineData(PublicAccessStatus.AccessDenied, false)]
    public async Task An_existing_Member_session_is_not_replaced_by_a_backoffice_session(PublicAccessStatus status, bool allowed)
    {
        var member = Principal(IdentityConstants.ApplicationScheme);
        var authentication = Stub<IAuthenticationService>((method, args) => {
            Assert.Equal("AuthenticateAsync", method.Name);
            return Task.FromResult((string)args![1]! == IdentityConstants.ApplicationScheme
                ? AuthenticateResult.Success(new AuthenticationTicket(member, IdentityConstants.ApplicationScheme)) : AuthenticateResult.NoResult());
        });
        using var services = new ServiceCollection().AddSingleton(authentication).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        var checker = Stub<IPublicAccessChecker>((method, args) => {
            Assert.Equal("HasMemberAccessToContentAsync", method.Name);
            Assert.Same(member, args![1]); return Task.FromResult(status);
        });
        var page = Stub<IPublishedContent>((method, _) => method.Name == "get_Id" ? 42 : throw new NotImplementedException(method.Name));
        // Umbraco decides for a Member. A refused Member is never replaced by another account, so a host's admission is not asked.
        var access = new AppPageAccess(null!, checker, new HttpContextAccessor { HttpContext = context },
            new ApplicationAccessPolicy(Stub<IUserService>(Never)), Stub<IPublicAccessService>(Never), [Stub<IProtectedPageAdmission>(Never)]);
        Assert.Equal(allowed, await access.UserAsync(page) is not null);
        Assert.Null(await access.AdmittedAsync(context, page));
    }

    private static IReadOnlyUserGroup Group(string name, string alias) => Stub<IReadOnlyUserGroup>((method, _) => method.Name switch {
        "get_Name" => name, "get_Alias" => alias, _ => throw new NotImplementedException(method.Name),
    });
    private static IExternalLoginWithKeyService Logins() => Stub<IExternalLoginWithKeyService>((method, _) =>
        method.Name == "GetExternalLogins" ? Array.CreateInstance(method.ReturnType.GenericTypeArguments[0], 0) : throw new NotImplementedException(method.Name));
    private static object? Never(MethodInfo method, object?[]? args) => throw new InvalidOperationException("Unexpected service call: " + method.Name);
    private static T Stub<T>(Func<MethodInfo, object?[]?, object?> invoke) where T : class {
        var proxy = DispatchProxy.Create<T, ServiceStub>(); ((ServiceStub)(object)proxy).Call = invoke; return proxy;
    }
    public class ServiceStub : DispatchProxy {
        public Func<MethodInfo, object?[]?, object?> Call { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!, args);
    }
}
