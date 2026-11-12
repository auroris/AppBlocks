using System.Text.Json;
using AppBlocks.Tabular;

namespace AppBlocks.Forms;

/// <param name="Checksum">The record's checksum as the browser loaded it, null under last-save-wins; an update or delete compares it with the current row.</param>
/// <param name="FormData">The form's other named controls by name, apart from <paramref name="Values"/> so validation and strict binding never see them.</param>
/// <param name="Details">The pending changes of the form's tabular blocks, saved with the record in the same request.</param>
public sealed record FormSubmission(string DefinitionVersion, string? Checksum, JsonElement Values, string? SubmissionKey, JsonElement? FormData = null,
    IReadOnlyList<FormDetailSubmission>? Details = null);

/// <summary>
/// A tabular block's pending changes, sent with its form's record so the two save together: in one transaction when the form is on
/// a data source and the block's data source shares its connection, otherwise the record first and then the rows.
/// </summary>
/// <param name="Block">The tabular block's key, inside this form.</param>
/// <param name="Revision">The revision its rows were listed under, so a changed data source is noticed before anything is written.</param>
/// <param name="SubmissionKey">The block's own key for this batch, so a retry does not write it twice; the form's when absent.</param>
/// <param name="Changes">The changes in the shape <c>/api/tabular</c> takes.</param>
public sealed record FormDetailSubmission(Guid Block, string? Revision, string? SubmissionKey, IReadOnlyList<TabularChange>? Changes);
