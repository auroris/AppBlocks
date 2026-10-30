using System.Security.Claims;
using UmbracoAppHost.Blocks;
using UmbracoAppHost.Reports;

namespace AppBlocks.InteractiveReports;

public sealed class ReportCatalogueContributor(IReportAccessPolicy access) : IAppBlocksCatalogueContributor
{
    public async Task PopulateAsync(IDictionary<string, object> catalogue, ClaimsPrincipal user)
    {
        catalogue["canPreview"] = await access.IsAdministratorAsync(user);
    }
}
