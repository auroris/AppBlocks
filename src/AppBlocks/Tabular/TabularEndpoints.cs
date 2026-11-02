using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Antiforgery;
using AppBlocks.DataSources;
using AppBlocks.Forms;

namespace AppBlocks.Tabular;

/// <summary>One change the runtime sends: <c>create</c>, <c>update</c> or <c>delete</c>, the row's key and version for the latter two, and the values.</summary>
public sealed record TabularChange(string Operation, string? Key, string? Version, JsonElement? Values);

/// <summary>A batch of changes to one record's rows, under one submission key so a retry is harmless.</summary>
public sealed record TabularSubmission(string? SubmissionKey, IReadOnlyList<TabularChange>? Changes);

/// <summary>
/// <c>/api/tabular/{page}/{block}</c>: the rows of the record a Tabular form block shows. Every call resolves the
/// block through <see cref="TabularAccess"/> first, so page access and the owning record's own load decide, and the
/// rows are then read and written through the child data source with its own rules. A save is one batch, applied
/// whole or not at all, carrying the antiforgery token the list hands out; it is logged like form operations.
/// <c>/new</c> lists a block inside a form whose record is not saved yet: no rows, and what a new row starts from. Its
/// rows save with that form's record, through the form endpoints (<see cref="FormDetailSubmission"/>).
/// </summary>
public static class TabularEndpoints
{
    public const string Route = "/api/tabular";

    public static void MapTabular(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(Route + "/{pageKey:guid}/{blockKey:guid}");
        group.MapGet("", List);
        group.MapGet("/new", ListNew);
        group.MapPost("", Save);
    }

    /// <summary>The revision a block's rows are listed under, which a save with its form sends back, so a changed data source is noticed.</summary>
    public static string Revision(SqlDataSource source) => $"{TabularAccess.Definition}:{source.ContractVersion}";

    private static async Task<IResult> List(Guid pageKey, Guid blockKey, HttpContext http, TabularAccess access, CancellationToken ct)
    {
        var scope = await access.ResolveAsync(pageKey, blockKey, ct);
        if (scope.Status != 200) return Refusal(scope);
        http.Response.Headers.CacheControl = "no-store";
        var context = Context(scope, pageKey, blockKey, null);
        var rows = await scope.Source!.ListRowsAsync(scope.Scope!, context, scope.Block!.MaxRows, ct);
        if (rows.Status != 200) return Audited(http, "list", scope, Failure(rows), rows.Status);
        return Results.Json(await Answer(scope, rows, http, ct));
    }

    private static async Task<IResult> ListNew(Guid pageKey, Guid blockKey, HttpContext http, TabularAccess access, CancellationToken ct)
    {
        var scope = await access.ResolveNewAsync(pageKey, blockKey, ct);
        if (scope.Status != 200) return Refusal(scope);
        http.Response.Headers.CacheControl = "no-store";
        var rows = await scope.Source!.NewRowsAsync(scope.Block!.LinkField, Context(scope, pageKey, blockKey, null), ct);
        if (rows.Status != 200) return Audited(http, "list", scope, Failure(rows), rows.Status);
        return Results.Json(await Answer(scope, rows, http, ct));
    }

    private static async Task<IResult> Save(Guid pageKey, Guid blockKey, HttpContext http, TabularAccess access, CancellationToken ct)
    {
        var scope = await access.ResolveAsync(pageKey, blockKey, ct);
        if (scope.Status != 200) return Refusal(scope);
        if (scope.Block!.ReadOnly || !await scope.Source!.MayWriteAsync(scope.User!, ct))
            return Audited(http, "save", scope, Results.Problem("This tabular form is read only.", statusCode: 403), 403);
        try { await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http); }
        catch (AntiforgeryValidationException) { return Results.BadRequest(); }
        TabularSubmission? submission;
        try { submission = await http.Request.ReadFromJsonAsync<TabularSubmission>(ct); }
        catch (JsonException) { return Results.BadRequest(); }
        if (submission?.Changes is null) return Results.BadRequest();
        http.Response.Headers.CacheControl = "no-store";
        var block = scope.Block;
        if (Changes(block, scope.Source!, submission.Changes, out var changes) is { } refused)
            return refused.Status == 403 ? Audited(http, "save", scope, Problem(refused), 403) : Problem(refused);
        var context = Context(scope, pageKey, blockKey, submission.SubmissionKey);
        var result = await scope.Source!.SaveRowsAsync(scope.Scope!, changes!, context, block.MaxRows, ct);
        if (result.Status != 200) return Audited(http, "save", scope, Failure(result), result.Status);
        return Audited(http, "save", scope, Results.Json(await Answer(scope, result, http, ct)), 200);
    }

    /// <summary>A change the block refuses before the engine sees it: its status, its position in the batch and the message, against a field or "".</summary>
    internal sealed record TabularRefusal(int Status, int Index, string Message, string Field = "");

    /// <summary>
    /// The runtime's changes as the engine takes them: only the block's own columns, as a form sends only its bound fields, without
    /// the link field, which is the server's; no new rows or deletions where the block offers none. A refusal names the change.
    /// </summary>
    internal static TabularRefusal? Changes(TabularBlock block, SqlDataSource source, IReadOnlyList<TabularChange> sent, out List<DataSourceChange>? changes)
    {
        changes = null;
        var columns = block.Columns(source.Fields);
        var converted = new List<DataSourceChange>(sent.Count);
        for (var i = 0; i < sent.Count; i++)
        {
            var change = sent[i];
            var operation = change.Operation?.Trim().ToLowerInvariant() ?? "";
            if (operation == "create" && block.HideAdd) return new(403, i, "This tabular form does not add rows.");
            if (operation == "delete" && block.HideDelete) return new(403, i, "This tabular form does not delete rows.");
            var values = new JsonObject();
            if (change.Values is { ValueKind: JsonValueKind.Object } submitted)
                foreach (var property in submitted.EnumerateObject())
                {
                    if (property.Name.Equals(block.LinkField, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!columns.Any(c => c.Key == property.Name)) return new(422, i, $"'{property.Name}' is not a column of this tabular form.", property.Name);
                    values[property.Name] = JsonNode.Parse(property.Value.GetRawText());
                }
            else if (change.Values is { } other && other.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) return new(422, i, "Row values must be an object.");
            converted.Add(new DataSourceChange(operation, change.Key, change.Version, JsonSerializer.SerializeToElement(values)));
        }
        changes = converted;
        return null;
    }

    /// <summary>The list, the capabilities and the columns, with a fresh antiforgery token.</summary>
    private static Task<object> Answer(TabularScope scope, DataSourceRowsResult rows, HttpContext http, CancellationToken ct)
        => Answer(scope.Block!, scope.Source!, scope.User!, scope.OwnerAlias, scope.Scope?.Value, rows, http, ct);

    /// <summary>A block's rows as the runtime takes them, also for a form that saved them with its record; <paramref name="ownerKey"/> is null for a record not saved yet.</summary>
    internal static async Task<object> Answer(TabularBlock block, SqlDataSource source, ClaimsPrincipal user, string? ownerAlias, string? ownerKey,
        DataSourceRowsResult rows, HttpContext http, CancellationToken ct)
    {
        var write = !block.ReadOnly && await source.MayWriteAsync(user, ct);
        var columns = block.Columns(source.Fields);
        var shown = columns.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        return new {
            owner = new { alias = ownerAlias, key = ownerKey },
            dataSource = source.Alias,
            revision = Revision(source),
            canAdd = write && !block.HideAdd && source.Definition.AllowInsert,
            canUpdate = write && source.Definition.AllowUpdate,
            canDelete = write && !block.HideDelete && source.Definition.AllowDelete,
            maxRows = block.MaxRows,
            fields = columns,
            rows = rows.Rows!.Select(row => Project(row, shown)),
            newRow = rows.NewRow is { } fresh ? Project(fresh, shown) : null,
            antiforgeryToken = http.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(http).RequestToken,
        };
    }

    /// <summary>A row with the block's columns only, as a form answers with its bound fields only.</summary>
    private static FormRecord Project(FormRecord record, HashSet<string> shown)
        => record with { Values = record.Values.Where(p => shown.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value) };

    private static FormExecutionContext Context(TabularScope scope, Guid pageKey, Guid blockKey, string? submissionKey)
        => new(scope.User!, pageKey, blockKey, Revision(scope.Source!), submissionKey, scope.Parameters);

    private static IResult Failure(DataSourceRowsResult result)
        => Results.Json(new { status = result.Status, index = result.Index, errors = result.Errors ?? new Dictionary<string, string[]>() }, statusCode: result.Status);

    private static IResult Problem(TabularRefusal refusal)
        => Results.Json(new { status = refusal.Status, index = refusal.Index, errors = new Dictionary<string, string[]> { [refusal.Field] = [refusal.Message] } }, statusCode: refusal.Status);

    private static IResult Refusal(TabularScope scope) => Results.Problem(scope.Problem, statusCode: scope.Status);

    private static IResult Audited(HttpContext http, string operation, TabularScope scope, IResult result, int status)
    {
        Audit(http, operation, scope.Source?.Alias, scope.OwnerAlias, scope.Scope?.Value, scope.User, status);
        return result;
    }

    /// <summary>Logs a tabular operation, also one a form saved with its record.</summary>
    internal static void Audit(HttpContext http, string operation, string? dataSource, string? ownerAlias, string? ownerKey, ClaimsPrincipal? user, int status)
        => http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ApplicationTabular").LogInformation(
            "Tabular {Operation} on {DataSource} for {Owner}/{Key} by {Member} returned {Status}", operation, dataSource, ownerAlias,
            ownerKey, user?.FindFirstValue(ClaimTypes.NameIdentifier), status);
}
