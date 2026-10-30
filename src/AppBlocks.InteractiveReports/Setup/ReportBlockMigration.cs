using AppBlocks.Setup;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using UmbracoAppHost.Blocks;

namespace AppBlocks.InteractiveReports;

public sealed class ReportBlockMigration(IContentTypeService contentTypes, IDataTypeService dataTypes, IShortStringHelper strings) : IAppBlocksSchemaMigration
{
    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        // Preserve legacy report-name bindings and customizations; add the new block-owned source option.
        var report = contentTypes.Get(AppBlockSchema.Key("appReport"));
        if (report is null || report.PropertyTypes.Any(p => p.Alias == "dataSource")) return;
        var dataType = await dataTypes.GetAsync(Guid.Parse("32f959d2-f116-57de-20b7-e54c95db31cc"));
        if (dataType is null) return;
        report.AddPropertyType(new PropertyType(strings, dataType) { Alias = "dataSource", Name = "Data source", SortOrder = 0 }, "configuration");
        if (report.PropertyTypes.FirstOrDefault(p => p.Alias == "reportName") is { } legacy) legacy.Mandatory = false;
        var result = await contentTypes.UpdateAsync(report, Umbraco.Cms.Core.Constants.Security.SuperUserKey);
        if (!result.Success) throw new InvalidOperationException("AppBlocks could not add the report block's data source property.");
    }
}
