using AppBlocks.Setup;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using AppBlocks.Blocks;
using AppBlocks.DataSources;

namespace AppBlocks.InteractiveReports;

public sealed class ReportBlockMigration(IContentTypeService contentTypes, IDataTypeService dataTypes, IShortStringHelper strings, SchemaProperties properties) : IAppBlocksSchemaMigration
{
    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        await EnsureDataSourceAsync();
        // Print is offered only when Documents is installed, but the picker is an ordinary content picker that needs nothing of it.
        await properties.AddMissingAsync("appReport", "paperReport");
    }

    private async Task EnsureDataSourceAsync()
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

/// <summary>
/// Lets report definition nodes sit under the Data sources root. The root belongs to AppBlocks, which cannot name a type
/// only this companion installs, so the companion adds its own child type.
/// </summary>
public sealed class ReportDefinitionChildMigration(IContentTypeService contentTypes) : IAppBlocksSchemaMigration
{
    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        if (contentTypes.Get(DataSourceSchema.RootAlias) is not { } root || contentTypes.Get("reportDefinition") is not { } definition) return;
        var allowed = root.AllowedContentTypes?.OrderBy(child => child.SortOrder).ToList() ?? [];
        if (allowed.Any(child => child.Key == definition.Key)) return;
        // Between the SQL data source and the list of values, as the children have always been ordered on sites that had it.
        allowed.Insert(Math.Min(1, allowed.Count), new ContentTypeSort(definition.Key, 1, definition.Alias));
        root.AllowedContentTypes = allowed.Select((child, order) => new ContentTypeSort(child.Key, order, child.Alias)).ToList();
        var result = await contentTypes.UpdateAsync(root, Umbraco.Cms.Core.Constants.Security.SuperUserKey);
        if (!result.Success) throw new InvalidOperationException("AppBlocks could not allow report definitions under the data sources root.");
    }
}
