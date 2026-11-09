using System.Xml.Linq;
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

/// <summary>
/// Gives an existing site's SQL data source type its date-time mode (<c>dateTimeMode</c>) and its list of values type its inline SQL's access
/// (<c>access</c>), each with the data type it needs when the site lacks it, as the package's schema declares them, and brings the data source
/// descriptions those settings changed up to date. Both properties are optional and empty means today's behaviour: server local time, and
/// Public. A type that is not there, as on a fresh site that the installer gives the current definitions, is left alone, as is a property the
/// type already has.
/// </summary>
public sealed class AddDateTimeModeAndListAccess(IMigrationContext context, IContentTypeService contentTypes, IDataTypeService dataTypes,
    IDataTypeContainerService dataFolders, PropertyEditorCollection editors, IConfigurationEditorJsonSerializer serializer, IShortStringHelper strings,
    IOptions<PackageMigrationSettings> settings, IKeyValueService keyValues, ICoreScopeProvider scopes)
    : AsyncMigrationBase(context)
{
    internal const string DateTimeProperty = "dateTimeMode";
    internal const string AccessProperty = "access";
    /// <summary>The data source properties whose descriptions name the new settings.</summary>
    private static readonly string[] Described = [DateTimeProperty, "auditColumns", "insertDefaults", "keyColumns", "customStatements"];

    protected override async Task MigrateAsync()
    {
        using var scope = scopes.CreateCoreScope();
        // The migration records its state even when no schema change is needed; lock before the first read on SQLite.
        scope.EagerWriteLock(Constants.Locks.KeyValues);
        if (settings.Value.RunSchemaAndContentMigrations && keyValues.GetValue(SchemaInstaller.UninstalledKey) != "true")
        {
            var schema = new AppBlocksSchema(typeof(AddDateTimeModeAndListAccess).Assembly);
            await AddAsync(schema, DataSourceSchema.SqlAlias, DateTimeProperty, Described);
            await AddAsync(schema, PublishedDataCatalog.ListAlias, AccessProperty, []);
        }
        scope.Complete();
    }

    /// <summary>Adds <paramref name="alias"/> to the package's type when it lacks it, and copies the package's descriptions of <paramref name="described"/>.</summary>
    private async Task AddAsync(AppBlocksSchema schema, string typeAlias, string alias, string[] described)
    {
        var definition = schema.Definitions("ContentTypes").Single(d => d.Attribute("Alias")!.Value == typeAlias);
        // The package's type, by key: a site's own type that took the alias is not the package's to change.
        if (contentTypes.Get(Guid.Parse(definition.Attribute("Key")!.Value)) is not { } type) return;
        var properties = definition.Element("GenericProperties")!.Elements("GenericProperty").ToArray();
        var changed = false;
        if (!type.PropertyTypeExists(alias))
        {
            var property = properties.Single(p => SchemaProperties.Value(p, "Alias") == alias);
            var dataTypeKey = Guid.Parse(SchemaProperties.Value(property, "Definition"));
            if (await dataTypes.GetAsync(dataTypeKey) is null)
                await SchemaInstaller.CreateDataTypeAsync(schema.Definitions("DataTypes").Single(d => Guid.Parse(d.Attribute("Key")!.Value) == dataTypeKey),
                    dataTypes, dataFolders, editors, serializer);
            var tab = (string?)property.Element("Tab")?.Attribute("Alias");
            if (!string.IsNullOrEmpty(tab) && type.PropertyGroups.All(group => group.Alias != tab))
                type.PropertyGroups.Add(SchemaProperties.Group(definition.Element("Tabs")!.Elements("Tab").Single(t => SchemaProperties.Value(t, "Alias") == tab)));
            SchemaProperties.Add(type, await new SchemaProperties(dataTypes, strings).PropertyAsync(type.Alias, property), tab);
            changed = true;
        }
        foreach (var name in described)
            if (type.PropertyTypes.FirstOrDefault(p => p.Alias == name) is { } existing && Description(properties, name) is { } text && existing.Description != text)
            {
                existing.Description = text;
                changed = true;
            }
        if (!changed) return;
        var result = await contentTypes.UpdateAsync(type, Constants.Security.SuperUserKey);
        if (!result.Success) throw new InvalidOperationException($"AppBlocks could not add the {alias} property to {type.Alias}: {result.Result}.");
    }

    private static string? Description(IEnumerable<XElement> properties, string alias)
        => properties.FirstOrDefault(p => SchemaProperties.Value(p, "Alias") == alias) is { } property ? SchemaProperties.Value(property, "Description") : null;
}
