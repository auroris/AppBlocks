using System.Globalization;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Extensions;

namespace AppBlocks.Schedules;

/// <summary>Whether a column of the schedule is a day or an hour.</summary>
public enum ScheduleScale { Days, Hours }

/// <summary>
/// What an editor sets on a Schedule block: where the rows and the bars come from, how wide the window is when the
/// page's address does not say, the scale of its columns and how a bar links to its record.
/// </summary>
/// <param name="RowsSource">The alias of a published report definition that selects a data source, or of a data source, whose rows are the schedule's rows.</param>
/// <param name="BarsSource">The alias of the source whose rows are the bars.</param>
/// <param name="DaysBefore">Days before today the window starts when the address carries no <c>from</c>.</param>
/// <param name="DaysAfter">Days after today the window ends when the address carries no <c>to</c>.</param>
/// <param name="LinkTemplate">Where a bar without a <c>link</c> value leads, with <c>{key}</c> and the other bar columns in braces; null leaves such bars without a link.</param>
public sealed record ScheduleSettings(string? Heading, string? Help, string RowsSource, string BarsSource, int DaysBefore, int DaysAfter,
    ScheduleScale Scale, string? LinkTemplate)
{
    public const string HeadingAlias = "heading", HelpAlias = "helpText", RowsSourceAlias = "rowsSource", BarsSourceAlias = "barsSource",
        DaysBeforeAlias = "daysBefore", DaysAfterAlias = "daysAfter", ColumnsAlias = "columns", LinkTemplateAlias = "linkTemplate";
    public const string DaysOption = "Days", HoursOption = "Hours";
    public const int DefaultDaysBefore = 7, DefaultDaysAfter = 14;
    /// <summary>The most days a window spans, whatever the address or the settings ask for.</summary>
    public const int MaxDays = 400;
    /// <summary>The most days a window of hour columns spans: a month is 744 columns.</summary>
    public const int MaxHourDays = 31;

    /// <summary>The columns a rows source must have, in any letter case, by column name or by field alias.</summary>
    public static readonly string[] RowColumns = ["key", "label", "group"];
    /// <summary>The columns a bars source must have.</summary>
    public static readonly string[] BarColumns = ["key", "row", "start", "end", "label", "kind", "link"];

    public static ScheduleSettings Read(IPublishedElement block)
    {
        string? Text(string alias) => block.Value<string>(alias) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        return Read(alias => alias is DaysBeforeAlias or DaysAfterAlias
            ? block.Value<int?>(alias)?.ToString(CultureInfo.InvariantCulture)
            : Text(alias));
    }

    /// <summary>Reads the settings from their text values, as publishing sees them; a day count that is not a number keeps its default.</summary>
    public static ScheduleSettings Read(Func<string, string?> text)
    {
        string? Clean(string alias) => text(alias) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        int Days(string alias, int fallback) => int.TryParse(Clean(alias), NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) ? days : fallback;
        return new(Clean(HeadingAlias), Clean(HelpAlias), Clean(RowsSourceAlias) ?? "", Clean(BarsSourceAlias) ?? "",
            Days(DaysBeforeAlias, DefaultDaysBefore), Days(DaysAfterAlias, DefaultDaysAfter), ParseScale(Clean(ColumnsAlias)) ?? ScheduleScale.Days, Clean(LinkTemplateAlias));
    }

    /// <summary>The scale an editor picked; empty is days, and anything else is null so publishing can say so.</summary>
    public static ScheduleScale? ParseScale(string? value) => value?.Trim() switch {
        null or "" => ScheduleScale.Days,
        var text when text.Equals(DaysOption, StringComparison.OrdinalIgnoreCase) => ScheduleScale.Days,
        var text when text.Equals(HoursOption, StringComparison.OrdinalIgnoreCase) => ScheduleScale.Hours,
        _ => null,
    };
}

/// <summary>
/// The dates a schedule shows: from the page's <c>from</c> and <c>to</c> parameters, which <c>:PARAM.from</c> and
/// <c>:PARAM.to</c> also read, or else the block's days before and after today. Both ends are inclusive dates; a
/// window of hour columns runs from the first day's midnight to the end of the last day.
/// </summary>
public sealed record ScheduleWindow(DateOnly From, DateOnly To, ScheduleScale Scale, DateTime Now)
{
    public const string FromParameter = "from", ToParameter = "to";

    /// <summary>
    /// Resolves the window for a request. A parameter that is not a date is ignored; an end before the start becomes the
    /// start; a window longer than the scale allows is cut at its end, so an address can never ask for an unbounded table.
    /// </summary>
    public static ScheduleWindow Resolve(ScheduleSettings settings, IReadOnlyDictionary<string, string>? parameters, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        var from = ParseDate(parameters?.GetValueOrDefault(FromParameter)) ?? today.AddDays(-Math.Clamp(settings.DaysBefore, 0, ScheduleSettings.MaxDays));
        var to = ParseDate(parameters?.GetValueOrDefault(ToParameter)) ?? today.AddDays(Math.Clamp(settings.DaysAfter, 0, ScheduleSettings.MaxDays));
        if (to < from) to = from;
        var limit = settings.Scale == ScheduleScale.Hours ? ScheduleSettings.MaxHourDays : ScheduleSettings.MaxDays;
        if (to.DayNumber - from.DayNumber + 1 > limit) to = from.AddDays(limit - 1);
        return new(from, to, settings.Scale, now);
    }

    /// <summary>A date written as <c>yyyy-MM-dd</c>, or a date and time whose date is taken; null for anything else.</summary>
    public static DateOnly? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
        return DateTime.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var moment)
            ? DateOnly.FromDateTime(moment) : null;
    }

    public int Days => To.DayNumber - From.DayNumber + 1;
    public int Columns => Scale == ScheduleScale.Days ? Days : Days * 24;
    public DateTime Start => From.ToDateTime(TimeOnly.MinValue);
    /// <summary>The first moment after the window.</summary>
    public DateTime End => To.AddDays(1).ToDateTime(TimeOnly.MinValue);

    /// <summary>The column a moment falls in, which is below zero before the window and at least <see cref="Columns"/> after it.</summary>
    public int ColumnOf(DateTime moment) => Scale == ScheduleScale.Days
        ? (moment.Date - Start).Days
        : (int)Math.Floor((moment - Start).TotalHours);

    public DateTime ColumnStart(int index) => Scale == ScheduleScale.Days ? Start.AddDays(index) : Start.AddHours(index);

    /// <summary>The column of the current day or hour, or null when today is outside the window.</summary>
    public int? TodayColumn => ColumnOf(Now) is var column && column >= 0 && column < Columns ? column : null;

    /// <summary>The same length of time, moved by whole windows: -1 is the window before this one.</summary>
    public ScheduleWindow Shift(int windows) => new(From.AddDays(windows * Days), To.AddDays(windows * Days), Scale, Now);

    /// <summary>
    /// The page's parameters with <c>from</c> and <c>to</c> filled in from the resolved window, so a source's
    /// <c>:PARAM.from</c> and <c>:PARAM.to</c> see the dates shown even when the address left them to the defaults.
    /// </summary>
    public IReadOnlyDictionary<string, string> Parameters(IReadOnlyDictionary<string, string>? parameters)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (parameters is not null) foreach (var (name, value) in parameters) result[name] = value;
        result[FromParameter] = From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        result[ToParameter] = To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return result;
    }
}
