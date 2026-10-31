using Microsoft.AspNetCore.Authorization;

namespace AppBlocks.InteractiveReports.Reports;

public sealed class ReportAdministratorRequirement : IAuthorizationRequirement
{
    public const string Policy = "UmbracoReportAdministrators";
}

public sealed class ReportAdministratorHandler(IHttpContextAccessor http)
    : AuthorizationHandler<ReportAdministratorRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ReportAdministratorRequirement requirement)
    {
        if (http.HttpContext is { } request && await request.RequestServices.GetRequiredService<IReportAccessPolicy>().IsAdministratorAsync(context.User)) context.Succeed(requirement);
    }
}
