using System.Xml.Linq;
using Microsoft.Extensions.FileProviders;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Strings;
using UmbracoAppHost.Blocks;
using uSync.BackOffice;
using uSync.BackOffice.SyncHandlers.Models;

namespace AppBlocks.Setup;

/// <summary>Adds missing package definitions. Existing site definitions are never replaced on startup.</summary>
public sealed class SchemaInstaller(ISyncService sync, IContentTypeService contentTypes, IDataTypeService dataTypes,
    IWebHostEnvironment environment, ILogger<SchemaInstaller> logger, ISyncHandlerFactory handlers, IShortStringHelper strings)
    : INotificationAsyncHandler<UmbracoApplicationStartedNotification>
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public async Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken cancellationToken)
        => await EnsureAsync(cancellationToken);

    public async Task EnsureAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var files = new ManifestEmbeddedFileProvider(typeof(SchemaInstaller).Assembly);
            var folder = Path.Combine(environment.ContentRootPath, "App_Data", "AppBlocks", "schema", Guid.NewGuid().ToString("N"));
            var copied = 0;
            foreach (var kind in new[] { "DataTypes", "ContentTypes" })
            foreach (var file in files.GetDirectoryContents("Schema/" + kind).Where(f => !f.IsDirectory))
            {
                using var stream = file.CreateReadStream();
                var xml = XElement.Load(stream);
                var key = Guid.Parse(xml.Attribute("Key")!.Value);
                var alias = xml.Attribute("Alias")!.Value;
                var exists = kind switch
                {
                    "ContentTypes" => contentTypes.Get(key) is not null || contentTypes.Get(alias) is not null,
                    _ => await dataTypes.GetAsync(key) is not null,
                };
                if (exists) continue;
                var target = Path.Combine(folder, kind, file.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                xml.Save(target);
                copied++;
            }
            if (copied == 0) { await UpgradeReportBlock(); return; }
            var options = new SyncHandlerOptions();
            var actions = await sync.ImportAsync([folder], false, handlers.GetValidHandlers(options), options, null);
            var failed = actions.Where(a => !a.Success).ToArray();
            if (failed.Length > 0) throw new InvalidOperationException($"AppBlocks could not install {failed.Length} definitions. Review the uSync log.");
            logger.LogInformation("Installed {Count} missing AppBlocks definitions.", copied);
            await UpgradeReportBlock();
        }
        finally { Gate.Release(); }
    }

    private async Task UpgradeReportBlock()
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
