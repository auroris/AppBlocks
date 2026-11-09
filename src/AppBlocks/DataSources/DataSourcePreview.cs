using System.Globalization;
using System.Text.Json;
using AppBlocks.Forms;

namespace AppBlocks.DataSources;

public sealed record DataSourcePreviewColumn(string Field, string Column, string Kind, bool ReadOnly, bool Required);
/// <param name="ApplicationNames">The <c>:APP.</c> names the bind variable providers declare, for the editor to give sample values; null when they are not known.</param>
/// <param name="Warnings">What publishing would warn about, such as an <c>:APP.</c> name no provider declares or writes through a view.</param>
public sealed record DataSourcePreviewResult(IReadOnlyList<DataSourcePreviewColumn> Columns,
    IReadOnlyDictionary<string, object?>? Row, IReadOnlyList<string> Errors, IReadOnlyList<string>? ApplicationNames = null, IReadOnlyList<string>? Warnings = null);

/// <summary>Tests the editor's current definition with sample bind values. Only the row source is executed.</summary>
public sealed class DataSourcePreview(DataSourceRuntime runtime, BindVariableProviders? providers = null)
{
    public async Task<DataSourcePreviewResult> TestAsync(DataSourceDefinition definition, DataSourceUser editor,
        IReadOnlyDictionary<string, JsonElement> samples, CancellationToken ct)
    {
        var warnings = new List<string>();
        var result = await RunAsync(definition, editor, samples, warnings, ct);
        var declared = providers?.DeclaredNames?.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var warning = providers?.UndeclaredWarning(definition.ApplicationNames());
        if (warning is not null) warnings.Insert(0, warning);
        return result with { ApplicationNames = declared, Warnings = warnings };
    }

    private async Task<DataSourcePreviewResult> RunAsync(DataSourceDefinition definition, DataSourceUser editor,
        IReadOnlyDictionary<string, JsonElement> samples, ICollection<string> warnings, CancellationToken ct)
    {
        var errors = new List<string>();
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, sample) in samples)
        {
            var variable = BindVariable.Parse(name.TrimStart(':'));
            if (variable is null) { errors.Add($"Unknown sample bind variable: {name}."); continue; }
            if (variable.Kind == BindVariableKind.UserGroups)
            {
                if (sample.ValueKind != JsonValueKind.Array || sample.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String))
                    errors.Add("USER_GROUPS needs an array of group names.");
                else values[variable.ToString()] = sample.EnumerateArray().Select(v => v.GetString()!).ToArray();
            }
            else if (sample.ValueKind is JsonValueKind.Array or JsonValueKind.Object or JsonValueKind.Undefined)
                errors.Add($"{name} needs a text, number, boolean or null value.");
            else values[variable.ToString()] = sample.ValueKind switch {
                JsonValueKind.Null => null, JsonValueKind.True => true, JsonValueKind.False => false,
                JsonValueKind.Number => sample.TryGetDecimal(out var number) ? number : (object)sample.GetDouble(), _ => sample.GetString(),
            };
        }
        if (errors.Count > 0) return new([], null, errors);
        var plan = runtime.Inspect(definition, errors, warnings);
        if (plan is null || errors.Count > 0) return new([], null, errors.Count > 0 ? errors : ["The data source could not be prepared."]);
        var columns = plan.Columns.Select(c => new DataSourcePreviewColumn(c.Field, c.Column, c.Kind, c.ReadOnly, c.Required)).ToArray();
        // Without a sample, the editor's own facts and the current time, as a request would bind them; parameters and application values bind NULL.
        var current = new BindVariableValues(editor, null, runtime.Now(definition), null);
        object? Value(BindVariable variable)
        {
            if (values.TryGetValue(variable.ToString(), out var sample))
            {
                if (variable.Kind is not (BindVariableKind.Now or BindVariableKind.NowUtc) || sample is null) return sample;
                var text = Convert.ToString(sample, CultureInfo.InvariantCulture);
                if (variable.Kind == BindVariableKind.NowUtc)
                {
                    // A sample without a zone is already UTC; one with an offset is converted.
                    if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment))
                        throw new DataSourceException("NOW_UTC needs a date and time value.");
                    return plan.Dialect.NowUtc(moment.UtcDateTime);
                }
                if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var instant))
                    throw new DataSourceException("NOW needs a date and time value.");
                return plan.Dialect.Now(instant);
            }
            return current.Bind(variable, plan.Dialect);
        }
        try
        {
            var parameters = plan.Parameters();
            var from = plan.From(parameters, Value);
            var restriction = plan.Restriction?.Render(parameters, Value);
            await using var connection = await runtime.OpenAsync(definition.Connection, ct);
            await using var command = parameters.Command(connection, null,
                plan.Dialect.Select(from, plan.Columns.Select(c => c.Column), [], restriction, limit: 1));
            await using var reader = await command.ExecuteReaderAsync(ct);
            Dictionary<string, object?>? row = null;
            if (await reader.ReadAsync(ct))
            {
                row = new(StringComparer.Ordinal);
                foreach (var column in plan.Columns)
                {
                    var value = reader[column.Column];
                    row[column.Column] = value is DBNull ? null : FieldValues.ToJson(value);
                }
            }
            return new(columns, row, []);
        }
        catch (Exception error) when (error is System.Data.Common.DbException or DataSourceException)
        {
            return new(columns, null, [error.Message]);
        }
    }
}
