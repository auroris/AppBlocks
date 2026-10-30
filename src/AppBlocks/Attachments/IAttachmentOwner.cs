using UmbracoAppHost.Forms;

namespace UmbracoAppHost.Attachments;

/// <summary>
/// A form resource that decides who may add, replace and remove the files of its records. Whoever may read a
/// record (its <see cref="IFormResource.LoadAsync"/> succeeds) may read its files; without this interface they may
/// write them too. A resource whose rule is narrower implements it: the data source engine, for one, lets only
/// the write groups write.
/// </summary>
public interface IAttachmentOwner
{
    /// <param name="record">The owning record as the resource loaded it for this caller.</param>
    ValueTask<bool> MayWriteAttachmentsAsync(FormRecord record, FormExecutionContext context, CancellationToken ct);
}
