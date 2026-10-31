using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Options;
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
public sealed record AttachmentScope(int Status, string? Problem = null, AttachmentBlock? Block = null, ClaimsPrincipal? User = null,
    AttachmentOwner? Owner = null, bool CanWrite = false)
{
    public static AttachmentScope Refused(int status, string problem) => new(status, problem);
}

/// <summary>
/// Resolves an Attachments block for a request the way the form endpoints resolve a form: the page's access first,
/// then the block, then the record that owns the files, whose resource must load it for this caller before anything
/// is read, and whose <see cref="IAttachmentOwner"/> rule, if it has one, decides writing. Everything the request
/// says about the owner (its key, the page parameters) is input: the resource's own load is what authorizes.
/// </summary>
public sealed class AttachmentAccess(BlockOwnerAccess owners, IOptions<AttachmentSettings> settings)
{
    /// <summary>The revision string the owner's record is loaded under; attachments never compare it with a form's.</summary>
    private const string Definition = "attachments";

    private static readonly BlockOwnerWords Words = new("Attachments", "these files", "files");

    public async Task<AttachmentScope> ResolveAsync(Guid pageKey, Guid blockKey, CancellationToken ct)
    {
        var scope = await owners.ResolveAsync(pageKey, blockKey, AppBlockSchema.AttachmentsAlias, Words, element => {
            var settingsOf = AttachmentBlock.Read(element, settings.Value);
            return (settingsOf.OwnerResource, settingsOf.OwnerKey);
        }, Definition, ct);
        if (scope.Status != 200) return AttachmentScope.Refused(scope.Status, scope.Problem!);
        var block = AttachmentBlock.Read(scope.Item!.Content, settings.Value);
        var context = new FormExecutionContext(scope.User!, pageKey, blockKey, Definition, Parameters: scope.Parameters);
        var write = !block.ReadOnly && (scope.Resource is not IAttachmentOwner rule || await rule.MayWriteAttachmentsAsync(scope.Record!, context, ct));
        return new(200, null, block, scope.User, new AttachmentOwner(scope.Resource!.Alias, scope.OwnerKey), write);
    }

    /// <summary>A field's value as a key: the text of a string, number or JSON value; null for nothing.</summary>
    internal static string? Text(object? value) => BlockOwnerAccess.Text(value);

    /// <summary>Who did something, as the audit columns of a data source record it: the username, or the account key when the ticket carries no name.</summary>
    public static string Actor(ClaimsPrincipal user) => BlockOwnerAccess.Actor(user);
}
