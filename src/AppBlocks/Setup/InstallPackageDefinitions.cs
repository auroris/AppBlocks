using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Infrastructure.Migrations;

namespace AppBlocks.Setup;

/// <summary>Installs missing definitions inside Umbraco's tracked package migration lifecycle.</summary>
public sealed class InstallPackageDefinitions(IMigrationContext context, IServiceScopeFactory scopes, ICoreScopeProvider coreScopes) : AsyncMigrationBase(context)
{
    protected override async Task MigrateAsync()
    {
        using var migrationScope = coreScopes.CreateCoreScope();
        // Even an already-installed schema writes migration state on completion. Acquire the native lock before reading,
        // so SQLite does not have to upgrade a read transaction after concurrent startup activity has written to the database.
        migrationScope.EagerWriteLock(Constants.Locks.KeyValues);
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SchemaInstaller>().EnsureAsync(CancellationToken.None);
        migrationScope.Complete();
    }
}
