using System.Globalization;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Extensions;

namespace AppBlocks.Schedules;

/// <summary>Whether a column of the schedule is a day or an hour.</summary>
public enum ScheduleScale { Days, Hours }

/// <summary>
/// What an editor sets on a Schedule block: where the rows and the bars come from, how wide the window is when the
/// page's address does not say, the scale of its columns, how a bar links to its record, and whether rows without a
/// bar in the window are shown.
/// </summary>
/// <param name="RowsSource">The alias of a published report definition that selects a data source, or of a data source, whose rows are the schedule's rows.</param>
/// <param name="BarsSource">The alias of the source whose rows are the bars.</param>
/// <param name="DaysBefore">Days before today the window starts when the address carries no <c>from</c>.</param>
/// <param name="DaysAfter">Days after today the window ends when the address carries no <c>to</c>.</param>
/// <param name="LinkTemplate">Where a bar without a <c>link</c> value leads, with <c>{key}</c> and the other bar columns in braces; null leaves such bars without a link.</param>
/// <param name="HideEmptyRows">Leave out rows with no bar in the window, so a long list of resources shows only the busy ones.</param>
public sealed record ScheduleSettings(string? Heading, string? Help, string RowsSource, string BarsSource, int DaysBefore, int DaysAfter,
    ScheduleScale Scale, string? LinkTemplate, bool HideEmptyRows = false)
{
    public const string HeadingAlias = "heading", HelpAlias = "helpText", RowsSourceAlias = "rowsSource", BarsSourceAlias = "barsSource",
        DaysBeforeAlias = "daysBefore", DaysAfterAlias = "daysAfter", ColumnsAlias = "columns", LinkTemplateAlias = "linkTemplate", HideEmptyRowsAlias = "hideEmptyRows";
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

    /// <summary>
    /// A column either source may also carry: a background and a text colour for the row's label and its bars, and a
    /// description a reader opens from a bar or a row label. A bar without colours takes its row's.
    /// </summary>
    public const string ColorColumn = "color", TextColorColumn = "textColor", DescriptionColumn = "description";

    /// <summary>
    /// A column a rows source may carry. A row whose value is true, 1 or vertical is drawn across the top of the schedule
    /// instead of down its side: each of its bars is a marker, written top to bottom in a band and shading its columns
    /// through every row below, as a statutory holiday shades the day for everyone.
    /// </summary>
    public const string OrientationColumn = "orientation";

    public static ScheduleSettings Read(IPublishedElement block)
    {
        string? Text(string alias) => block.Value<string>(alias) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        return Read(alias => alias switch {
            DaysBeforeAlias or DaysAfterAlias => block.Value<int?>(alias)?.ToString(CultureInfo.InvariantCulture),
            HideEmptyRowsAlias => block.Value<bool>(alias) ? "true" : "false",
            _ => Text(alias),
        });
    }

    /// <summary>Reads the settings from their text values, as publishing sees them; a day count that is not a number keeps its default.</summary>
    public static ScheduleSettings Read(Func<string, string?> text)
    {
        string? Clean(string alias) => text(alias) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        int Days(string alias, int fallback) => int.TryParse(Clean(alias), NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) ? days : fallback;
        return new(Clean(HeadingAlias), Clean(HelpAlias), Clean(RowsSourceAlias) ?? "", Clean(BarsSourceAlias) ?? "",
            Days(DaysBeforeAlias, DefaultDaysBefore), Days(DaysAfterAlias, DefaultDaysAfter), ParseScale(Clean(ColumnsAlias)) ?? ScheduleScale.Days, Clean(LinkTemplateAlias),
            ParseFlag(Clean(HideEmptyRowsAlias)));
    }

    /// <summary>The scale an editor picked; empty is days, and anything else is null so publishing can say so.</summary>
    public static ScheduleScale? ParseScale(string? value) => value?.Trim() switch {
        null or "" => ScheduleScale.Days,
        var text when text.Equals(DaysOption, StringComparison.OrdinalIgnoreCase) => ScheduleScale.Days,
        var text when text.Equals(HoursOption, StringComparison.OrdinalIgnoreCase) => ScheduleScale.Hours,
        _ => null,
    };

    /// <summary>A yes as a toggle, a number column or a word writes it: true, 1, yes, on, or for a row's orientation, vertical.</summary>
    public static bool ParseFlag(string? value) => value?.Trim().ToLowerInvariant() is "true" or "1" or "yes" or "y" or "on" or "vertical" or "v";
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
        return Bounded(from, to, settings.Scale, now);
    }

    /// <summary>A window over the dates given, with the end pulled back to the start and to the scale's limit as <see cref="Resolve"/> does.</summary>
    public static ScheduleWindow Bounded(DateOnly from, DateOnly to, ScheduleScale scale, DateTime now)
    {
        if (to < from) to = from;
        var limit = scale == ScheduleScale.Hours ? ScheduleSettings.MaxHourDays : ScheduleSettings.MaxDays;
        if (to.DayNumber - from.DayNumber + 1 > limit) to = from.AddDays(limit - 1);
        return new(from, to, scale, now);
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

    /// <summary>Whether this window shows exactly the dates of another, whatever else they differ in.</summary>
    public bool SameDates(ScheduleWindow other) => From == other.From && To == other.To;

    /// <summary>Whole calendar months a reader can jump to: this month and the ones after it, <paramref name="count"/> in all.</summary>
    public IEnumerable<ScheduleWindow> Months(int count)
    {
        var first = new DateOnly(Now.Year, Now.Month, 1);
        for (var i = 0; i < count; i++)
        {
            var from = first.AddMonths(i);
            yield return Bounded(from, from.AddMonths(1).AddDays(-1), Scale, Now);
        }
    }

    /// <summary>Whole calendar quarters a reader can jump to: this quarter and the ones after it, <paramref name="count"/> in all.</summary>
    public IEnumerable<ScheduleWindow> Quarters(int count)
    {
        var first = new DateOnly(Now.Year, (Now.Month - 1) / 3 * 3 + 1, 1);
        for (var i = 0; i < count; i++)
        {
            var from = first.AddMonths(3 * i);
            yield return Bounded(from, from.AddMonths(3).AddDays(-1), Scale, Now);
        }
    }

    /// <summary>The quarter a window starts in, as <c>Q4 2026</c>.</summary>
    public string QuarterName => $"Q{(From.Month - 1) / 3 + 1} {From.Year}";

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
