using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Migrations;
using AppBlocks.Setup;

namespace AppBlocks.InteractiveReports.Setup;

/// <summary>
/// Points an existing site's "Application: Report starting view" data type at the editor's current name, <c>AppBlocks.ReportState</c>,
/// from the earlier <c>AppHost.ReportState</c>, which the package no longer registers. A data type the site gave another editor keeps it.
/// </summary>
public sealed class RenameReportStateEditor(IMigrationContext context, IDataTypeService dataTypes, IOptions<PackageMigrationSettings> settings,
    IKeyValueService keyValues, ICoreScopeProvider scopes) : AsyncMigrationBase(context)
{
    internal const string EarlierAlias = "AppHost.ReportState";

    protected override async Task MigrateAsync()
    {
        using var scope = scopes.CreateCoreScope();
        // The migration records its state even when no change is needed; lock before the first read on SQLite.
        scope.EagerWriteLock(Constants.Locks.KeyValues);
        if (settings.Value.RunSchemaAndContentMigrations && keyValues.GetValue(SchemaInstaller.UninstalledKey) != "true")
        {
            var definition = new AppBlocksSchema(typeof(RenameReportStateEditor).Assembly).Definitions("DataTypes")
                .Single(d => d.Attribute("Alias")!.Value == "Application: Report starting view");
            if (await dataTypes.GetAsync(Guid.Parse(definition.Attribute("Key")!.Value)) is { EditorUiAlias: EarlierAlias } type)
            {
                type.EditorUiAlias = definition.Element("Info")?.Element("EditorUIAlias")?.Value.Trim() ?? "";
                var result = await dataTypes.UpdateAsync(type, Constants.Security.SuperUserKey);
                if (!result.Success) throw new InvalidOperationException($"AppBlocks could not rename the report starting view editor: {result.Status}.");
            }
        }
        scope.Complete();
    }
}
