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
/// </summary>
public static class TabularEndpoints
{
    public const string Route = "/api/tabular";

    public static void MapTabular(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(Route + "/{pageKey:guid}/{blockKey:guid}");
        group.MapGet("", List);
        group.MapPost("", Save);
    }

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
        var columns = block.Columns(scope.Source!.Fields);
        var changes = new List<DataSourceChange>(submission.Changes.Count);
        for (var i = 0; i < submission.Changes.Count; i++)
        {
            var change = submission.Changes[i];
            var operation = change.Operation?.Trim().ToLowerInvariant() ?? "";
            if (operation == "create" && block.HideAdd) return Audited(http, "save", scope, Problem(403, i, "This tabular form does not add rows."), 403);
            if (operation == "delete" && block.HideDelete) return Audited(http, "save", scope, Problem(403, i, "This tabular form does not delete rows."), 403);
            // Only the block's own columns can be sent, as a form sends only its bound fields; the link field is the server's.
            var values = new JsonObject();
            if (change.Values is { ValueKind: JsonValueKind.Object } sent)
                foreach (var property in sent.EnumerateObject())
                {
                    if (property.Name.Equals(block.LinkField, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!columns.Any(c => c.Key == property.Name)) return Problem(422, i, $"'{property.Name}' is not a column of this tabular form.", property.Name);
                    values[property.Name] = JsonNode.Parse(property.Value.GetRawText());
                }
            else if (change.Values is { } other && other.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) return Problem(422, i, "Row values must be an object.");
            changes.Add(new DataSourceChange(operation, change.Key, change.Version, JsonSerializer.SerializeToElement(values)));
        }
        var context = Context(scope, pageKey, blockKey, submission.SubmissionKey);
        var result = await scope.Source.SaveRowsAsync(scope.Scope!, changes, context, block.MaxRows, ct);
        if (result.Status != 200) return Audited(http, "save", scope, Failure(result), result.Status);
        return Audited(http, "save", scope, Results.Json(await Answer(scope, result, http, ct)), 200);
    }

    /// <summary>The list, the capabilities and the columns, with a fresh antiforgery token.</summary>
    private static async Task<object> Answer(TabularScope scope, DataSourceRowsResult rows, HttpContext http, CancellationToken ct)
    {
        var block = scope.Block!;
        var source = scope.Source!;
        var write = !block.ReadOnly && await source.MayWriteAsync(scope.User!, ct);
        var columns = block.Columns(source.Fields);
        var shown = columns.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        return new {
            owner = new { alias = scope.OwnerAlias, key = scope.Scope!.Value },
            dataSource = source.Alias,
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
        => new(scope.User!, pageKey, blockKey, $"{TabularAccess.Definition}:{scope.Source!.ContractVersion}", submissionKey, scope.Parameters);

    private static IResult Failure(DataSourceRowsResult result)
        => Results.Json(new { status = result.Status, index = result.Index, errors = result.Errors ?? new Dictionary<string, string[]>() }, statusCode: result.Status);

    private static IResult Problem(int status, int index, string message, string field = "")
        => Results.Json(new { status, index, errors = new Dictionary<string, string[]> { [field] = [message] } }, statusCode: status);

    private static IResult Refusal(TabularScope scope) => Results.Problem(scope.Problem, statusCode: scope.Status);

    private static IResult Audited(HttpContext http, string operation, TabularScope scope, IResult result, int status)
    {
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ApplicationTabular").LogInformation(
            "Tabular {Operation} on {DataSource} for {Owner}/{Key} by {Member} returned {Status}", operation, scope.Source?.Alias, scope.OwnerAlias,
            scope.Scope?.Value, scope.User?.FindFirstValue(ClaimTypes.NameIdentifier), status);
        return result;
    }
}
