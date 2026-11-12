using Umbraco.Cms.Infrastructure.BackgroundJobs;

namespace AppBlocks.Attachments;

/// <summary>
/// Deletes expired pending uploads even when nobody visits an Attachments block. It first looks at the upload tables as the
/// Database setup screen does, which creates nothing, so a site where no upload has created them, or whose upload database
/// cannot be reached, is left exactly as it is: nothing can be pending there. A store that fails has already logged why, once
/// per problem, and the next hour tries again.
/// </summary>
public sealed class AttachmentCleanupJob(IServiceScopeFactory scopes) : RecurringBackgroundJobBase(TimeSpan.FromHours(1))
{
    public override async Task RunJobAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var status = await scope.ServiceProvider.GetRequiredService<AttachmentDatabaseSetup>().StatusAsync(cancellationToken);
        if (status.Tables.Any(table => table.State == "missing")) return;
        try { await scope.ServiceProvider.GetRequiredService<AttachmentStore>().ExpirePendingAsync(cancellationToken); }
        catch (AttachmentStoreException) { }
    }
}
