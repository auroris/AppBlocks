using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Umbraco.Cms.Core.Models.Blocks;
using Umbraco.Cms.Core.Models.PublishedContent;
using AppBlocks.Authentication;
using AppBlocks.Blocks;
using AppBlocks.DataSources;
using AppBlocks.Forms;

namespace AppBlocks.Attachments;

/// <summary>
/// One request's standing with an Attachments block: who is calling, whose files the block shows, and what the
/// caller may do with them. <see cref="Status"/> is 200 when everything resolved; otherwise the HTTP status to answer
/// with and <see cref="Problem"/> says why, in words meant for the person.
/// </summary>
/// <param name="CanWrite">The caller may add files, and replace and remove the ones there.</param>
/// <param name="SubmissionKey">The form submission whose pending files the scope holds, for a record not saved yet; null for a record's own files.</param>
/// <param name="CanAdd">The caller may add files: everyone who may write, and whoever an <see cref="IAddOnlyAttachmentOwner"/> lets add.</param>
public sealed record AttachmentScope(int Status, string? Problem = null, AttachmentBlock? Block = null, ClaimsPrincipal? User = null,
    AttachmentOwner? Owner = null, bool CanWrite = false, Guid? SubmissionKey = null, bool CanAdd = false)
{
    public static AttachmentScope Refused(int status, string problem) => new(status, problem);
}

/// <summary>
/// Resolves an Attachments block for a request the way the form endpoints resolve a form: the page's access first,
/// then the block, then the record that owns the files, whose resource must load it for this caller before anything
/// is read, and whose <see cref="IAttachmentOwner"/> rule, if it has one, decides writing. Without that rule an anonymous
/// visitor only reads. Everything the request says about the owner (its key, the page parameters) is input: the
/// resource's own load is what authorizes.
/// </summary>
public sealed class AttachmentAccess(BlockOwnerAccess owners, IOptions<AttachmentSettings> settings)
{
    /// <summary>The revision string the owner's record is loaded under; attachments never compare it with a form's.</summary>
    private const string Definition = "attachments";

    private static readonly BlockOwnerWords Words = new("Attachments", "these files", "files");

    public async Task<AttachmentScope> ResolveAsync(Guid pageKey, Guid blockKey, CancellationToken ct)
    {
        var scope = await owners.ResolveAsync(pageKey, blockKey, AppBlockSchema.AttachmentsAlias, Words, Owner, Definition, ct, allowAnonymous: true);
        if (scope.Status != 200) return AttachmentScope.Refused(scope.Status, scope.Problem!);
        var block = AttachmentBlock.Read(scope.Item!.Content, settings.Value);
        var context = new FormExecutionContext(scope.User!, pageKey, blockKey, Definition, Parameters: scope.Parameters);
        var write = !block.ReadOnly && (scope.Resource is IAttachmentOwner rule
            ? await rule.MayWriteAttachmentsAsync(scope.Record!, context, ct)
            : scope.User!.Identity?.IsAuthenticated == true);
        // A resource may let a caller it does not let write add files all the same.
        var add = write || !block.ReadOnly && scope.Resource is IAddOnlyAttachmentOwner additions
            && await additions.MayAddAttachmentsAsync(scope.Record!, context, ct);
        return new(200, null, block, scope.User, new AttachmentOwner(scope.Resource!.Alias, scope.OwnerKey), write, CanAdd: add);
    }

    /// <summary>
    /// The block inside a form whose record is not saved yet, for one submission of that form: the files the caller uploaded
    /// for it, which the adoption gives to the record once the form saves it. Refused with 404 unless the caller may add files
    /// to the new record (<see cref="AllowsNewUploadsAsync"/>).
    /// </summary>
    public async Task<AttachmentScope> ResolveNewAsync(Guid pageKey, Guid blockKey, Guid submissionKey, CancellationToken ct)
    {
        var scope = await owners.ResolveAsync(pageKey, blockKey, AppBlockSchema.AttachmentsAlias, Words, Owner, Definition, ct, allowAnonymous: true, newRecord: true);
        if (scope.Status != 200) return AttachmentScope.Refused(scope.Status, scope.Problem!);
        var block = AttachmentBlock.Read(scope.Item!.Content, settings.Value);
        if (!await AllowsNewUploadsAsync(block, scope.Form!, scope.User!, pageKey, blockKey, scope.Parameters, ct))
            return AttachmentScope.Refused(404, "Save the record before adding files.");
        return new(200, null, block, scope.User, AttachmentOwner.Pending(scope.Resource!.Alias, submissionKey, scope.User!), true, submissionKey, CanAdd: true);
    }

    /// <summary>
    /// The record a form has just saved, named by <paramref name="key"/> instead of the page's address, as an adoption names it.
    /// Everything else is <see cref="ResolveAsync"/>: the record's own load for this caller decides.
    /// </summary>
    public async Task<AttachmentScope> ResolveSavedAsync(HttpContext http, Guid pageKey, Guid blockKey, string key, CancellationToken ct)
    {
        var form = await owners.ResolveAsync(pageKey, blockKey, AppBlockSchema.AttachmentsAlias, Words, Owner, Definition, ct, allowAnonymous: true, newRecord: true);
        if (form.Status != 200) return AttachmentScope.Refused(form.Status, form.Problem!);
        // The owner resolution reads the record's key from the page's address, so the key from the body takes that place.
        http.Request.Query = new QueryCollection(new Dictionary<string, StringValues>(http.Request.Query, StringComparer.OrdinalIgnoreCase) { [form.Form!.KeyParameter] = key });
        return await ResolveAsync(pageKey, blockKey, ct);
    }

    /// <summary>
    /// Whether <paramref name="user"/> may add files to the record <paramref name="form"/> would create, before it is saved.
    /// The block must belong to the form's record and write; the form must offer this caller a new record, as its class's
    /// Initialize says; and the class's <see cref="IAttachmentOwner"/> rule, given the record the form starts from, must let
    /// them write, or without that rule they must be signed in, as for a saved record. An anonymous visitor therefore adds
    /// files only where the class lets them create a record and write its files, and an adoption then succeeds.
    /// </summary>
    public static async Task<bool> AllowsNewUploadsAsync(AttachmentBlock block, FormDefinition form, ClaimsPrincipal user, Guid pageKey, Guid blockKey,
        IReadOnlyDictionary<string, string>? parameters, CancellationToken ct)
    {
        if (block.ReadOnly || !block.OwnedByForm || form.ViewOnly || FormResourceAccess.Refusal(form.Resource, user) is not null) return false;
        var initialized = await form.Resource.InitializeAsync(new FormExecutionContext(user, pageKey, form.Block.ContentKey, form.Revision, Parameters: parameters), ct);
        if (initialized.Status != 200 || initialized.Record is not { Capabilities.Create: true } record) return false;
        return form.Resource is IAttachmentOwner rule
            ? await rule.MayWriteAttachmentsAsync(record, new FormExecutionContext(user, pageKey, blockKey, Definition, Parameters: parameters), ct)
            : user.Identity?.IsAuthenticated == true;
    }

    /// <summary>The block's owner settings, as the owner resolution reads them.</summary>
    private (string? Resource, string? Key) Owner(IPublishedElement element)
    {
        var block = AttachmentBlock.Read(element, settings.Value);
        return (block.OwnerResource, block.OwnerKey);
    }

    /// <summary>A field's value as a key: the text of a string, number or JSON value; null for nothing.</summary>
    internal static string? Text(object? value) => BlockOwnerAccess.Text(value);

    /// <summary>Who did something, as the audit columns of a data source record it: the username, or the account key when the ticket carries no name.</summary>
    public static string Actor(ClaimsPrincipal user) => BlockOwnerAccess.Actor(user);
}
