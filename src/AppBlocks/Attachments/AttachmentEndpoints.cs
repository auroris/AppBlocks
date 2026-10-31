using System.Globalization;
using System.Net.Mime;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace AppBlocks.Attachments;

/// <summary>
/// <c>/api/attachments/{page}/{block}</c>: the files of the record an Attachments block shows. Every call resolves the
/// block through <see cref="AttachmentAccess"/> first, so page access and the owning record's own load decide, and a
/// file id is only ever looked up among that owner's links. Writes carry the antiforgery token the list hands out,
/// are refused on a read-only block, and are logged like form operations.
/// </summary>
public static class AttachmentEndpoints
{
    public const string Route = "/api/attachments";

    /// <summary>Room for the multipart framing and the text fields around a file of the allowed size.</summary>
    private const long MultipartOverhead = 64 * 1024;

    public static void MapAttachments(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(Route + "/{pageKey:guid}/{blockKey:guid}");
        group.MapGet("", List);
        group.MapPost("", Upload);
        group.MapGet("/{id:long}", Download);
        group.MapPut("/{id:long}", Replace);
        group.MapDelete("/{id:long}", Remove);
    }

    private static async Task<IResult> List(Guid pageKey, Guid blockKey, HttpContext http, AttachmentAccess access, AttachmentStore store, CancellationToken ct)
    {
        var scope = await access.ResolveAsync(pageKey, blockKey, ct);
        if (scope.Status != 200) return Refusal(scope);
        http.Response.Headers.CacheControl = "no-store";
        var items = await Run(() => store.ListAsync(scope.Owner!, ct));
        if (items.Problem is not null) return items.Problem;
        return Results.Json(new {
            owner = new { alias = scope.Owner!.Alias, key = scope.Owner.Key },
            canWrite = scope.CanWrite,
            purposes = scope.Block!.Purposes,
            maxFileSize = scope.Block.Limits.MaxFileSize,
            accept = scope.Block.Limits.Accept,
            items = items.Value!.Select(Item),
            antiforgeryToken = http.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(http).RequestToken,
        });
    }

    private static async Task<IResult> Download(Guid pageKey, Guid blockKey, long id, HttpContext http, AttachmentAccess access, AttachmentStore store, CancellationToken ct)
    {
        var scope = await access.ResolveAsync(pageKey, blockKey, ct);
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

    private static Task<IResult> Upload(Guid pageKey, Guid blockKey, HttpContext http, AttachmentAccess access, AttachmentStore store, CancellationToken ct)
        => Write(pageKey, blockKey, null, http, access, store, ct);

    private static Task<IResult> Replace(Guid pageKey, Guid blockKey, long id, HttpContext http, AttachmentAccess access, AttachmentStore store, CancellationToken ct)
        => Write(pageKey, blockKey, id, http, access, store, ct);

    /// <summary>
    /// An upload (no id) or a replacement: a multipart body with a <c>file</c> part and the text parts <c>purpose</c>,
    /// <c>label</c> and <c>notes</c>. The size limit is applied before the body is read: a declared length over it is
    /// refused at once, and the request body is capped so an undeclared one is cut off at the limit.
    /// </summary>
    private static async Task<IResult> Write(Guid pageKey, Guid blockKey, long? id, HttpContext http, AttachmentAccess access, AttachmentStore store, CancellationToken ct)
    {
        var scope = await access.ResolveAsync(pageKey, blockKey, ct);
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
            if (!limits.Accepts(fileName)) return Results.Problem($"Files of this type are not accepted here. Accepted: {limits.Accept}.", statusCode: 415);
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
        return Audited(http, id is null ? "upload" : "replace", scope, result.Value.Id, Results.Json(Item(result.Value), statusCode: id is null ? 201 : 200), id is null ? 201 : 200);
    }

    private static async Task<IResult> Remove(Guid pageKey, Guid blockKey, long id, HttpContext http, AttachmentAccess access, AttachmentStore store, CancellationToken ct)
    {
        var scope = await access.ResolveAsync(pageKey, blockKey, ct);
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

    /// <summary>What the list and the writes return for a file: its current revision and the earlier ones, newest first.</summary>
    private static object Item(AttachmentItem item) => new {
        id = item.Id, fileName = item.FileName, contentType = item.ContentType, size = item.Size, sha256 = item.Sha256,
        purpose = item.Purpose, label = item.Label, notes = item.Notes,
        uploadedBy = item.UploadedBy, uploadedAt = Utc(item.UploadedAt), updatedBy = item.UpdatedBy, updatedAt = Utc(item.UpdatedAt),
        revisions = item.Revisions.Select(r => new { id = r.Id, fileName = r.FileName, contentType = r.ContentType, size = r.Size, sha256 = r.Sha256, uploadedBy = r.UploadedBy, uploadedAt = Utc(r.UploadedAt) }),
    };

    /// <summary>Times are stored as UTC and read back without a kind on SQLite, so they are stamped before they are written as ISO text.</summary>
    private static string Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static IResult Refusal(AttachmentScope scope) => Results.Problem(scope.Problem, statusCode: scope.Status);

    private static string TooLarge(AttachmentLimits limits) => $"The file is larger than the {limits.MaxFileSize / (1024 * 1024)} MB this block accepts.";

    /// <summary>A store failure is the database's problem, answered as 503 with the detail in the log, never a crash of the page.</summary>
    private static async Task<(T? Value, IResult? Problem)> Run<T>(Func<Task<T>> work)
    {
        try { return (await work(), null); }
        catch (AttachmentStoreException error) { return (default, Results.Problem(error.Message, statusCode: 503)); }
    }

    private static IResult Audited(HttpContext http, string operation, AttachmentScope scope, long? id, IResult result, int status)
    {
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ApplicationAttachments").LogInformation(
            "Attachment operation {Operation} on {Owner}/{Key} file {File} by {Member} returned {Status}", operation, scope.Owner?.Alias, scope.Owner?.Key, id,
            scope.User?.FindFirstValue(ClaimTypes.NameIdentifier), status);
        return result;
    }

    /// <summary>The plain-ASCII fallback name beside the UTF-8 one, for clients that read only <c>filename=</c>.</summary>
    private static string SafeAscii(string name)
    {
        var ascii = new string(name.Select(c => c < 128 && c is not ('"' or '\\') && !char.IsControl(c) ? c : '_').ToArray());
        return ascii.Length == 0 ? "file" : ascii;
    }
}
