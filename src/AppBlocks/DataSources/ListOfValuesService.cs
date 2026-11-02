using System.Text.Json;
using AppBlocks.Forms;
using AppBlocks.Blocks;

namespace AppBlocks.DataSources;

public sealed class ListOfValuesService(PublishedDataCatalog catalog, DataSourceQueries queries, ILogger<ListOfValuesService> logger)
{
    public async Task<IReadOnlyList<FormChoice>> ChoicesAsync(string selection, FormExecutionContext context, CancellationToken ct = default)
    {
        var list = catalog.FindList(selection) ?? throw new DataSourceException("The selected list of values is not published.");
        var result = await queries.QueryAsync(list.DataSource, context, list.MaximumRows, ct);
        return result.Rows.Where(row => row.GetValueOrDefault(list.ValueColumn) is not null)
            .Select(row => new FormChoice(DataSourceQueries.ChoiceValue(row.GetValueOrDefault(list.ValueColumn)), DataSourceQueries.ChoiceValue(row.GetValueOrDefault(list.DisplayColumn))))
            .DistinctBy(choice => choice.Value, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Changed values must be current choices; a server-loaded unchanged value can remain after its option is retired.</summary>
    public async Task<Dictionary<string, string[]>> ValidateAsync(FormDefinition form, JsonElement input, FormExecutionContext context, CancellationToken ct,
        IReadOnlyDictionary<string, object?>? stored = null)
    {
        var errors = new Dictionary<string, string[]>();
        if (input.ValueKind != JsonValueKind.Object) return errors;
        foreach (var block in form.Fields.Where(block => block.Content.ContentType.Alias is "appSelect" or AppBlockSchema.ShuttleAlias))
        {
            var selection = block.Content.Value<string>("listOfValues");
            var field = block.Content.Value<string>("field")!;
            if (string.IsNullOrWhiteSpace(selection) || !input.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null || value.ToString().Length == 0) continue;
            if (stored?.TryGetValue(field, out var previous) == true && FieldValues.Unchanged(value, previous)) continue;
            try
            {
                var choices = await ChoicesAsync(selection, context, ct);
                if (block.Content.ContentType.Alias == AppBlockSchema.ShuttleAlias)
                {
                    // Retired items already on this record may be reordered or removed, but cannot be added to another record.
                    ShuttleValue.TryParse(stored?.GetValueOrDefault(field)?.ToString(), out var previousItems);
                    if (value.ValueKind != JsonValueKind.String || !ShuttleValue.TryParse(value.GetString(), out var selected)
                        || selected.Any(item => !choices.Any(choice => choice.Value == item) && !previousItems.Contains(item, StringComparer.Ordinal)))
                        errors[field] = ["Select available choices."];
                }
                else if (!choices.Any(choice => choice.Value == value.ToString())) errors[field] = ["Select an available choice."];
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogWarning(error, "List {List} could not validate field {Field}.", selection, field);
                errors[field] = ["The choices for this field are not available. Reload the page and try again."];
            }
        }
        return errors;
    }
}
