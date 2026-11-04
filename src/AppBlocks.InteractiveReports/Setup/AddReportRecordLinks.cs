using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Infrastructure.Migrations;
using AppBlocks.InteractiveReports.Blocks;
using AppBlocks.Setup;

namespace AppBlocks.InteractiveReports.Setup;

/// <summary>
/// Gives an existing site's Interactive Report block its Record links setting: the <c>recordLinks</c> property on the Configuration
/// tab, and the "Application: Report record links" data type it needs when the site lacks it, both as the package's schema declares
/// them. Does nothing when the package's <c>appReport</c> type is not there, as on a fresh site, which the installer gives the current
/// definitions, or when the type already has the property. Blocks keep no value for it, which means Open and create, as before.
/// </summary>
public sealed class AddReportRecordLinks(IMigrationContext context, IContentTypeService contentTypes, IDataTypeService dataTypes,
    IDataTypeContainerService dataFolders, PropertyEditorCollection editors, IConfigurationEditorJsonSerializer serializer, IShortStringHelper strings,
    IOptions<PackageMigrationSettings> settings, IKeyValueService keyValues, ICoreScopeProvider scopes)
    : AsyncMigrationBase(context)
{
    protected override async Task MigrateAsync()
    {
        using var scope = scopes.CreateCoreScope();
        // The migration records its state even when no schema change is needed; lock before the first read on SQLite.
        scope.EagerWriteLock(Constants.Locks.KeyValues);
        await UpdateAsync();
        scope.Complete();
    }

    private async Task UpdateAsync()
    {
        if (!settings.Value.RunSchemaAndContentMigrations || keyValues.GetValue(SchemaInstaller.UninstalledKey) == "true") return;
        var schema = new AppBlocksSchema(typeof(AddReportRecordLinks).Assembly);
        var definition = schema.Definitions("ContentTypes").Single(d => d.Attribute("Alias")!.Value == "appReport");
        // The package's type, by key: a site's own type that took the alias is not the package's to change.
        if (contentTypes.Get(Guid.Parse(definition.Attribute("Key")!.Value)) is not { } type || type.PropertyTypeExists(ReportRecordLinkChoices.Property)) return;
        var property = definition.Element("GenericProperties")!.Elements("GenericProperty").Single(p => SchemaProperties.Value(p, "Alias") == ReportRecordLinkChoices.Property);
        var dataTypeKey = Guid.Parse(SchemaProperties.Value(property, "Definition"));
        if (await dataTypes.GetAsync(dataTypeKey) is null)
            await SchemaInstaller.CreateDataTypeAsync(schema.Definitions("DataTypes").Single(d => Guid.Parse(d.Attribute("Key")!.Value) == dataTypeKey),
                dataTypes, dataFolders, editors, serializer);
        var tab = (string?)property.Element("Tab")?.Attribute("Alias");
        if (!string.IsNullOrEmpty(tab) && type.PropertyGroups.All(group => group.Alias != tab))
            type.PropertyGroups.Add(SchemaProperties.Group(definition.Element("Tabs")!.Elements("Tab").Single(t => SchemaProperties.Value(t, "Alias") == tab)));
        SchemaProperties.Add(type, await new SchemaProperties(dataTypes, strings).PropertyAsync(type.Alias, property), tab);
        var result = await contentTypes.UpdateAsync(type, Constants.Security.SuperUserKey);
        if (!result.Success) throw new InvalidOperationException($"AppBlocks could not add the record links setting to {type.Alias}: {result.Result}.");
    }
}
