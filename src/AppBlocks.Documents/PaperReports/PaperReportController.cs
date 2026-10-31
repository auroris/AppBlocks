using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Web.Common.Controllers;

namespace AppBlocks.Documents.PaperReports;

/// <summary>A paperReport's normal Umbraco URL opens its download, whose route also rechecks Public Access.</summary>
public sealed class PaperReportController(ILogger<PaperReportController> logger, ICompositeViewEngine views,
    IUmbracoContextAccessor context) : RenderController(logger, views, context)
{
    public override IActionResult Index() => CurrentPage is { } page
        ? Redirect(Request.PathBase + "/api/paper-reports/" + page.Key + Request.QueryString) : NotFound();
}
