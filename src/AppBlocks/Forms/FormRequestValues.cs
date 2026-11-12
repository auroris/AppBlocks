using System.Text.Json;
using AppBlocks.Blocks;

namespace AppBlocks.Forms;

/// <summary>Bounded external values shared by form, block and data requests.</summary>
public static class FormRequestValues
{
    /// <summary>
    /// The request's query parameters, which data sources bind as <c>:PARAM.name</c>. Bounded like any other
    /// input: at most 50 names of identifier characters, one value each of at most 4000 characters. A repeated
    /// name is ambiguous and left out rather than guessed. The culture parameter, and any <paramref name="reserved"/>
    /// name an endpoint reads for itself, are not parameters. Paper reports bind theirs the same way.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Parameters(IQueryCollection query, params string[] reserved)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in query)
        {
            if (parameters.Count == 50) break;
            if (name.Equals(AppBlockContent.CultureParameter, StringComparison.OrdinalIgnoreCase) || reserved.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (values.Count == 1 && values[0] is { Length: <= 4000 } value && IsParameterName(name)) parameters[name] = value;
        }
        return parameters;

        static bool IsParameterName(string name) => name.Length is > 0 and <= 64 && (char.IsAsciiLetter(name[0]) || name[0] == '_')
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    }

    /// <summary>The most names <see cref="FormData"/> keeps.</summary>
    public const int MaxFormDataNames = 50;

    /// <summary>The longest value <see cref="FormData"/> keeps: room for a real captcha token, which runs to several KB.</summary>
    public const int MaxFormDataValueLength = 16384;

    /// <summary>What a request with no other controls carries.</summary>
    public static readonly IReadOnlyDictionary<string, string> NoFormData = new Dictionary<string, string>(0);

    /// <summary>
    /// The submitted part of a form that is not its fields, bounded like <see cref="Parameters"/>: at most
    /// <see cref="MaxFormDataNames"/> names of letters, digits, <c>_</c>, <c>-</c>, <c>.</c> and <c>:</c>, up to 64 characters, one
    /// text value each of at most <see cref="MaxFormDataValueLength"/> characters. Names ignore case. A repeated name is
    /// ambiguous and left out entirely rather than guessed, and so is anything else over a limit or not a string. A
    /// <paramref name="reserved"/> name, such as the antiforgery field, is never passed on.
    /// </summary>
    public static IReadOnlyDictionary<string, string> FormData(JsonElement? submitted, params string[] reserved)
    {
        if (submitted is not { ValueKind: JsonValueKind.Object } controls) return NoFormData;
        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var repeated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var control in controls.EnumerateObject())
        {
            if (!seen.Add(control.Name)) { repeated.Add(control.Name); continue; }
            if (data.Count < MaxFormDataNames && control.Value.ValueKind == JsonValueKind.String && control.Value.GetString() is { Length: <= MaxFormDataValueLength } value
                && IsControlName(control.Name) && !reserved.Contains(control.Name, StringComparer.OrdinalIgnoreCase)) data[control.Name] = value;
        }
        foreach (var name in repeated) data.Remove(name);
        return data;

        static bool IsControlName(string name) => name.Length is > 0 and <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':');
    }

}
