using AppBlocks.Forms;

namespace AppBlocks.Attachments;

/// <summary>
/// A form resource that decides who may add, replace and remove the files of its records. Whoever may read a
/// record (its <see cref="IFormResource.LoadAsync"/> succeeds) may read its files; without this interface someone
/// signed in may write them too, and an anonymous visitor may not. A resource whose rule is different implements it:
/// the data source engine, for one, lets only the write groups write, and a public class can let anonymous visitors in.
/// </summary>
public interface IAttachmentOwner
{
    /// <param name="record">The owning record as the resource loaded it for this caller.</param>
    ValueTask<bool> MayWriteAttachmentsAsync(FormRecord record, FormExecutionContext context, CancellationToken ct);
}
