using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AppBlocks.DataSources;

/// <summary>The field kinds the form blocks know, as <see cref="Forms.FormField.Kind"/> spells them.</summary>
public static class FieldKinds
{
    public const string Text = "text";
    public const string Number = "number";
    public const string Date = "date";
    public const string Time = "time";
    public const string DateTime = "datetime";
    public const string Checkbox = "checkbox";
    public static readonly string[] All = [Text, Number, Date, Time, DateTime, Checkbox];

    /// <summary>The length a text field gets when its column has none (SQLite TEXT, nvarchar(max)): input stays bounded.</summary>
    public const int DefaultTextLength = 4000;
    /// <summary>The longest text a mapping may allow.</summary>
    public const int MaxTextLength = 1_000_000;
}

/// <summary>
/// Converts field values between the three shapes they take. The JSON shape is what forms send and receive:
/// text, invariant decimal text, true or false, <c>yyyy-MM-dd</c>, <c>HH:mm:ss</c> and ISO date-times with an offset.
/// The canonical shape is one CLR type per kind (string, decimal, bool, DateOnly, TimeOnly, DateTimeOffset), which
/// comparisons, checksums and the dialects use. Database values arrive as whatever the provider returns.
/// Date-times without a zone in the database are the web server's local time, like <c>:NOW</c>.
/// </summary>
internal static class FieldValues
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>
    /// The JSON shape of a canonical value. Times keep milliseconds, the most an HTML time input and a JavaScript
    /// date accept; an unchanged value is recognised by this text (<see cref="Unchanged"/>), so nothing finer is lost by a save.
    /// </summary>
    public static object? ToJson(object? value) => value switch {
        null => null,
        string text => text,
        decimal number => number.ToString(Invariant),
        double number => number.ToString("R", Invariant),
        bool flag => flag,
        DateOnly date => date.ToString("yyyy-MM-dd", Invariant),
        TimeOnly time => time.ToString("HH:mm:ss.FFF", Invariant),
        DateTimeOffset moment => moment.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFzzz", Invariant),
        _ => System.Convert.ToString(value, Invariant),
    };

    /// <summary>
    /// Whether a submitted value is the stored value sent back as it was loaded. The form endpoints return every
    /// field on an update, shown or not, so such a value is no change: it is neither checked nor written, and a
    /// stored value the field's rules would no longer accept (a legacy blank, an over-long text) does not block a save.
    /// </summary>
    public static bool Unchanged(JsonElement submitted, object? stored) => ToJson(stored) switch {
        null => submitted.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined,
        string text => submitted.ValueKind == JsonValueKind.String && submitted.GetString() == text,
        bool flag => submitted.ValueKind == (flag ? JsonValueKind.True : JsonValueKind.False),
        _ => false,
    };

    /// <summary>The canonical value of whatever a provider returned for a column of the given kind; null when it cannot be read as that kind.</summary>
    public static object? FromDatabase(object? value, string kind)
    {
        if (value is null or DBNull) return null;
        switch (kind)
        {
            case FieldKinds.Number:
                return value switch {
                    decimal number => number,
                    double number => ToDecimal(number),
                    float number => ToDecimal(number),
                    string text => decimal.TryParse(text, NumberStyles.Float, Invariant, out var parsed) ? parsed : null,
                    bool flag => flag ? 1m : 0m,
                    IConvertible convertible => convertible.ToDecimal(Invariant),
                    _ => null,
                };
            case FieldKinds.Checkbox:
                return value switch {
                    bool flag => flag,
                    string text => text.Trim().ToLowerInvariant() switch { "1" or "true" or "yes" or "y" => true, "0" or "false" or "no" or "n" or "" => false, _ => null },
                    IConvertible convertible => convertible.ToDecimal(Invariant) != 0,
                    _ => null,
                };
            case FieldKinds.Date:
                return value switch {
                    DateOnly date => date,
                    DateTime moment => DateOnly.FromDateTime(moment),
                    DateTimeOffset moment => DateOnly.FromDateTime(moment.DateTime),
                    string text when DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", Invariant, DateTimeStyles.None, out var date) => date,
                    string text when DateTime.TryParse(text, Invariant, DateTimeStyles.AllowWhiteSpaces, out var moment) => DateOnly.FromDateTime(moment),
                    _ => null,
                };
            case FieldKinds.Time:
                return value switch {
                    TimeOnly time => time,
                    TimeSpan span when span >= TimeSpan.Zero && span < TimeSpan.FromDays(1) => TimeOnly.FromTimeSpan(span),
                    DateTime moment => TimeOnly.FromDateTime(moment),
                    string text when TimeOnly.TryParse(text.Trim(), Invariant, out var time) => time,
                    _ => null,
                };
            case FieldKinds.DateTime:
                return value switch {
                    DateTimeOffset moment => moment,
                    DateTime moment => Local(moment),
                    string text when DateTimeOffset.TryParse(text.Trim(), Invariant, DateTimeStyles.AssumeLocal, out var moment) => moment,
                    _ => null,
                };
            default:
                return value switch {
                    string text => text,
                    Guid guid => guid.ToString("D"),
                    DateTime moment => moment.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", Invariant).TrimEnd('.'),
                    DateTimeOffset moment => moment.ToString("O", Invariant),
                    byte[] bytes => System.Convert.ToBase64String(bytes),
                    IFormattable formattable => formattable.ToString(null, Invariant),
                    _ => value.ToString(),
                };
        }
    }

    /// <summary>A moment as <c>:NOW_UTC</c> writes it into text: ISO-8601 to the second with <c>Z</c>, fixed width so it sorts in time order.</summary>
    public static string UtcText(DateTimeOffset moment) => moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", Invariant);

    private static decimal? ToDecimal(double number) => double.IsFinite(number) && Math.Abs(number) < 7.9e27 ? (decimal)number : null;

    /// <summary>
    /// A date-time from the database or from <c>:NOW</c>, read as server local time unless it says UTC. The
    /// extremes (0001-01-01, 9999-12-31) cannot carry every offset; those keep their wall time at UTC instead.
    /// </summary>
    public static DateTimeOffset Local(DateTime moment)
    {
        try
        {
            return moment.Kind == DateTimeKind.Utc ? new DateTimeOffset(moment).ToLocalTime() : new DateTimeOffset(DateTime.SpecifyKind(moment, DateTimeKind.Local));
        }
        catch (ArgumentOutOfRangeException) { return new DateTimeOffset(DateTime.SpecifyKind(moment, DateTimeKind.Unspecified), TimeSpan.Zero); }
    }

    /// <summary>
    /// The canonical value of a submitted JSON value, checked against the field's own rules; an error message otherwise. A blank is
    /// refused for a required field, and on an update also for a column that must hold a value, which a default fills only on insert.
    /// </summary>
    public static object? FromJson(JsonElement value, PlanColumn column, bool updating, out string? error)
    {
        error = null;
        // Blank text is no value, as the form runtime sends it, so a required field cannot be satisfied with spaces.
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            || value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()))
        {
            error = column.Required || updating && column.NeedsValue ? "A value is required." : null;
            return null;
        }
        object? result = column.Kind switch {
            FieldKinds.Number => value.ValueKind switch {
                JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
                JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Float, Invariant, out var number) => number,
                _ => null,
            },
            FieldKinds.Checkbox => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null,
            FieldKinds.Date => value.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", Invariant, DateTimeStyles.None, out var date) ? date : null,
            FieldKinds.Time => value.ValueKind == JsonValueKind.String && TimeOnly.TryParse(value.GetString(), Invariant, out var time) ? time : null,
            FieldKinds.DateTime => value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), Invariant, DateTimeStyles.None, out var moment) ? moment : null,
            _ => value.ValueKind == JsonValueKind.String ? value.GetString() : null,
        };
        if (result is null) { error = "Enter a valid value."; return null; }
        return Check(result, column, out error) ? result : null;
    }

    /// <summary>The field's length, range and choice rules, enforced by the engine whatever the form already checked.</summary>
    public static bool Check(object value, PlanColumn column, out string? error)
    {
        error = value switch {
            string text when column.MaxLength is { } max && text.Length > max => $"Use at most {max} characters.",
            decimal number when number < (column.Minimum ?? decimal.MinValue) || number > (column.Maximum ?? decimal.MaxValue) => "The number is outside the allowed range.",
            _ when column.Choices is { Count: > 0 } choices && !choices.Any(c => c.Value == System.Convert.ToString(ToJson(value), Invariant)) => "Select an available choice.",
            _ => null,
        };
        return error is null;
    }

    /// <summary>The canonical value of an insert default's literal text, or of a bind variable's value, for a field of the given kind.</summary>
    public static object? FromValue(object? value, string kind, out bool valid)
    {
        valid = true;
        switch (value)
        {
            case null: return null;
            case DateTime moment when kind == FieldKinds.Date: return DateOnly.FromDateTime(moment);
            case DateTime moment when kind == FieldKinds.Time: return TimeOnly.FromDateTime(moment);
            case DateTime moment when kind == FieldKinds.DateTime: return Local(moment);
            case DateTime moment: return moment.ToString("yyyy-MM-dd HH:mm:ss", Invariant);
            case string text when kind == FieldKinds.Text: return text;
            case string text:
                var converted = FromDatabase(text, kind);
                valid = converted is not null;
                return converted;
            default:
                var other = FromDatabase(value, kind);
                valid = other is not null;
                return other;
        }
    }

    /// <summary>Whether two canonical values are the same value, so an update writes only what changed.</summary>
    public static bool Same(object? a, object? b) => (a, b) switch {
        (null, null) => true,
        (null, _) or (_, null) => false,
        (DateTimeOffset x, DateTimeOffset y) => x.UtcDateTime == y.UtcDateTime,
        (decimal x, decimal y) => x == y,
        _ => a.Equals(b),
    };

    /// <summary>A stable hash of a row's values for opt-in conflict detection.</summary>
    public static string Checksum(IEnumerable<(string Column, object? Value)> values)
    {
        var text = new StringBuilder();
        foreach (var (column, value) in values)
            text.Append(column).Append('\u001f').Append(ToJson(value) switch { null => "\u0000", bool flag => flag ? "true" : "false", var other => other.ToString() }).Append('\u001e');
        return System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32];
    }
}
