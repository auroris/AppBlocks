using InteractiveReport.AspNetCore;
using InteractiveReport.Client.FileDownload;
using InteractiveReport.Client.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using AppBlocks.Connections;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authorization;
using Umbraco.Cms.Core;

namespace AppBlocks.InteractiveReports.Reports;

/// <summary>Registers Interactive Reports with the site's databases and Umbraco-backed authorization.</summary>
public static class InteractiveReportsSite
{
    /// <summary>Route prefix of the JSON client: the REST API, the viewer and administration pages, and the packaged assets.</summary>
    public const string ReportsRoute = "/api/reports";

    /// <summary>Route prefix of the file-download client.</summary>
    public const string DownloadRoute = "/api/download";

    /// <summary>Backoffice report routes always use the backoffice identity, even when a member is also signed in.</summary>
    public const string BackOfficeReportsRoute = "/umbraco/reports";
    public const string BackOfficeDownloadRoute = "/umbraco/report-download";

    /// <summary>The embedded definitions of the reports every site gets.</summary>
    private const string BuiltInReportsResource = "AppBlocks.InteractiveReports.Reports.umbraco-reports.json";

    private static readonly PathString[] BackOfficeReportPaths = [BackOfficeReportsRoute, BackOfficeDownloadRoute];
    private static readonly PathString[] ReportPaths = [ReportsRoute, DownloadRoute, ..BackOfficeReportPaths];

    /// <summary>
    /// Registers the package: Umbraco's own database as <see cref="ConnectionCatalog.UmbracoConnection"/>, opened as AppBlocks
    /// opens it, the built-in audit reports, the JSON and download clients, the authorization bridge, and the virtual-path link fix.
    /// <paramref name="configure"/> customizes the report engine through the same builder.
    /// </summary>
    public static IUmbracoBuilder AddInteractiveReportsSite(
        this IUmbracoBuilder builder,
        IConfiguration configuration,
        Action<InteractiveReportBuilder>? configure = null)
    {
        var services = builder.Services;

        var reports = services.AddInteractiveReports(configuration)
            .AddConnection(ConnectionCatalog.UmbracoConnection, provider => provider.GetRequiredService<ConnectionCatalog>().Openable(ConnectionCatalog.UmbracoConnection))
            .UseAdministrators(async (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await request.RequestServices.GetRequiredService<IReportAccessPolicy>().IsAdministratorAsync(request.User);
            })
            .UseUserProvider<ReportAdministrationUsers>()
            .UseRowRestrictions((request, cancellationToken) =>
                request.RequestServices.GetRequiredService<RecentPagesReport>().RestrictAsync(request, cancellationToken))
            .UseRowRestrictions((request, cancellationToken) =>
                request.RequestServices.GetRequiredService<AppBlocks.InteractiveReports.DataSources.DataSourceReportAccess>().RestrictAsync(request, cancellationToken))
            .UseAuthorization((request, cancellationToken) =>
                request.RequestServices.GetRequiredService<IReportAccessPolicy>().AuthorizeAsync(request, cancellationToken));
        configure?.Invoke(reports);

        services.AddOptions<AppBlocksOptions>().Bind(configuration.GetSection(AppBlocksOptions.Section));
        services.AddBuiltInReports(configuration);
        services.AddInteractiveReportJson();
        services.AddInteractiveReportFileDownload();
        services.AddScoped<RecentPagesReport>();
        services.AddAuthorization(options =>
        {
            options.AddPolicy(ReportAdministratorRequirement.Policy,
                policy => policy.AddRequirements(new ReportAdministratorRequirement()));
            options.AddPolicy(RecentPagesReport.Policy,
                policy => policy.AddRequirements(new BackOfficeContentReportRequirement()));
        });
        services.AddSingleton<IAuthorizationHandler, ReportAdministratorHandler>();
        services.AddSingleton<IAuthorizationHandler, BackOfficeContentReportHandler>();

        // Definitions are written as if the site were at the root; this applies
        // Umbraco:CMS:Hosting:ApplicationVirtualPath to the URLs they emit.
        services.AddSingleton<IPostConfigureOptions<InteractiveReportOptions>, ReportLinksUnderVirtualPath>();
        return builder;
    }

    /// <summary>
    /// The content, member and media reports every site gets, read from the
    /// embedded <c>umbraco-reports.json</c>, which has the same shape as the <c>InteractiveReport</c>
    /// section of appsettings.json. A site's own definition of the same name wins, so a site can
    /// adjust or replace them there.
    /// </summary>
    /// <summary>The built-in audit reports, when <see cref="AppBlocksOptions.IncludeBackOfficeReports"/> asks for them, from configuration or code.</summary>
    private static void AddBuiltInReports(this IServiceCollection services, IConfiguration configuration)
        => services.AddOptions<InteractiveReportOptions>().Configure<IOptions<AppBlocksOptions>>((options, appBlocks) =>
        {
            if (!appBlocks.Value.IncludeBackOfficeReports) return;
            using var stream = typeof(InteractiveReportsSite).Assembly.GetManifestResourceStream(BuiltInReportsResource)
                ?? throw new InvalidOperationException($"Embedded resource {BuiltInReportsResource} is missing.");
            var builtIn = new InteractiveReportOptions();
            new ConfigurationBuilder().AddJsonStream(stream).Build().GetSection("InteractiveReport").Bind(builtIn);
            builtIn.Reports[RecentPagesReport.Name].Sql = RecentPagesReport.PrepareSql(
                builtIn.Reports[RecentPagesReport.Name].Sql, configuration);
            foreach (var (name, definition) in builtIn.Reports)
                options.Reports.TryAdd(name, definition);
        });

    /// <summary>
    /// Authenticates report requests with each Umbraco scheme so the package sees whichever
    /// identity the browser carries. Umbraco's authentication middleware only applies the
    /// default (member) scheme. Add this inside <c>UseUmbraco().WithMiddleware(...)</c>, after
    /// <c>UseBackOffice()</c> and <c>UseWebsite()</c>: middleware appended to the application
    /// after <c>WithEndpoints(...)</c> never runs for matched endpoints.
    /// </summary>
    public static IApplicationBuilder UseInteractiveReportsSite(this IApplicationBuilder app)
    {
        app.UseWhen(
            context => ReportPaths.Any(path => context.Request.Path.StartsWithSegments(path)),
            branch => branch.Use(async (context, next) =>
            {
                var backOffice = BackOfficeReportPaths.Any(path => context.Request.Path.StartsWithSegments(path));
                // Website reports carry both sessions, Member first. Backoffice reports retain their
                // User-only identity so a Member sign-in never changes their saved-view ownership.
                var identities = new List<System.Security.Claims.ClaimsIdentity>();
                if (backOffice) context.User = new System.Security.Claims.ClaimsPrincipal();
                if (!backOffice)
                {
                    var member = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
                    if (member.Succeeded && member.Principal is { Identity.IsAuthenticated: true } principal)
                        identities.AddRange(principal.Identities);
                }
                if (await AppBlocks.Authentication.ApplicationAccessPolicy.BackOfficeTicketAsync(context) is { } ticket)
                    identities.AddRange(ticket.Identities);
                if (identities.Count > 0) context.User = new System.Security.Claims.ClaimsPrincipal(identities);
                await next(context);
            }));
        return app;
    }
}
