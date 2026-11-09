using System.Globalization;
using System.Net.Mime;
using System.Security.Claims;
using System.Text.Json;
using AppBlocks.Authentication;
using AppBlocks.Databases;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace AppBlocks.Attachments;

/// <summary>The body of an adoption: the key of the record the form saved.</summary>
public sealed record AttachmentAdoption(string? Key);

/// <summary>
/// <c>/api/attachments/{page}/{block}</c>, or the same under the configured <see cref="AppBlocksRoutes.Root"/>: the files of
/// the record an Attachments block shows. Every call resolves the block through <see cref="AttachmentAccess"/> first, so page
/// access and the owning record's own load decide, and a file id is only ever looked up among that owner's links. Writes
/// carry the antiforgery token the list hands out, are refused on a read-only block, and are logged like form operations.
/// <c>/new/{submissionKey}</c> holds the files someone uploads in a form whose record is not saved yet, under the key the
/// runtime generates for that submission: list, upload, download and remove, then <c>/adopt</c> once the form has saved,
/// which gives them to the record.
/// </summary>
public static class AttachmentEndpoints
{
    /// <summary>Where the endpoints answer: <c>attachments</c> under <see cref="AppBlocksRoutes.Root"/>, by default <c>/api/attachments</c>.</summary>
    public static string Route(IConfiguration? configuration) => AppBlocksRoutes.Under(configuration, "attachments");

    /// <summary>Room for the multipart framing and the text fields around a file of the allowed size.</summary>
    private const long MultipartOverhead = 64 * 1024;

    public static void MapAttachments(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(Route(endpoints.ServiceProvider.GetService<IConfiguration>()) + "/{pageKey:guid}/{blockKey:guid}");
        group.MapGet("", (Guid pageKey, Guid blockKey, HttpContext http, AttachmentAccess access, AttachmentStore store, CancellationToken ct)
            => List(access.ResolveAsync(pageKey, blockKey, ct), http, store, ct));
        group.MapPost("", (Guid pageKey, Guid blockKey, HttpContext http, AttachmentAccess access, AttachmentStore store, CancellationToken ct)
            => Write(access.ResolveAsync(pageKey, blockKey, ct), null, http, store, ct));
        group.MapGet("/{id:long}", (Guid pageKey, Guid blockKey, long id, HttpContext http, AttachmentAccess access, AttachmentStore store, CancellationToken ct)
            => Download(access.ResolveAsync(pageKey, blockKey, ct), id, http, store, ct));
        group.MapPut("/{id:long}", (Guid pageKey, Guid blockKey, long id, HttpContext http, AttachmentAccess access, AttachmentStore store, CancellationToken ct)
            => Write(access.ResolveAsync(pageKey, blockKey, ct), id, http, store, ct));
        group.MapDelete("/{id:long}", (Guid pageKey, Guid blockKey, long id, HttpContext http, AttachmentAccess access, AttachmentStore store, CancellationToken ct)
            => Remove(access.ResolveAsync(pageKey, blockKey, ct), id, http, store, ct));

        var pending = group.MapGroup("/new/{submissionKey:guid}");
        pending.MapGet("", (Guid pageKey, Guid blockKey, Guid submissionKey, HttpContext http, AttachmentAccess access, AttachmentStore store, CancellationToken ct)
            => List(Pending(access.ResolveNewAsync(pageKey, blockKey, submissionKey, ct), store, ct), http, store, ct));
        pending.MapPost("", (Guid pageKey, Guid blockKey, Guid submissionKey, HttpContext http, AttachmentAccess access, AttachmentStore store, CancellationToken ct)
            => Write(Pending(access.ResolveNewAsync(pageKey, blockKey, submissionKey, ct), store, ct), null, http, store, ct));
        pending.MapGet("/{id:long}", (Guid pageKey, Guid blockKey, Guid submissionKey, long id, HttpContext http, AttachmentAccess access, AttachmentStore store, CancellationToken ct)
            => Download(Pending(access.ResolveNewAsync(pageKey, blockKey, submissionKey, ct), store, ct), id, http, store, ct));
        pending.MapDelete("/{id:long}", (Guid pageKey, Guid blockKey, Guid submissionKey, long id, HttpContext http, AttachmentAccess access, AttachmentStore store, CancellationToken ct)
            => Remove(Pending(access.ResolveNewAsync(pageKey, blockKey, submissionKey, ct), store, ct), id, http, store, ct));
        pending.MapPost("/adopt", Adopt);
    }

    private static async Task<IResult> List(Task<AttachmentScope> resolving, HttpContext http, AttachmentStore store, CancellationToken ct)
    {
        var scope = await resolving;
        if (scope.Status != 200) return Refusal(scope);
        http.Response.Headers.CacheControl = "no-store";
        IReadOnlyList<AttachmentItem> items;
        try { items = await store.ListAsync(scope.Owner!, ct); }
        catch (AttachmentStoreException error)
        {
            // Not a failure of this page: the block says so in words for the person, and the form around it never hears of it.
            return Results.Json(new {
                owner = Owner(scope),
                canWrite = false, purposes = scope.Block!.Purposes, maxFileSize = scope.Block.Limits.MaxFileSize, accept = scope.Block.Limits.Accept,
                items = Array.Empty<object>(),
                storage = new { message = Plain(error), setupUrl = await SetupUrlAsync(http) },
            });
        }
        return Results.Json(new {
            owner = Owner(scope),
            canWrite = scope.CanWrite,
            purposes = scope.Block!.Purposes,
            maxFileSize = scope.Block.Limits.MaxFileSize,
            accept = scope.Block.Limits.Accept,
            items = items.Select(item => Item(item, scope.User!)),
            antiforgeryToken = http.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(http).RequestToken,
        });
    }

    /// <summary>The owner the list names: the record's resource and key, or for pending files the resource and no key.</summary>
    private static object Owner(AttachmentScope scope) => new { alias = scope.Owner!.ResourceAlias, key = scope.SubmissionKey is null ? scope.Owner.Key : null };

    private static async Task<IResult> Download(Task<AttachmentScope> resolving, long id, HttpContext http, AttachmentStore store, CancellationToken ct)
    {
        var scope = await resolving;
        if (scope.Status != 200) return Refusal(scope);
        var file = await Run(() => store.ReadAsync(scope.Owner!, id, ct));
        if (file.Problem is not null) return file.Problem;
        if (file.Value is null) return Results.Problem("This file does not belong to the record.", statusCode: 404);
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        // The stored name and type come from the uploader; the disposition keeps the browser from showing the file in place.
        http.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = file.Value.FileName, FileName = SafeAscii(file.Value.FileName) }.ToString();
        return Results.Bytes(file.Value.Bytes, file.Value.ContentType);
    }

    /// <summary>
    /// An upload (no id) or a replacement: a multipart body with a <c>file</c> part and the text parts <c>purpose</c>,
    /// <c>label</c> and <c>notes</c>. The size limit is applied before the body is read: a declared length over it is
    /// refused at once, and the request body is capped so an undeclared one is cut off at the limit.
    /// </summary>
    private static async Task<IResult> Write(Task<AttachmentScope> resolving, long? id, HttpContext http, AttachmentStore store, CancellationToken ct)
    {
        var scope = await resolving;
        if (scope.Status != 200) return Refusal(scope);
        if (!scope.CanWrite) return Audited(http, id is null ? "upload" : "replace", scope, id, Results.Problem("You may read these files but not change them.", statusCode: 403), 403);
        try { await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http); }
        catch (AntiforgeryValidationException) { return Results.BadRequest(); }
        var limits = scope.Block!.Limits;
        if (!http.Request.HasFormContentType) return Results.Problem("Send the file as multipart form data.", statusCode: 415);
        var cap = limits.MaxFileSize + MultipartOverhead;
        if (http.Request.ContentLength is > 0 and var declared && declared > cap) return Results.Problem(TooLarge(limits), statusCode: 413);
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } body) body.MaxRequestBodySize = cap;
        IFormCollection form;
        try { form = await http.Request.ReadFormAsync(new FormOptions { MultipartBodyLengthLimit = cap, ValueLengthLimit = 4000 }, ct); }
        catch (Exception error) when (error is InvalidDataException or BadHttpRequestException or IOException) { return Results.Problem(TooLarge(limits), statusCode: 413); }
        var part = form.Files.GetFile("file");
        AttachmentUpload? upload = null;
        if (part is not null)
        {
            if (part.Length > limits.MaxFileSize) return Results.Problem(TooLarge(limits), statusCode: 413);
            var fileName = Path.GetFileName(part.FileName ?? "").Trim();
            if (fileName.Length is 0 or > 260) return Results.Problem("The file needs a name of at most 260 characters.", statusCode: 422);
            if (!limits.Accepts(fileName)) return Results.Problem(limits.Accept is { } accepted
                ? $"Files of this type are not accepted here. Accepted: {accepted}."
                : "The site's and this block's allowed file types do not overlap. No file types are accepted here.", statusCode: 415);
            var contentType = string.IsNullOrWhiteSpace(part.ContentType) || part.ContentType.Length > 200 ? MediaTypeNames.Application.Octet : part.ContentType.Trim();
            await using var stream = part.OpenReadStream();
            using var buffer = new MemoryStream((int)Math.Min(part.Length, int.MaxValue));
            await stream.CopyToAsync(buffer, ct);
            if (buffer.Length > limits.MaxFileSize) return Results.Problem(TooLarge(limits), statusCode: 413);
            upload = new(fileName, contentType, buffer.ToArray());
        }
        else if (id is null) return Results.Problem("Choose a file to upload.", statusCode: 422);
        string? Field(string name) => form.TryGetValue(name, out var values) && values.Count == 1 ? values[0] : null;
        var purpose = Field("purpose"); var label = Field("label"); var notes = Field("notes");
        if (purpose is { Length: > 0 } chosen && scope.Block.Purposes.Count > 0 && !scope.Block.Purposes.Contains(chosen, StringComparer.Ordinal))
            return Results.Problem("Choose one of the purposes the block offers.", statusCode: 422);
        if (purpose?.Length > 200 || label?.Length > 200) return Results.Problem("Use at most 200 characters for the purpose and the label.", statusCode: 422);
        if (notes?.Length > 1000) return Results.Problem("Use at most 1000 characters for the notes.", statusCode: 422);
        var by = AttachmentAccess.Actor(scope.User!);
        http.Response.Headers.CacheControl = "no-store";
        var result = await Run(async () => id is null
            ? await store.AddAsync(scope.Owner!, upload!, purpose, label, notes, by, ct)
            : await store.ReplaceAsync(scope.Owner!, id.Value, upload, purpose, label, notes, by, ct));
        if (result.Problem is not null) return result.Problem;
        if (result.Value is null) return Audited(http, "replace", scope, id, Results.Problem("This file does not belong to the record.", statusCode: 404), 404);
        return Audited(http, id is null ? "upload" : "replace", scope, result.Value.Id, Results.Json(Item(result.Value, scope.User!), statusCode: id is null ? 201 : 200), id is null ? 201 : 200);
    }

    private static async Task<IResult> Remove(Task<AttachmentScope> resolving, long id, HttpContext http, AttachmentStore store, CancellationToken ct)
    {
        var scope = await resolving;
        if (scope.Status != 200) return Refusal(scope);
        if (!scope.CanWrite) return Audited(http, "remove", scope, id, Results.Problem("You may read these files but not change them.", statusCode: 403), 403);
        try { await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http); }
        catch (AntiforgeryValidationException) { return Results.BadRequest(); }
        var removed = await Run(() => store.RemoveAsync(scope.Owner!, id, AttachmentAccess.Actor(scope.User!), ct));
        if (removed.Problem is not null) return removed.Problem;
        http.Response.Headers.CacheControl = "no-store";
        return removed.Value ? Audited(http, "remove", scope, id, Results.NoContent(), 204)
            : Audited(http, "remove", scope, id, Results.Problem("This file does not belong to the record.", statusCode: 404), 404);
    }

    /// <summary>
    /// Gives the caller's pending files of one submission to the record its form has just saved, then answers with that
    /// record's list. The caller must be able to write the record's files, as for an upload to it; the files they reach are
    /// only their own, since the pending owner carries their account. When none are theirs but someone else has files pending
    /// under the same submission key, the adoption is refused rather than answered as if there were nothing to adopt.
    /// </summary>
    private static async Task<IResult> Adopt(Guid pageKey, Guid blockKey, Guid submissionKey, HttpContext http, AttachmentAccess access, AttachmentStore store, CancellationToken ct)
    {
        // ReadFromJsonAsync throws for any other content type, which would otherwise be a 500.
        if (!http.Request.HasJsonContentType()) return Results.Problem("Send the record's key as JSON.", statusCode: 415);
        AttachmentAdoption? adoption;
        try { adoption = await http.Request.ReadFromJsonAsync<AttachmentAdoption>(ct); }
        catch (Exception error) when (error is JsonException or BadHttpRequestException) { return Results.BadRequest(); }
        if (adoption?.Key?.Trim() is not { Length: > 0 and <= 200 } key) return Results.Problem("Name the record the files now belong to.", statusCode: 422);
        var scope = await Pending(access.ResolveSavedAsync(http, pageKey, blockKey, key, ct), store, ct);
        if (scope.Status != 200) return Refusal(scope);
        if (!scope.CanWrite) return Audited(http, "adopt", scope, null, Results.Problem("You may read these files but not change them.", statusCode: 403), 403);
        try { await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http); }
        catch (AntiforgeryValidationException) { return Results.BadRequest(); }
        var pending = AttachmentOwner.Pending(scope.Owner!.Alias, submissionKey, scope.User!);
        var adopted = await Run(() => store.AdoptAsync(pending, scope.Owner, AttachmentAccess.Actor(scope.User!), ct));
        if (adopted.Problem is not null) return adopted.Problem;
        if (adopted.Value == 0)
        {
            var others = await Run(() => store.PendingForOthersAsync(pending, submissionKey, ct));
            if (others.Problem is not null) return others.Problem;
            if (others.Value) return Audited(http, "adopt", scope, null, Results.Problem("These files were uploaded by someone else.", statusCode: 403), 403);
        }
        Audited(http, "adopt", scope, null, Results.Empty, 200);
        return await List(Task.FromResult(scope), http, store, ct);
    }

    /// <summary>
    /// A pending scope, after the pending files past <see cref="AttachmentSettings.PendingLifetimeHours"/> are deleted: every
    /// pending operation does that first, so no background service is needed.
    /// </summary>
    private static async Task<AttachmentScope> Pending(Task<AttachmentScope> resolving, AttachmentStore store, CancellationToken ct)
    {
        var scope = await resolving;
        if (scope.Status == 200)
            try { await store.ExpirePendingAsync(ct); }
            catch (AttachmentStoreException) { } // The operation itself then meets the store's problem and reports it.
        return scope;
    }

    /// <summary>What someone with no account is told in place of an account name: that it was someone signed in, not who.</summary>
    internal const string HiddenAccount = "a signed-in user";

    /// <summary>What the list and the writes return for a file: its current revision and the earlier ones, newest first. Account names are for people signed in themselves.</summary>
    private static object Item(AttachmentItem item, ClaimsPrincipal reader)
    {
        string By(string actor) => reader.Identity?.IsAuthenticated == true || actor == AppBlocks.Blocks.BlockOwnerAccess.AnonymousActor ? actor : HiddenAccount;
        return new {
            id = item.Id, fileName = item.FileName, contentType = item.ContentType, size = item.Size, sha256 = item.Sha256,
            purpose = item.Purpose, label = item.Label, notes = item.Notes,
            uploadedBy = By(item.UploadedBy), uploadedAt = Utc(item.UploadedAt), updatedBy = By(item.UpdatedBy), updatedAt = Utc(item.UpdatedAt),
            revisions = item.Revisions.Select(r => new { id = r.Id, fileName = r.FileName, contentType = r.ContentType, size = r.Size, sha256 = r.Sha256, uploadedBy = By(r.UploadedBy), uploadedAt = Utc(r.UploadedAt) }),
        };
    }

    /// <summary>Times are stored as UTC and read back without a kind on SQLite, so they are stamped before they are written as ISO text.</summary>
    private static string Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static IResult Refusal(AttachmentScope scope) => Results.Problem(scope.Problem, statusCode: scope.Status);

    private static string TooLarge(AttachmentLimits limits) => $"The file is larger than the {limits.MaxFileSize / (1024 * 1024)} MB this block accepts.";

    /// <summary>A store failure is the database's problem, answered as 503 with the detail in the log, never a crash of the page.</summary>
    private static async Task<(T? Value, IResult? Problem)> Run<T>(Func<Task<T>> work)
    {
        try { return (await work(), null); }
        catch (AttachmentStoreException error) { return (default, Results.Problem(Plain(error), statusCode: 503)); }
    }

    /// <summary>What anyone may be told: never the database's own words, which the log keeps.</summary>
    private static string Plain(AttachmentStoreException error) => error.NeedsSetup
        ? "File storage isn't set up on this site, so files can't be shown or added here yet."
        : "File storage isn't available right now. Please try again later.";

    /// <summary>The way to the setup screen, for a backoffice administrator alone. An ordinary visitor gets no pointer at all.</summary>
    private static async Task<string?> SetupUrlAsync(HttpContext http)
        => await http.RequestServices.GetRequiredService<ApplicationAccessPolicy>().BackOfficeAdministratorAsync(http) is null
            ? null : http.Request.PathBase + DatabaseSetupGuide.ScreenPath;

    private static IResult Audited(HttpContext http, string operation, AttachmentScope scope, long? id, IResult result, int status)
    {
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ApplicationAttachments").LogInformation(
            "Attachment operation {Operation} on {Owner}/{Key} file {File} by {Member} returned {Status}", operation, scope.Owner?.Alias, scope.Owner?.Key, id,
            scope.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? AppBlocks.Blocks.BlockOwnerAccess.AnonymousActor, status);
        return result;
    }

    /// <summary>The plain-ASCII fallback name beside the UTF-8 one, for clients that read only <c>filename=</c>.</summary>
    private static string SafeAscii(string name)
    {
        var ascii = new string(name.Select(c => c < 128 && c is not ('"' or '\\') && !char.IsControl(c) ? c : '_').ToArray());
        return ascii.Length == 0 ? "file" : ascii;
    }
}
