using System.Text;

namespace AppBlocks.Forms;

/// <summary>An ordered selection as one CSV record, with no line breaks or empty/duplicate item keys.</summary>
public static class ShuttleValue
{
    public static bool IsItemValue(string value) => !string.IsNullOrWhiteSpace(value) && value.IndexOfAny(['\r', '\n']) < 0;

    /// <summary>Null or empty means no selection. Quotes and commas use standard CSV escaping; spaces are significant.</summary>
    public static bool TryParse(string? csv, out IReadOnlyList<string> values)
    {
        values = [];
        if (string.IsNullOrEmpty(csv)) return true;
        if (csv.IndexOfAny(['\r', '\n']) >= 0) return false;
        var items = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var position = 0;
        while (position < csv.Length)
        {
            var item = new StringBuilder();
            if (csv[position] == '"')
            {
                position++;
                var closed = false;
                while (position < csv.Length)
                {
                    var c = csv[position++];
                    if (c != '"') { item.Append(c); continue; }
                    if (position < csv.Length && csv[position] == '"') { item.Append('"'); position++; }
                    else { closed = true; break; }
                }
                if (!closed || position < csv.Length && csv[position] != ',') return false;
            }
            else
            {
                while (position < csv.Length && csv[position] != ',')
                {
                    if (csv[position] == '"') return false;
                    item.Append(csv[position++]);
                }
            }
            var value = item.ToString();
            if (!IsItemValue(value) || !seen.Add(value)) return false;
            items.Add(value);
            if (position < csv.Length && ++position == csv.Length) return false;
        }
        values = items;
        return true;
    }

    /// <summary>Serializes item keys in the given order. An empty selection is the empty string.</summary>
    public static string Serialize(IEnumerable<string> values)
    {
        var items = values.ToArray();
        if (items.Any(value => !IsItemValue(value)) || items.Distinct(StringComparer.Ordinal).Count() != items.Length)
            throw new ArgumentException("Shuttle items must have unique, non-empty values without line breaks.", nameof(values));
        return string.Join(",", items.Select(value => value.IndexOfAny([',', '"']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value));
    }
}
