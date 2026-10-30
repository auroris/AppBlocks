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
        var report = contentTypes.Get(AppBlockSchema.Key("appReport"));
        if (report is null) return;
        var dataType = await dataTypes.GetAsync(Guid.Parse("996a3ce5-b660-4da4-97c7-bb7cdd55d3b5"));
        if (dataType is null) return;
        if (report.PropertyTypes.FirstOrDefault(p => p.Alias == "dataSource") is { } source)
        {
            if (source.DataTypeId == dataType.Id) return;
            source.DataTypeId = dataType.Id;
            source.Name = "Table or SQL query";
        }
        else report.AddPropertyType(new PropertyType(strings, dataType) { Alias = "dataSource", Name = "Table or SQL query", SortOrder = 0 }, "configuration");
        var result = await contentTypes.UpdateAsync(report, Umbraco.Cms.Core.Constants.Security.SuperUserKey);
        if (!result.Success) throw new InvalidOperationException("AppBlocks could not add the report block's data source property.");
    }
}
