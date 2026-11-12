using Umbraco.Cms.Infrastructure.BackgroundJobs;

namespace AppBlocks.Attachments;

/// <summary>Deletes expired pending uploads even when nobody visits an Attachments block.</summary>
public sealed class AttachmentCleanupJob(IServiceScopeFactory scopes) : RecurringBackgroundJobBase(TimeSpan.FromHours(1))
{
    public override async Task RunJobAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AttachmentStore>().ExpirePendingAsync(cancellationToken);
    }
}
