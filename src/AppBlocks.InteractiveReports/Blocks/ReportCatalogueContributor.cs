using System.Security.Claims;
using UmbracoAppHost.Blocks;
using UmbracoAppHost.DataSources;
using UmbracoAppHost.Reports;

namespace AppBlocks.InteractiveReports;

public sealed class ReportCatalogueContributor(PublishedReportCatalog definitions, IReportAccessPolicy access) : IAppBlocksCatalogueContributor
{
    public async Task PopulateAsync(IDictionary<string, object> catalogue, ClaimsPrincipal user)
    {
        catalogue["reports"] = definitions.Reports.Select(r => new { alias = r.Key, label = r.Value.Title ?? r.Key }).OrderBy(r => r.label);
        catalogue["canPreview"] = await access.IsAdministratorAsync(user);
    }
}
