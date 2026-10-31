using System.Reflection;
using System.Security.Claims;
using AppBlocks.InteractiveReports.Reports;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Services;

namespace AppBlocks.InteractiveReports.Tests;

public sealed class ReportAccessPolicyTests
{
    [Fact]
    public async Task Backoffice_administrator_gets_report_privileges_only_when_the_application_grants_them()
    {
        var group = Stub<IReadOnlyUserGroup>((method, _) => method.Name == "get_Alias" ? "admin" : throw new NotImplementedException());
        var user = Stub<IUser>((method, _) => method.Name switch {
            "get_IsApproved" => true, "get_IsLockedOut" => false, "get_Groups" => new[] { group },
            _ => throw new NotImplementedException(method.Name),
        });
        var users = Stub<IUserService>((method, _) => method.Name == "GetAsync" ? Task.FromResult<IUser?>(user) : throw new NotImplementedException());
        var options = new AppBlocksOptions();
        var policy = new DefaultReportAccessPolicy(users, Options.Create(options), null!);
        var id = Guid.NewGuid().ToString();
        var backOfficeUser = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, id)], Constants.Security.BackOfficeAuthenticationType));
        Assert.False(await policy.IsAdministratorAsync(backOfficeUser));
        options.ReportAdministratorGroups = ["admin"];
        Assert.True(await policy.IsAdministratorAsync(backOfficeUser));
        var member = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, id), new(ClaimTypes.Role, "admin")], IdentityConstants.ApplicationScheme));
        Assert.False(await policy.IsAdministratorAsync(member));
        options.ReportAdministratorGroups = [];
        Assert.False(await policy.IsAdministratorAsync(backOfficeUser));
    }

    private static T Stub<T>(Func<MethodInfo, object?[]?, object?> invoke) where T : class {
        var proxy = DispatchProxy.Create<T, ServiceStub>(); ((ServiceStub)(object)proxy).Call = invoke; return proxy;
    }
    public class ServiceStub : DispatchProxy {
        public Func<MethodInfo, object?[]?, object?> Call { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!, args);
    }
}
