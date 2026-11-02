using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Migrations;
using AppBlocks.Api;
using AppBlocks.DataSources;

namespace AppBlocks.Setup;

/// <summary>
/// Gives an existing site the API endpoint type, the API endpoints root that may keep endpoints together, and their data types, installed
/// as the package's schema declares them, and lets the package's Data sources root hold endpoints too, after lists of values. Does nothing
/// more on a fresh site, which the installer gives the current definitions, or where the root already allows them; a site's own type that
/// took the root's alias is not the package's to change.
/// </summary>
public sealed class AddApiEndpoints(IMigrationContext context, IServiceScopeFactory scopes, ICoreScopeProvider coreScopes, IContentTypeService contentTypes,
    IOptions<PackageMigrationSettings> settings, IKeyValueService keyValues) : AsyncMigrationBase(context)
{
    protected override async Task MigrateAsync()
    {
        using var migrationScope = coreScopes.CreateCoreScope();
        // The migration records its state even when no schema change is needed; lock before the first read on SQLite.
        migrationScope.EagerWriteLock(Constants.Locks.KeyValues);
        if (settings.Value.RunSchemaAndContentMigrations && keyValues.GetValue(SchemaInstaller.UninstalledKey) != "true")
        {
            using (var scope = scopes.CreateScope()) await scope.ServiceProvider.GetRequiredService<SchemaInstaller>().EnsureAsync(CancellationToken.None);
            var schema = new AppBlocksSchema(typeof(AddApiEndpoints).Assembly);
            Guid Key(string alias) => Guid.Parse(schema.Definitions("ContentTypes").Single(d => d.Attribute("Alias")!.Value == alias).Attribute("Key")!.Value);
            if (contentTypes.Get(Key(DataSourceSchema.RootAlias)) is { } root && contentTypes.Get(Key(ApiEndpointCatalog.Alias)) is { } endpoint
                && root.AllowedContentTypes?.Any(c => c.Key == endpoint.Key) != true)
            {
                var allowed = (root.AllowedContentTypes ?? []).OrderBy(c => c.SortOrder).ToList();
                allowed.Add(new ContentTypeSort(endpoint.Key, allowed.Count, endpoint.Alias));
                root.AllowedContentTypes = allowed;
                var result = await contentTypes.UpdateAsync(root, Constants.Security.SuperUserKey);
                if (!result.Success) throw new InvalidOperationException($"AppBlocks could not allow API endpoints under {root.Alias}: {result.Result}.");
            }
        }
        migrationScope.Complete();
    }
}
