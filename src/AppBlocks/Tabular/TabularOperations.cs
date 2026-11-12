using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using AppBlocks.DataSources;
using AppBlocks.Forms;

namespace AppBlocks.Tabular;

/// <summary>Shared validation and presentation of tabular rows, independent of the endpoint serving them.</summary>
internal static class TabularOperations
{
    internal static string Revision(SqlDataSource source) => $"{TabularAccess.Definition}:{source.ContractVersion}";
    /// <summary>A change the block refuses before the engine sees it: its status, its position in the batch and the message, against a field or "".</summary>
    internal sealed record TabularRefusal(int Status, int Index, string Message, string Field = "");

    /// <summary>
    /// The runtime's changes as the engine takes them: only the block's own columns, as a form sends only its bound fields, without
    /// the link field, which is the server's; no new rows, changed rows or deletions where the block offers none. A refusal names the change.
    /// </summary>
    internal static TabularRefusal? Changes(TabularBlock block, SqlDataSource source, IReadOnlyList<TabularChange> sent, out List<DataSourceChange>? changes)
    {
        changes = null;
        var columns = block.Columns(source.Fields);
        var converted = new List<DataSourceChange>(sent.Count);
        for (var i = 0; i < sent.Count; i++)
        {
            var change = sent[i];
            if (Operation(change.Operation) is not { } operation) return new(422, i, "Each change is a create, an update or a delete.");
            if (operation == RowOperation.Create && block.HideAdd) return new(403, i, "This tabular form does not add rows.");
            if (operation == RowOperation.Update && block.HideEdit) return new(403, i, "This tabular form does not change saved rows.");
            if (operation == RowOperation.Delete && block.HideDelete) return new(403, i, "This tabular form does not delete rows.");
            var values = new JsonObject();
            if (change.Values is { ValueKind: JsonValueKind.Object } submitted)
                foreach (var property in submitted.EnumerateObject())
                {
                    if (property.Name.Equals(block.LinkField, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!columns.Any(c => c.Key == property.Name)) return new(422, i, $"'{property.Name}' is not a column of this tabular form.", property.Name);
                    values[property.Name] = JsonNode.Parse(property.Value.GetRawText());
                }
            else if (change.Values is { } other && other.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) return new(422, i, "Row values must be an object.");
            converted.Add(new DataSourceChange(operation, change.Key, change.Checksum, JsonSerializer.SerializeToElement(values)));
        }
        changes = converted;
        return null;
    }

    /// <summary>The operation a change names, in any letter case; null for anything else, so an unknown name never runs.</summary>
    private static RowOperation? Operation(string? sent) => sent?.Trim().ToLowerInvariant() switch {
        "create" => RowOperation.Create,
        "update" => RowOperation.Update,
        "delete" => RowOperation.Delete,
        _ => null,
    };

    /// <summary>A block's rows as the runtime takes them, also for a form that saved them with its record; <paramref name="ownerKey"/> is null for a record not saved yet.</summary>
    internal static async Task<object> Answer(TabularBlock block, SqlDataSource source, ClaimsPrincipal user, string? ownerAlias, string? ownerKey,
        DataSourceRowsResult rows, Func<string?> antiforgeryToken, CancellationToken ct)
    {
        var write = !block.ReadOnly && await source.MayWriteAsync(user, ct);
        var columns = block.Columns(source.Fields);
        var shown = columns.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        return new {
            owner = new { alias = ownerAlias, key = ownerKey },
            dataSource = source.Alias,
            revision = Revision(source),
            canAdd = write && !block.HideAdd && source.Definition.AllowInsert,
            canUpdate = write && !block.HideEdit && source.Definition.AllowUpdate,
            canDelete = write && !block.HideDelete && source.Definition.AllowDelete,
            maxRows = block.MaxRows,
            fields = columns,
            rows = rows.Rows!.Select(row => Project(row, shown)),
            newRow = rows.NewRow is { } fresh ? Project(fresh, shown) : null,
            antiforgeryToken = antiforgeryToken(),
        };
    }

    /// <summary>A row with the block's columns only, as a form answers with its bound fields only.</summary>
    private static FormRecord Project(FormRecord record, HashSet<string> shown)
        => record with { Values = record.Values.Where(p => shown.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value) };

    /// <summary>Logs a tabular operation, also one a form saved with its record.</summary>
    internal static void Audit(ILogger logger, string operation, string? dataSource, string? ownerAlias, string? ownerKey, ClaimsPrincipal? user, int status)
        => logger.LogInformation(
            "Tabular {Operation} on {DataSource} for {Owner}/{Key} by {Member} returned {Status}", operation, dataSource, ownerAlias,
            ownerKey, user?.FindFirstValue(ClaimTypes.NameIdentifier), status);
}
