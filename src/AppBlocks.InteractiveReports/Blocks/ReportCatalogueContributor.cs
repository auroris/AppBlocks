using System.Security.Claims;
using AppBlocks.Blocks;
using AppBlocks.InteractiveReports.Reports;

namespace AppBlocks.InteractiveReports;

public sealed class ReportCatalogueContributor(IReportAccessPolicy access) : IAppBlocksCatalogueContributor
{
    public async Task PopulateAsync(IDictionary<string, object> catalogue, ClaimsPrincipal user)
    {
        catalogue["canPreview"] = await access.IsAdministratorAsync(user);
    }
}
