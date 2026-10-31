using AppBlocks.Authentication;
using AppBlocks.Databases;
using AppBlocks.Publishing;

namespace AppBlocks.InteractiveReports.Reports;

/// <summary>
/// Whether to tell the person looking at a report that saving views is off and where to fix it, and only a backoffice
/// administrator is told. The report block's page leaves this to the browser, because an answer that depends on who is
/// asking does not belong in a page a site's cache might keep and serve to the next visitor.
/// </summary>
public static class SavedViewsSetupNote
{
    /// <summary>Under the engine's reserved names, so it can never hide a report: <c>__</c> is not an alias anyone can give one.</summary>
    public const string Route = InteractiveReportsSite.ReportsRoute + "/__saved-views-setup";

    internal static void MapSavedViewsSetupNote(this IEndpointRouteBuilder endpoints) => endpoints.MapGet(Route, async (HttpContext http, CancellationToken ct) => {
        http.KeepOutOfCaches();
        var savedViews = http.RequestServices.GetService<SavedViewStore>();
        return savedViews is null || await savedViews.ReadyAsync(ct)
            || await http.RequestServices.GetRequiredService<ApplicationAccessPolicy>().BackOfficeAdministratorAsync(http) is null
            ? Results.NoContent() : Results.Json(new { setupUrl = http.Request.PathBase + DatabaseSetupGuide.ScreenPath });
    });
}
