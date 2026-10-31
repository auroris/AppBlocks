using System.Reflection;
using System.Security.Claims;
using AppBlocks.Authentication;
using AppBlocks.Blocks;
using AppBlocks.DataSources;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;

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
        var resolver = new UmbracoDataSourceUsers(Stub<IMemberService>(Never), users, Logins());
        var principal = Principal(Constants.Security.BackOfficeAuthenticationType, key.ToString());
        var account = (await resolver.ResolveAsync(principal, default))!;
        Assert.True(account.IsBackOfficeUser); Assert.False(account.IsMember);
        Assert.True(account.IsInUserGroup("EDITOR")); Assert.False(account.IsInUserGroup("admin"));
        Assert.False(account.IsInMemberGroup("Editorial team"));
        Assert.Equal(["Editorial team"], account.Groups); Assert.Equal("backoffice:" + key, account.Key);
        groups = [Group("Site administrators", "admin")];
        account = (await resolver.ResolveAsync(principal, default))!;
        Assert.True(account.IsInUserGroup("admin")); Assert.False(account.IsInUserGroup("editor"));
        locked = true; Assert.Null(await resolver.ResolveAsync(principal, default));
        locked = false; approved = false; Assert.Null(await resolver.ResolveAsync(principal, default));
    }

    [Fact]
    public async Task Member_groups_do_not_become_backoffice_user_groups()
    {
        var locked = false;
        var member = Stub<IMember>((method, _) => method.Name switch {
            "get_Key" => Guid.Empty, "get_IsApproved" => true, "get_IsLockedOut" => locked,
            "get_Username" => "member", "get_Email" => "member@example.test",
            _ => throw new NotImplementedException(method.Name),
        });
        var members = Stub<IMemberService>((method, _) => method.Name switch {
            "GetById" => member, "GetAllRoles" => new[] { "Administrators", "Order editors" },
            _ => throw new NotImplementedException(method.Name),
        });
        var resolver = new UmbracoDataSourceUsers(members, Stub<IUserService>(Never), Logins());
        var account = (await resolver.ResolveAsync(Principal(IdentityConstants.ApplicationScheme), default))!;
        Assert.True(account.IsMember); Assert.False(account.IsBackOfficeUser);
        Assert.True(account.IsInMemberGroup("order EDITORS"));
        Assert.False(account.IsInUserGroup("admin")); Assert.False(account.IsInUserGroup("Administrators"));
        Assert.Equal("42", account.Key); Assert.Empty(account.UserGroupAliases);
        locked = true; Assert.Null(await resolver.ResolveAsync(Principal(IdentityConstants.ApplicationScheme), default));
        Assert.Null(await resolver.ResolveAsync(new ClaimsPrincipal(), default));
        Assert.Null(await resolver.ResolveAsync(Principal("unrecognised"), default));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public async Task Backoffice_accounts_are_available_on_public_pages_but_do_not_bypass_Member_Public_Access(bool admin, bool protectedPage, bool allowed)
    {
        var key = Guid.NewGuid();
        var principal = Principal(Constants.Security.BackOfficeAuthenticationType, key.ToString());
        var user = Stub<IUser>((method, _) => method.Name switch {
            "get_IsApproved" => true, "get_IsLockedOut" => false,
            "get_Groups" => new[] { Group(admin ? "Administrators" : "Editors", admin ? "admin" : "editor") },
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
        var page = Stub<IPublishedContent>((method, _) => method.Name == "get_Path" ? "-1,42" : throw new NotImplementedException(method.Name));
        var access = new AppPageAccess(null!, Stub<IPublicAccessChecker>(Never), new HttpContextAccessor { HttpContext = context }, new ApplicationAccessPolicy(users), protection);
        Assert.Equal(allowed, await access.UserAsync(page) is not null);
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
