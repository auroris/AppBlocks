using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Infrastructure.Migrations;
using AppBlocks.InteractiveReports.DataSources;
using AppBlocks.Setup;

namespace AppBlocks.InteractiveReports.Setup;

/// <summary>
/// Gives an existing site's report definition type its Access choice and Access groups picker on an Access tab, and the
/// "Application: Report access" data type when the site lacks it, as the companion's schema declares them. Nodes keep no value,
/// which means Signed-in accounts, so every report admits whom it admitted before. Does nothing when the package's type is not there.
/// </summary>
public sealed class AddReportDefinitionAccess(IMigrationContext context, IContentTypeService contentTypes, IDataTypeService dataTypes,
    IDataTypeContainerService dataFolders, PropertyEditorCollection editors, IConfigurationEditorJsonSerializer serializer, IShortStringHelper strings,
    IOptions<PackageMigrationSettings> settings, IKeyValueService keyValues, ICoreScopeProvider scopes)
    : AsyncMigrationBase(context)
{
    internal static readonly string[] Properties = ["access", "accessGroups"];

    protected override async Task MigrateAsync()
    {
        using var scope = scopes.CreateCoreScope();
        // The migration records its state even when no schema change is needed; lock before the first read on SQLite.
        scope.EagerWriteLock(Constants.Locks.KeyValues);
        if (settings.Value.RunSchemaAndContentMigrations && keyValues.GetValue(SchemaInstaller.UninstalledKey) != "true")
        {
            var definition = new AppBlocksSchema(typeof(AddReportDefinitionAccess).Assembly).Definitions("ContentTypes")
                .Single(d => d.Attribute("Alias")!.Value == PublishedReportCatalog.ReportAlias);
            // The package's type, by key: a site's own type that took the alias is not the package's to change.
            if (contentTypes.Get(Guid.Parse(definition.Attribute("Key")!.Value)) is { } type)
                await new CompanionSchemaProperties(contentTypes, dataTypes, dataFolders, editors, serializer, strings).AddMissingAsync(type, definition, Properties);
        }
        scope.Complete();
    }
}
