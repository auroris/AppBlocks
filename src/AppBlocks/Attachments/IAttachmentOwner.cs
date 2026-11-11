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

/// <summary>
/// A resource that lets some callers add files they may not then replace or remove: the record's filed documents stay as
/// filed, and someone asked for a missing one can still send it. It is asked only for a saved record, and only about a
/// caller its write rule refuses; whoever may write may add. A block set to read only takes no files from anyone.
/// </summary>
public interface IAddOnlyAttachmentOwner
{
    /// <param name="record">The owning record as the resource loaded it for this caller.</param>
    ValueTask<bool> MayAddAttachmentsAsync(FormRecord record, FormExecutionContext context, CancellationToken ct);
}

/// <summary>
/// A resource that adopts pending files in its create transaction. The normal post-save adoption request
/// only confirms that submission; it never transfers more files. Implementations must verify the caller,
/// record and submission key against persisted state, independently of later file-write permission.
/// </summary>
public interface ISubmittedAttachmentOwner
{
    ValueTask<bool> ConfirmSubmissionAsync(string key, Guid submissionKey, FormExecutionContext context, CancellationToken ct);
}
