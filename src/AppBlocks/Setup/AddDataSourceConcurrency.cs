using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Infrastructure.Migrations;
using AppBlocks.DataSources;

namespace AppBlocks.Setup;

/// <summary>Installs the two concurrency choices, replacing legacy column-based protection with checksums.</summary>
public sealed class AddDataSourceConcurrency(IMigrationContext context, IContentTypeService contentTypes, IDataTypeService dataTypes,
    IDataTypeContainerService dataFolders, PropertyEditorCollection editors, IConfigurationEditorJsonSerializer serializer, IShortStringHelper strings,
    IOptions<PackageMigrationSettings> settings, IKeyValueService keyValues, ICoreScopeProvider scopes) : AsyncMigrationBase(context)
{
    protected override async Task MigrateAsync()
    {
        using var scope = scopes.CreateCoreScope();
        scope.EagerWriteLock(Constants.Locks.KeyValues);
        if (settings.Value.RunSchemaAndContentMigrations && keyValues.GetValue(SchemaInstaller.UninstalledKey) != "true")
        {
            var schema = new AppBlocksSchema(typeof(AddDataSourceConcurrency).Assembly);
            var definition = schema.Definitions("ContentTypes").Single(d => d.Attribute("Alias")!.Value == DataSourceSchema.SqlAlias);
            if (contentTypes.Get(Guid.Parse(definition.Attribute("Key")!.Value)) is { } type)
            {
                var properties = definition.Element("GenericProperties")!.Elements("GenericProperty").ToArray();
                var mode = properties.Single(p => SchemaProperties.Value(p, "Alias") == "concurrencyMode");
                var dataTypeKey = Guid.Parse(SchemaProperties.Value(mode, "Definition"));
                if (await dataTypes.GetAsync(dataTypeKey) is null)
                    await SchemaInstaller.CreateDataTypeAsync(schema.Definitions("DataTypes").Single(d => Guid.Parse(d.Attribute("Key")!.Value) == dataTypeKey),
                        dataTypes, dataFolders, editors, serializer);
                else
                {
                    var picker = (await dataTypes.GetAsync(dataTypeKey))!;
                    picker.ConfigurationData = new Dictionary<string, object> { ["items"] = Enum.GetNames<DataSourceConcurrency>() };
                    var updated = await dataTypes.UpdateAsync(picker, Constants.Security.SuperUserKey);
                    if (!updated.Success) throw new InvalidOperationException($"AppBlocks could not update the concurrency choices: {updated.Status}.");
                }
                if (!type.PropertyTypeExists("concurrencyMode"))
                    SchemaProperties.Add(type, await new SchemaProperties(dataTypes, strings).PropertyAsync(type.Alias, mode), "writes");
                type.PropertyTypes.Single(p => p.Alias == "concurrencyMode").Description = SchemaProperties.Value(mode, "Description");
                var result = await contentTypes.UpdateAsync(type, Constants.Security.SuperUserKey);
                if (!result.Success) throw new InvalidOperationException($"AppBlocks could not add the concurrency choice: {result.Result}.");

                // Update every stored version, including published values, without publishing unrelated drafts.
                var modeId = type.PropertyTypes.Single(p => p.Alias == "concurrencyMode").Id;
                var modes = StoredValues(modeId).ToDictionary(p => p.VersionId);
                foreach (var value in modes.Values.Where(p => p.VarcharValue == "VersionColumn"))
                {
                    value.VarcharValue = nameof(DataSourceConcurrency.Checksum);
                    Database.Execute("UPDATE umbracoPropertyData SET varcharValue = @0 WHERE id = @1", value.VarcharValue, value.Id);
                }
                if (type.PropertyTypes.FirstOrDefault(p => p.Alias == "concurrency") is { } column)
                {
                    foreach (var old in StoredValues(column.Id).Where(p => !string.IsNullOrWhiteSpace(p.VarcharValue)))
                    {
                        if (modes.TryGetValue(old.VersionId, out var selected))
                        {
                            if (!string.IsNullOrWhiteSpace(selected.VarcharValue)) continue;
                            selected.VarcharValue = nameof(DataSourceConcurrency.Checksum);
                            Database.Execute("UPDATE umbracoPropertyData SET varcharValue = @0 WHERE id = @1", selected.VarcharValue, selected.Id);
                        }
                        else
                            Database.Execute("INSERT INTO umbracoPropertyData (versionId, propertyTypeId, varcharValue) VALUES (@0, @1, @2)",
                                old.VersionId, modeId, nameof(DataSourceConcurrency.Checksum));
                    }
                    type.RemovePropertyType("concurrency");
                    result = await contentTypes.UpdateAsync(type, Constants.Security.SuperUserKey);
                    if (!result.Success) throw new InvalidOperationException($"AppBlocks could not remove the version-column setting: {result.Result}.");
                }
                RebuildCache = true;
            }
        }
        scope.Complete();
    }

    private List<StoredConcurrency> StoredValues(int property)
        => Database.Fetch<StoredConcurrency>("SELECT id, versionId, varcharValue FROM umbracoPropertyData WHERE propertyTypeId = @0", property);

    private sealed class StoredConcurrency
    {
        public int Id { get; set; }
        public int VersionId { get; set; }
        public string? VarcharValue { get; set; }
    }
}
