using System.Reflection;
using System.Security.Claims;
using AppBlocks.InteractiveReports.Reports;
using AppBlocks.InteractiveReports.Setup;
using InteractiveReport.AspNetCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Services;

namespace AppBlocks.InteractiveReports.Tests;

public sealed class ReportAccessPolicyTests
{
    [Theory]
    [InlineData("admin", true)]
    [InlineData(ReportAdministratorGroup.Alias, true)]
    [InlineData("INTERACTIVEREPORTSADMINISTRATOR", true)]
    [InlineData("editor", false)]
    public async Task Native_administrator_groups_are_always_allowed_and_configured_groups_are_additional(string alias, bool builtIn)
    {
        var group = Stub<IReadOnlyUserGroup>((method, _) => method.Name == "get_Alias" ? alias : throw new NotImplementedException());
        var user = Stub<IUser>((method, _) => method.Name switch {
            "get_IsApproved" => true, "get_IsLockedOut" => false, "get_Groups" => new[] { group },
            _ => throw new NotImplementedException(method.Name),
        });
        var users = Stub<IUserService>((method, _) => method.Name == "GetAsync" ? Task.FromResult<IUser?>(user) : throw new NotImplementedException());
        var options = new AppBlocksOptions();
        var policy = new DefaultReportAccessPolicy(new AppBlocks.Authentication.ApplicationAccessPolicy(users), Options.Create(options), null!, Monitor());
        var id = Guid.NewGuid().ToString();
        var backOfficeUser = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, id)], Constants.Security.BackOfficeAuthenticationType));
        Assert.Equal(builtIn, await policy.IsAdministratorAsync(backOfficeUser));
        options.ReportAdministratorGroups = [alias];
        Assert.True(await policy.IsAdministratorAsync(backOfficeUser));
        var member = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, id), new(ClaimTypes.Role, "admin")], IdentityConstants.ApplicationScheme));
        Assert.False(await policy.IsAdministratorAsync(member));
        var combined = new ClaimsPrincipal(new[] { new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "42")], IdentityConstants.ApplicationScheme) }
            .Concat(backOfficeUser.Identities));
        Assert.True(await policy.IsAdministratorAsync(combined));
        options.ReportAdministratorGroups = [];
        Assert.Equal(builtIn, await policy.IsAdministratorAsync(backOfficeUser));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Disabled_or_locked_out_accounts_do_not_get_report_administration(bool approved, bool lockedOut)
    {
        var user = Stub<IUser>((method, _) => method.Name switch {
            "get_IsApproved" => approved, "get_IsLockedOut" => lockedOut,
            _ => throw new NotImplementedException(method.Name),
        });
        var users = Stub<IUserService>((method, _) => Task.FromResult<IUser?>(user));
        var policy = new DefaultReportAccessPolicy(new AppBlocks.Authentication.ApplicationAccessPolicy(users), Options.Create(new AppBlocksOptions()), null!, Monitor());
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], Constants.Security.BackOfficeAuthenticationType));
        Assert.False(await policy.IsAdministratorAsync(principal));
    }

    [Theory]
    // A report block: what a reader does, and an empty list of saved views.
    [InlineData("block", InteractiveReportAction.ViewReport, true)]
    [InlineData("block", InteractiveReportAction.Query, true)]
    [InlineData("block", InteractiveReportAction.Export, true)]
    [InlineData("block", InteractiveReportAction.ListSavedReports, true)]
    // Saved views have no owner to belong to, and the rest is an administrator's.
    [InlineData("block", InteractiveReportAction.ReadSavedReport, false)]
    [InlineData("block", InteractiveReportAction.CreateSavedReport, false)]
    [InlineData("block", InteractiveReportAction.UpdateSavedReport, false)]
    [InlineData("block", InteractiveReportAction.DeleteSavedReport, false)]
    [InlineData("block", InteractiveReportAction.SelectDefaultReport, false)]
    [InlineData("block", InteractiveReportAction.PublishGlobalReport, false)]
    [InlineData("block", InteractiveReportAction.ListAllSavedReports, false)]
    [InlineData("block", InteractiveReportAction.ManageAdministrators, false)]
    [InlineData("block", InteractiveReportAction.DownloadReportDocument, false)]
    // Anything that is not a block keeps refusing, reserved names and ordinary ones alike.
    [InlineData("__saved-reports", InteractiveReportAction.ViewReport, false)]
    [InlineData("__saved-reports", InteractiveReportAction.Query, false)]
    [InlineData("orders", InteractiveReportAction.ViewReport, false)]
    [InlineData("orders", InteractiveReportAction.Query, false)]
    [InlineData("orders", InteractiveReportAction.Export, false)]
    public async Task An_anonymous_visitor_may_read_a_report_block_and_nothing_else(string report, InteractiveReportAction action, bool allowed)
    {
        var name = report == "block" ? AppBlocks.Blocks.AppBlockContent.ReportName(Guid.NewGuid(), Guid.NewGuid()) : report;
        var policy = new DefaultReportAccessPolicy(null!, Options.Create(new AppBlocksOptions()), null!, Monitor());
        var request = new InteractiveReportAuthorizationRequest {
            User = new ClaimsPrincipal(new ClaimsIdentity()), Action = action,
            Resource = new InteractiveReportAuthorizationResource { ReportName = name }, RequestServices = null!,
        };
        Assert.Equal(allowed, await policy.AuthorizeAsync(request, default));
    }

    private const string MemberId = "42";
    private static readonly string BackOfficeKey = Guid.NewGuid().ToString();

    [Theory]
    // Signed in to both: the view belongs to the Member, whichever order the principal lists the identities in.
    [InlineData("both", "member", true)]
    [InlineData("both", "backoffice", false)]
    [InlineData("both, backoffice first", "member", true)]
    [InlineData("both, backoffice first", "backoffice", false)]
    [InlineData("both", "other member", false)]
    [InlineData("both", "other backoffice", false)]
    // One session only owns what was saved under that identity.
    [InlineData("backoffice", "backoffice", true)]
    [InlineData("backoffice", "member", false)]
    [InlineData("member", "member", true)]
    [InlineData("member", "backoffice", false)]
    public async Task A_saved_view_belongs_to_the_Member_when_one_is_signed_in_and_otherwise_to_the_backoffice_User(string signedIn, string savedBy, bool owner)
    {
        var user = Stub<IUser>((method, _) => method.Name switch {
            "get_IsApproved" => true, "get_IsLockedOut" => false, "get_Groups" => Array.Empty<IReadOnlyUserGroup>(),
            _ => throw new NotImplementedException(method.Name),
        });
        var users = Stub<IUserService>((method, _) => method.Name == "GetAsync" ? Task.FromResult<IUser?>(user) : throw new NotImplementedException());
        // A reserved report name keeps the published catalogue out of this test; it only governs reading the report itself.
        var policy = new DefaultReportAccessPolicy(new AppBlocks.Authentication.ApplicationAccessPolicy(users), Options.Create(new AppBlocksOptions()), null!, Monitor());
        var member = new ClaimsIdentity([new(ClaimTypes.NameIdentifier, MemberId)], IdentityConstants.ApplicationScheme);
        var backOffice = new ClaimsIdentity([new(ClaimTypes.NameIdentifier, BackOfficeKey)], Constants.Security.BackOfficeAuthenticationType);
        var principal = new ClaimsPrincipal(signedIn switch {
            "both" => new[] { member, backOffice }, "both, backoffice first" => new[] { backOffice, member }, "member" => new[] { member }, _ => new[] { backOffice },
        });
        var saved = new InteractiveReport.Core.SavedReports.SavedReportMetadata(7, "__views", null, "Mine", savedBy switch {
            "member" => MemberId, "backoffice" => BackOfficeKey, "other member" => "43", _ => Guid.NewGuid().ToString(),
        }, IsGlobal: false, IsDefault: false, DateTime.UtcNow, InteractiveReport.Core.SavedReports.SavedReportOrigin.User);
        foreach (var action in new[] { InteractiveReportAction.ReadSavedReport, InteractiveReportAction.UpdateSavedReport, InteractiveReportAction.DeleteSavedReport })
        {
            var request = new InteractiveReportAuthorizationRequest {
                User = principal, Action = action, RequestServices = null!,
                Resource = new InteractiveReportAuthorizationResource { ReportName = "__views", SavedReport = saved },
            };
            Assert.True(owner == await policy.AuthorizeAsync(request, default), $"{action} by {signedIn} on a view saved by {savedBy}");
        }
    }

    [Fact]
    public async Task A_saved_view_belongs_to_the_value_of_the_configured_identity_claim()
    {
        var user = Stub<IUser>((method, _) => method.Name switch {
            "get_IsApproved" => true, "get_IsLockedOut" => false, "get_Groups" => Array.Empty<IReadOnlyUserGroup>(),
            _ => throw new NotImplementedException(method.Name),
        });
        var users = Stub<IUserService>((method, _) => method.Name == "GetAsync" ? Task.FromResult<IUser?>(user) : throw new NotImplementedException());
        var policy = new DefaultReportAccessPolicy(new AppBlocks.Authentication.ApplicationAccessPolicy(users), Options.Create(new AppBlocksOptions()), null!,
            Monitor(new InteractiveReportOptions { IdentityClaim = ClaimTypes.Email }));
        var member = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, MemberId), new(ClaimTypes.Email, "reader@example.invalid")],
            IdentityConstants.ApplicationScheme));
        Assert.Equal("reader@example.invalid", DefaultReportAccessPolicy.OwnerKey(member, ClaimTypes.Email));
        foreach (var (savedBy, owner) in new[] { ("reader@example.invalid", true), (MemberId, false) })
        {
            var saved = new InteractiveReport.Core.SavedReports.SavedReportMetadata(7, "__views", null, "Mine", savedBy, IsGlobal: false, IsDefault: false,
                DateTime.UtcNow, InteractiveReport.Core.SavedReports.SavedReportOrigin.User);
            foreach (var action in new[] { InteractiveReportAction.UpdateSavedReport, InteractiveReportAction.DeleteSavedReport })
                Assert.Equal(owner, await policy.AuthorizeAsync(new InteractiveReportAuthorizationRequest {
                    User = member, Action = action, RequestServices = null!,
                    Resource = new InteractiveReportAuthorizationResource { ReportName = "__views", SavedReport = saved },
                }, default));
        }
    }

    private static IOptionsMonitor<InteractiveReportOptions> Monitor(InteractiveReportOptions? options = null) => new FixedMonitor(options ?? new());
    private sealed class FixedMonitor(InteractiveReportOptions value) : IOptionsMonitor<InteractiveReportOptions>
    {
        public InteractiveReportOptions CurrentValue => value;
        public InteractiveReportOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<InteractiveReportOptions, string?> listener) => null;
    }

    private static T Stub<T>(Func<MethodInfo, object?[]?, object?> invoke) where T : class {
        var proxy = DispatchProxy.Create<T, ServiceStub>(); ((ServiceStub)(object)proxy).Call = invoke; return proxy;
    }
    public class ServiceStub : DispatchProxy {
        public Func<MethodInfo, object?[]?, object?> Call { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!, args);
    }
}
