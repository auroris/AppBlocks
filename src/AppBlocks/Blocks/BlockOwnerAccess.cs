using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Umbraco.Cms.Core.Models.Blocks;
using Umbraco.Cms.Core.Models.PublishedContent;
using AppBlocks.Authentication;
using AppBlocks.DataSources;
using AppBlocks.Forms;

namespace AppBlocks.Blocks;

/// <summary>
/// One request's standing with a block that follows a record, such as Attachments or a tabular form: who is calling,
/// the block, the form it sits in, and the record that owns what the block shows, as its own resource loaded it.
/// <see cref="Status"/> is 200 when everything resolved; otherwise the HTTP status to answer with and
/// <see cref="Problem"/> says why, in words meant for the person.
/// </summary>
/// <param name="Resource">The owner's resource: the enclosing form's, or the one the block names.</param>
/// <param name="Record">The owner's record as its resource loaded it for this caller; its key is the authoritative one.</param>
/// <param name="Key">The owner's key as the request or the block named it.</param>
public sealed record BlockOwnerScope(int Status, string? Problem = null, ClaimsPrincipal? User = null, BlockGridItem? Item = null, FormDefinition? Form = null,
    IFormResource? Resource = null, FormRecord? Record = null, string? Key = null, IReadOnlyDictionary<string, string>? Parameters = null)
{
    public static BlockOwnerScope Refused(int status, string problem) => new(status, problem);

    /// <summary>The owner's key: the loaded record's, or the one the request named when the resource returns none.</summary>
    public string OwnerKey => Record?.Key ?? Key!;
}

/// <summary>The words a block's refusals use: the block's name, "these files" or "these rows", and "files" or "rows".</summary>
public sealed record BlockOwnerWords(string BlockName, string These, string Things);

/// <summary>
/// Resolves a block that follows a record the way the form endpoints resolve a form: the page's access first, then
/// the block, then the record that owns what the block shows, whose resource must load it for this caller before
/// anything is read. Everything the request says about the owner (its key, the page parameters) is input: the
/// resource's own load is what authorizes.
/// </summary>
public sealed class BlockOwnerAccess(AppBlockContent content, AppPageAccess access, FormResourceRegistry resources, PublishedDataCatalog catalog, IHttpContextAccessor http)
{
    /// <param name="blockAlias">The element type the block must be.</param>
    /// <param name="owner">Reads the block's owner settings: the resource alias and the key expression, both empty for the enclosing form's record.</param>
    /// <param name="definition">The revision string the owner's record is loaded under; such blocks never compare it with a form's.</param>
    /// <param name="allowAnonymous">
    /// Whether an anonymous visitor to an open page reaches the owner's resource, which then decides as it does for anyone.
    /// When false, visitors without an account are refused here.
    /// </param>
    /// <param name="newRecord">
    /// The enclosing form's record is one not saved yet: nothing is loaded, and the scope has no record and no key. The caller decides
    /// what that allows. A block that names an owner of its own is refused.
    /// </param>
    /// <param name="savedOwnerKey">An explicit key for the enclosing form's just-saved record. It overrides only the execution parameters, never the HTTP request.</param>
    public async Task<BlockOwnerScope> ResolveAsync(Guid pageKey, Guid blockKey, string blockAlias, BlockOwnerWords words,
        Func<IPublishedElement, (string? Resource, string? Key)> owner, string definition, CancellationToken ct, bool allowAnonymous = false, bool newRecord = false, string? savedOwnerKey = null)
    {
        if (http.HttpContext is not { } request) return BlockOwnerScope.Refused(403, "This request has no context.");
        if (await content.PageAsync(pageKey) is not { } page) return BlockOwnerScope.Refused(403, $"You do not have access to {words.These}.");
        var user = allowAnonymous ? await access.VisitorAsync(page) : await access.UserAsync(page);
        if (user is null) return BlockOwnerScope.Refused(403, $"You do not have access to {words.These}.");
        // Antiforgery tokens and the audit log name the actual Umbraco Member or backoffice User, or nobody.
        request.User = user;
        var culture = AppBlockContent.RequestCulture(request);
        var blocks = AppBlockContent.Blocks(page, culture);
        var flat = AppBlockContent.Flatten(blocks).ToArray();
        if (flat.FirstOrDefault(b => b.ContentKey == blockKey && b.Content.ContentType.Alias == blockAlias) is not { } item)
            return BlockOwnerScope.Refused(404, $"This page has no such {words.BlockName} block.");
        var (ownerResource, ownerKey) = owner(item.Content);
        var parameters = FormRequestValues.Parameters(request.Request.Query);
        // The form the block sits in, if any: its record is the owner by default and supplies :FIELD values.
        var formBlock = flat.FirstOrDefault(b => b.Content.ContentType.Alias == "appForm" && AppBlockContent.Flatten(b.Areas.SelectMany(a => a)).Any(c => c.ContentKey == blockKey));
        var form = formBlock is null ? null : await FormDefinition.ResolveAsync(page, formBlock.ContentKey, resources, catalog, culture, request.RequestServices.GetServices<IAppBlockPlugin>());

        IFormResource resource; string? key;
        if (string.IsNullOrWhiteSpace(ownerResource))
        {
            if (form is null) return BlockOwnerScope.Refused(422, $"This {words.BlockName} block is not inside a form and names no owner.");
            resource = form.Resource;
            if (FormResourceAccess.Refusal(resource, user) is { } refused) return BlockOwnerScope.Refused(refused, $"Sign in as a member to use {words.These}.");
            if (newRecord) return new(200, null, user, item, form, resource, null, null, parameters);
            key = savedOwnerKey ?? Single(request.Request.Query[form.KeyParameter]);
            if (savedOwnerKey is not null)
                parameters = FormRequestValues.Parameters(new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(
                    request.Request.Query, StringComparer.OrdinalIgnoreCase) { [form.KeyParameter] = savedOwnerKey }));
            if (key is null) return BlockOwnerScope.Refused(404, $"Save the record before adding {words.Things}.");
        }
        else
        {
            if (savedOwnerKey is not null) return BlockOwnerScope.Refused(422, $"This {words.BlockName} block names its own owner.");
            if (newRecord) return BlockOwnerScope.Refused(422, $"This {words.BlockName} block names its own owner, so it has no {words.Things} before that record exists.");
            if (await resources.FindAsync(ownerResource.Trim()) is not { } named) return BlockOwnerScope.Refused(422, $"The owner resource {ownerResource} is not registered.");
            resource = named;
            if (FormResourceAccess.Refusal(resource, user) is { } refused) return BlockOwnerScope.Refused(refused, $"Sign in as a member to use {words.These}.");
            key = await OwnerKeyAsync(ownerKey, form, parameters, user, pageKey, request, ct);
            if (key is null) return BlockOwnerScope.Refused(404, $"The record {words.These} belong to is not known yet.");
        }
        var context = new FormExecutionContext(user, pageKey, blockKey, definition, Parameters: parameters);
        var loaded = await resource.LoadAsync(key, context, ct);
        if (loaded.Record is null) return BlockOwnerScope.Refused(loaded.Status is 403 or 404 ? loaded.Status : 403,
            loaded.Status == 404 ? $"The record {words.These} belong to was not found." : $"You do not have access to {words.These}.");
        return new(200, null, user, item, form, resource, loaded.Record, key, parameters);
    }

    /// <summary>
    /// The owner's key as the block names it: <c>:PARAM.name</c> reads the page's address, <c>:FIELD.alias</c> a
    /// field of the enclosing form's record, which the form's resource loads for this caller first; anything else is
    /// the key itself. Null when the value is not there yet, such as a form record that has not been saved.
    /// </summary>
    private static async Task<string?> OwnerKeyAsync(string? expression, FormDefinition? form, IReadOnlyDictionary<string, string> parameters,
        ClaimsPrincipal user, Guid pageKey, HttpContext request, CancellationToken ct)
    {
        var text = expression?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (text.StartsWith(":PARAM.", StringComparison.OrdinalIgnoreCase))
            return parameters.FirstOrDefault(p => p.Key.Equals(text[7..], StringComparison.OrdinalIgnoreCase)).Value is { Length: > 0 } value ? value : null;
        if (text.StartsWith(":FIELD.", StringComparison.OrdinalIgnoreCase))
        {
            if (form is null || Single(request.Request.Query[form.KeyParameter]) is not { } recordKey || FormResourceAccess.Refusal(form.Resource, user) is not null) return null;
            var record = await form.Resource.LoadAsync(recordKey, new FormExecutionContext(user, pageKey, form.Block.ContentKey, form.Revision, Parameters: parameters), ct);
            // Publishing accepts the alias in any letter case, as editors' names are matched everywhere else, so the record is read the same way.
            if (record.Record is null) return null;
            var alias = text[7..];
            if (record.Record.Values.TryGetValue(alias, out var exact)) return Text(exact);
            return record.Record.Values.FirstOrDefault(value => value.Key.Equals(alias, StringComparison.OrdinalIgnoreCase)) is { Key: not null } field ? Text(field.Value) : null;
        }
        return text;
    }

    /// <summary>A field's value as a key: the text of a string, number or JSON value; null for nothing.</summary>
    public static string? Text(object? value) => value switch {
        null => null,
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        JsonElement element => element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    } is { Length: > 0 } text ? text : null;

    private static string? Single(Microsoft.Extensions.Primitives.StringValues values)
        => values.Count == 1 && !string.IsNullOrWhiteSpace(values[0]) ? values[0] : null;

    /// <summary>What the audit columns and logs say for someone with no account.</summary>
    public const string AnonymousActor = "anonymous";

    /// <summary>Who did something, as the audit columns of a data source record it: the username, the account key when the ticket carries no name, or <see cref="AnonymousActor"/> for nobody.</summary>
    public static string Actor(ClaimsPrincipal user) => user.Identity?.IsAuthenticated != true ? AnonymousActor
        : user.Identity.Name is { Length: > 0 } name ? name : ApplicationAccessPolicy.UserKey(user);
}
