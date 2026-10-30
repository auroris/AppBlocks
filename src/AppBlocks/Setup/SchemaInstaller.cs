using System.Xml.Linq;
using Microsoft.Extensions.FileProviders;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;
using uSync.BackOffice;
using uSync.BackOffice.SyncHandlers.Models;

namespace AppBlocks.Setup;

/// <summary>Adds missing package definitions. Existing site definitions are never replaced on startup.</summary>
public sealed class SchemaInstaller(ISyncService sync, IContentTypeService contentTypes, IDataTypeService dataTypes,
    IWebHostEnvironment environment, ILogger<SchemaInstaller> logger, ISyncHandlerFactory handlers, IEnumerable<AppBlocksSchema> schemas,
    IEnumerable<IAppBlocksSchemaMigration> migrations)
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
            var folder = Path.Combine(environment.ContentRootPath, "App_Data", "AppBlocks", "schema", Guid.NewGuid().ToString("N"));
            var copied = 0;
            foreach (var kind in new[] { "DataTypes", "ContentTypes" })
            foreach (var schema in schemas)
            foreach (var file in new ManifestEmbeddedFileProvider(schema.Assembly).GetDirectoryContents("Schema/" + kind).Where(f => !f.IsDirectory))
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
            if (copied == 0) { await MigrateAsync(ct); return; }
            var options = new SyncHandlerOptions();
            var actions = await sync.ImportAsync([folder], false, handlers.GetValidHandlers(options), options, null);
            var failed = actions.Where(a => !a.Success).ToArray();
            if (failed.Length > 0) throw new InvalidOperationException($"AppBlocks could not install {failed.Length} definitions. Review the uSync log.");
            logger.LogInformation("Installed {Count} missing AppBlocks definitions.", copied);
            await MigrateAsync(ct);
        }
        finally { Gate.Release(); }
    }

    private async Task MigrateAsync(CancellationToken ct)
    {
        foreach (var migration in migrations) await migration.EnsureAsync(ct);
    }
}
