using Umbraco.Cms.Core.Services;

namespace AppBlocks.Setup;

/// <summary>
/// Brings the form and data source types of a site installed before AppBlocks' schema carried everything the engine
/// reads. A site whose types came from the older uSync export has the properties and needs only the connection picker;
/// a site installed from the older schema lacks the properties.
/// </summary>
public sealed class DataSourceSchemaMigration(IContentTypeService contentTypes, IDataTypeService dataTypes, SchemaProperties properties) : IAppBlocksSchemaMigration
{
    /// <summary>"Application: Text", which the connection used before it had a picker. Any other editor is the site's choice.</summary>
    private static readonly Guid PlainText = Guid.Parse("90cb2487-adcb-2ec5-c4a0-840175ba5424");
    private static readonly Guid ConnectionPicker = Guid.Parse("0bb63f2b-95ed-4a5a-81d4-5a7ddcd52751");

    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        await properties.AddMissingAsync("appForm", "paperReport");
        await properties.AddMissingAsync("listOfValues", "connection", "sql");
        await properties.AddMissingAsync("sqlDataSource", "mapping", "rowRestriction", "insertDefaults", "auditColumns", "keySequences", "customStatements");

        var definition = properties.Definition("sqlDataSource");
        var source = contentTypes.Get(Guid.Parse(definition.Attribute("Key")!.Value));
        if (source?.PropertyTypes.FirstOrDefault(p => p.Alias == "connection") is not { } connection || connection.DataTypeKey != PlainText) return;
        if (await dataTypes.GetAsync(ConnectionPicker) is not { } picker || await dataTypes.GetAsync(PlainText) is not { } plain) return;
        // A value is read from the column its data type names, so the data sources keep their connections only if both
        // name the same one. An earlier installer gave the picker the long text column; while nothing uses it that is
        // safe to correct, and if something does this site keeps its plain text box.
        if (picker.DatabaseType != plain.DatabaseType)
        {
            if (contentTypes.GetAll().Any(type => type.PropertyTypes.Any(p => p.DataTypeKey == picker.Key))) return;
            picker.DatabaseType = plain.DatabaseType;
            var corrected = await dataTypes.UpdateAsync(picker, Umbraco.Cms.Core.Constants.Security.SuperUserKey);
            if (!corrected.Success) throw new InvalidOperationException($"AppBlocks could not update the connection picker: {corrected.Status}.");
        }
        connection.DataTypeId = picker.Id;
        connection.DataTypeKey = picker.Key;
        connection.Mandatory = false;
        connection.Description = SchemaProperties.Value(definition.Element("GenericProperties")!.Elements("GenericProperty").Single(p => SchemaProperties.Value(p, "Alias") == "connection"), "Description");
        source.Description = SchemaProperties.Value(definition.Element("Info")!, "Description");
        var result = await contentTypes.UpdateAsync(source, Umbraco.Cms.Core.Constants.Security.SuperUserKey);
        if (!result.Success) throw new InvalidOperationException("AppBlocks could not update the data source connection picker.");
    }
}
