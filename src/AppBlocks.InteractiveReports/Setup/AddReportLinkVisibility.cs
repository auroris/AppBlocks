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
/// Gives an existing site's Interactive Report block its Create button and Open link settings, each with its backoffice and member
/// group pickers, on the Configuration tab, and the data types they need when the site lacks them, all as the package's schemas
/// declare them. Does nothing when the package's <c>appReport</c> type is not there, as on a fresh site, which the installer gives the
/// current definitions, or for a property the type already has. Blocks keep no value for them, which means Form page decides.
/// </summary>
public sealed class AddReportLinkVisibility(IMigrationContext context, IContentTypeService contentTypes, IDataTypeService dataTypes,
    IDataTypeContainerService dataFolders, PropertyEditorCollection editors, IConfigurationEditorJsonSerializer serializer, IShortStringHelper strings,
    IOptions<PackageMigrationSettings> settings, IKeyValueService keyValues, ICoreScopeProvider scopes)
    : AsyncMigrationBase(context)
{
    internal static readonly string[] Properties = [.. new[] { ReportLinkRule.Create, ReportLinkRule.Open }
        .SelectMany(link => new[] { link + "Visibility", link + "UserGroups", link + "MemberGroups" })];

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
        var schema = new AppBlocksSchema(typeof(AddReportLinkVisibility).Assembly);
        var definition = schema.Definitions("ContentTypes").Single(d => d.Attribute("Alias")!.Value == "appReport");
        // The package's type, by key: a site's own type that took the alias is not the package's to change.
        if (contentTypes.Get(Guid.Parse(definition.Attribute("Key")!.Value)) is not { } type) return;
        var missing = Properties.Where(alias => !type.PropertyTypeExists(alias)).ToArray();
        if (missing.Length == 0) return;
        // The member group pickers use the base package's data type.
        var dataTypeDefinitions = schema.Definitions("DataTypes").Concat(new AppBlocksSchema(typeof(SchemaInstaller).Assembly).Definitions("DataTypes")).ToArray();
        foreach (var alias in missing)
        {
            var property = definition.Element("GenericProperties")!.Elements("GenericProperty").Single(p => SchemaProperties.Value(p, "Alias") == alias);
            var dataTypeKey = Guid.Parse(SchemaProperties.Value(property, "Definition"));
            if (await dataTypes.GetAsync(dataTypeKey) is null)
                await SchemaInstaller.CreateDataTypeAsync(dataTypeDefinitions.First(d => Guid.Parse(d.Attribute("Key")!.Value) == dataTypeKey),
                    dataTypes, dataFolders, editors, serializer);
            var tab = (string?)property.Element("Tab")?.Attribute("Alias");
            if (!string.IsNullOrEmpty(tab) && type.PropertyGroups.All(group => group.Alias != tab))
                type.PropertyGroups.Add(SchemaProperties.Group(definition.Element("Tabs")!.Elements("Tab").Single(t => SchemaProperties.Value(t, "Alias") == tab)));
            SchemaProperties.Add(type, await new SchemaProperties(dataTypes, strings).PropertyAsync(type.Alias, property), tab);
        }
        var result = await contentTypes.UpdateAsync(type, Constants.Security.SuperUserKey);
        if (!result.Success) throw new InvalidOperationException($"AppBlocks could not add the report link settings to {type.Alias}: {result.Result}.");
    }
}
