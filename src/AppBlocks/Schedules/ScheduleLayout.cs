using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AppBlocks.DataSources;

namespace AppBlocks.Schedules;

/// <summary>One column of the table: a day or an hour.</summary>
/// <param name="Marker">The first marker that covers the column, whose colour shades the column through every row; null for an unmarked column.</param>
public sealed record ScheduleColumn(int Index, DateTime Start, bool IsWeekend, bool IsToday, ScheduleBar? Marker = null);

/// <summary>A span of the first header row: a month over day columns, a day over hour columns.</summary>
public sealed record ScheduleHeaderGroup(DateTime Start, int Span);

/// <param name="Start">The first column the bar covers, after clamping to the window.</param>
/// <param name="Span">How many columns it covers.</param>
/// <param name="Lane">Its sub-row within the row, from zero.</param>
/// <param name="KindIndex">The one-based position of its kind in the legend; zero for a bar without a kind.</param>
/// <param name="OpenEnded">A bar without an end, which runs to the window's edge.</param>
/// <param name="ClippedStart">The bar began before the window.</param>
/// <param name="ClippedEnd">The bar ends after the window.</param>
/// <param name="Color">The bar's own background colour, or its row's; null leaves the kind's colour. Only a hex value or a colour name gets through.</param>
/// <param name="TextColor">The colour of the bar's label, on the same terms.</param>
/// <param name="Description">What a reader opens from the bar, beside its dates and link; null makes the bar a plain link.</param>
public sealed record ScheduleBar(string Key, string Label, string? Kind, int KindIndex, string? Link, DateTime StartsAt, DateTime? EndsAt,
    int Start, int Span, int Lane, bool OpenEnded, bool ClippedStart, bool ClippedEnd, string? Color = null, string? TextColor = null, string? Description = null);

/// <param name="Lanes">How many sub-rows the row needs, at least one.</param>
/// <param name="Vertical">A row drawn across the top as a band of markers rather than down the side.</param>
/// <param name="Color">The background colour of the row's label, and of its bars without one of their own.</param>
/// <param name="Description">What a reader opens from the row's label; null leaves the label as text.</param>
public sealed record ScheduleRow(string Key, string Label, string? Group, IReadOnlyList<ScheduleBar> Bars, int Lanes,
    bool Vertical = false, string? Color = null, string? TextColor = null, string? Description = null);

/// <summary>Rows gathered under one group heading; the title is null for rows without a group.</summary>
public sealed record ScheduleGroup(string? Title, IReadOnlyList<ScheduleRow> Rows);

public sealed record ScheduleLegendEntry(string Kind, int Index);

/// <summary>
/// The table a schedule renders, computed once from the two query results and the window: the columns and their
/// header groups, the rows under their groups with the bars placed in lanes, the vertical rows as bands of markers
/// across the top, and the legend of kinds. It is the activity schedule's layout in C#: bars are clamped to the
/// window, an open-ended bar runs to its edge, and the bars of one row are packed with <see cref="ScheduleLanes"/>.
/// Nothing here reads a database or renders HTML.
/// </summary>
public sealed class ScheduleLayout
{
    private ScheduleLayout(ScheduleWindow window, IReadOnlyList<ScheduleColumn> columns, IReadOnlyList<ScheduleHeaderGroup> headerGroups,
        IReadOnlyList<ScheduleGroup> groups, IReadOnlyList<ScheduleRow> markers, IReadOnlyList<ScheduleLegendEntry> legend, int rowCount, int barCount, int hiddenRowCount)
    {
        Window = window; Columns = columns; HeaderGroups = headerGroups; Groups = groups; Markers = markers; Legend = legend;
        RowCount = rowCount; BarCount = barCount; HiddenRowCount = hiddenRowCount;
    }

    public ScheduleWindow Window { get; }
    public IReadOnlyList<ScheduleColumn> Columns { get; }
    public IReadOnlyList<ScheduleHeaderGroup> HeaderGroups { get; }
    public IReadOnlyList<ScheduleGroup> Groups { get; }
    /// <summary>The vertical rows, in source order, each a band across the top whose bars are markers.</summary>
    public IReadOnlyList<ScheduleRow> Markers { get; }
    public IReadOnlyList<ScheduleLegendEntry> Legend { get; }
    /// <summary>The rows down the side; vertical rows are counted in <see cref="Markers"/> instead.</summary>
    public int RowCount { get; }
    /// <summary>The bars placed in the window, markers included; bars outside it or on unknown rows are not counted.</summary>
    public int BarCount { get; }
    /// <summary>Rows left out for having no bar in the window, when the settings ask for that.</summary>
    public int HiddenRowCount { get; }
    public int? TodayColumn => Window.TodayColumn;
    public IEnumerable<ScheduleRow> Rows => Groups.SelectMany(group => group.Rows);
    public bool IsEmpty => RowCount == 0 && Markers.Count == 0;

    /// <summary>
    /// Lays the schedule out. Rows keep the order their source returned them, gathered under their group in the order
    /// the groups first appear; a row without a key, or with a key seen before, is left out. A bar needs a start and a
    /// row that exists; one that ends before the window or begins after it is left out, one that crosses an edge is
    /// clamped and marked, and one without an end runs to the window's last column. A bar of a vertical row is a
    /// marker: without an end it covers its start column only, and it shades its columns through every row.
    /// </summary>
    /// <param name="linkTemplate">Where a bar without a <c>link</c> value leads; <c>{key}</c> and the other columns of the bar in braces are replaced, URL-encoded.</param>
    /// <param name="pathBase">Put before a link that starts with <c>/</c>, so templates are written from the site's root.</param>
    /// <param name="hideEmptyRows">Leave out rows, and vertical rows, with no bar in the window.</param>
    public static ScheduleLayout Build(ScheduleWindow window, DataSourceQueryResult rows, DataSourceQueryResult bars, string? linkTemplate = null, string pathBase = "",
        bool hideEmptyRows = false)
    {
        var columns = new ScheduleColumn[window.Columns];
        var today = window.TodayColumn;
        for (var i = 0; i < columns.Length; i++)
        {
            var start = window.ColumnStart(i);
            columns[i] = new(i, start, start.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday, i == today);
        }
        var headerGroups = new List<ScheduleHeaderGroup>();
        foreach (var column in columns)
        {
            var groupStart = window.Scale == ScheduleScale.Days ? new DateTime(column.Start.Year, column.Start.Month, 1) : column.Start.Date;
            if (headerGroups.Count > 0 && headerGroups[^1].Start == groupStart) headerGroups[^1] = headerGroups[^1] with { Span = headerGroups[^1].Span + 1 };
            else headerGroups.Add(new(groupStart, 1));
        }

        var rowKey = ScheduleColumns.Find(rows, "key"); var rowLabel = ScheduleColumns.Find(rows, "label"); var rowGroup = ScheduleColumns.Find(rows, "group");
        var rowOrientation = ScheduleColumns.Find(rows, ScheduleSettings.OrientationColumn);
        var rowColor = ScheduleColumns.Find(rows, ScheduleSettings.ColorColumn); var rowTextColor = ScheduleColumns.Find(rows, ScheduleSettings.TextColorColumn);
        var rowDescription = ScheduleColumns.Find(rows, ScheduleSettings.DescriptionColumn);
        var ordered = new List<ScheduleRow>();
        var known = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Rows)
        {
            var key = ScheduleColumns.Text(row, rowKey);
            if (key.Length == 0 || known.ContainsKey(key)) continue;
            known[key] = ordered.Count;
            var label = ScheduleColumns.Text(row, rowLabel);
            var group = ScheduleColumns.Text(row, rowGroup);
            var description = ScheduleColumns.Text(row, rowDescription);
            ordered.Add(new(key, label.Length == 0 ? key : label, group.Length == 0 ? null : group, [], 1,
                Vertical: ScheduleSettings.ParseFlag(ScheduleColumns.Text(row, rowOrientation)),
                Color: ScheduleColumns.Color(row, rowColor), TextColor: ScheduleColumns.Color(row, rowTextColor),
                Description: description.Length == 0 ? null : description));
        }

        var barKey = ScheduleColumns.Find(bars, "key"); var barRow = ScheduleColumns.Find(bars, "row"); var barStart = ScheduleColumns.Find(bars, "start");
        var barEnd = ScheduleColumns.Find(bars, "end"); var barLabel = ScheduleColumns.Find(bars, "label"); var barKind = ScheduleColumns.Find(bars, "kind");
        var barLink = ScheduleColumns.Find(bars, "link");
        var barColor = ScheduleColumns.Find(bars, ScheduleSettings.ColorColumn); var barTextColor = ScheduleColumns.Find(bars, ScheduleSettings.TextColorColumn);
        var barDescription = ScheduleColumns.Find(bars, ScheduleSettings.DescriptionColumn);
        var placed = new List<(int Row, ScheduleBar Bar)>();
        var kinds = new List<string>();
        var last = columns.Length - 1;
        foreach (var bar in bars.Rows)
        {
            if (!known.TryGetValue(ScheduleColumns.Text(bar, barRow), out var rowIndex)) continue;
            if (ScheduleColumns.Moment(bar, barStart) is not { } startsAt) continue;
            var owner = ordered[rowIndex];
            var endsAt = ScheduleColumns.Moment(bar, barEnd);
            if (endsAt is { } ends && ends < startsAt) endsAt = startsAt;
            var start = window.ColumnOf(startsAt);
            // A marker without an end is one column: a holiday has no "onward".
            var end = endsAt is { } moment ? window.ColumnOf(moment) : owner.Vertical ? start : last;
            if (start > last || end < 0) continue;
            var kind = ScheduleColumns.Text(bar, barKind);
            var kindIndex = 0;
            if (kind.Length > 0)
            {
                kindIndex = kinds.FindIndex(k => k.Equals(kind, StringComparison.OrdinalIgnoreCase)) + 1;
                if (kindIndex == 0) { kinds.Add(kind); kindIndex = kinds.Count; }
            }
            var key = ScheduleColumns.Text(bar, barKey);
            var label = ScheduleColumns.Text(bar, barLabel);
            var link = ScheduleColumns.Text(bar, barLink);
            if (link.Length == 0) link = Link(linkTemplate, bar) ?? "";
            if (link.StartsWith('/') && !link.StartsWith("//", StringComparison.Ordinal)) link = pathBase + link;
            var description = ScheduleColumns.Text(bar, barDescription);
            var first = Math.Max(0, start);
            placed.Add((rowIndex, new(key, label.Length == 0 ? key : label, kind.Length == 0 ? null : kind, kindIndex, link.Length == 0 ? null : link,
                startsAt, endsAt, first, Math.Min(last, end) - first + 1, 0,
                OpenEnded: endsAt is null && !owner.Vertical, ClippedStart: start < 0, ClippedEnd: endsAt is not null && end > last,
                Color: ScheduleColumns.Color(bar, barColor) ?? owner.Color, TextColor: ScheduleColumns.Color(bar, barTextColor) ?? owner.TextColor,
                Description: description.Length == 0 ? null : description)));
        }

        var scheduleRows = new List<ScheduleRow>();
        var hidden = 0;
        for (var index = 0; index < ordered.Count; index++)
        {
            var own = placed.Where(p => p.Row == index).Select(p => p.Bar)
                .OrderBy(b => b.Start).ThenBy(b => b.Start + b.Span).ThenBy(b => b.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (own.Count == 0 && hideEmptyRows) { hidden++; continue; }
            var (lanes, count) = ScheduleLanes.Pack(own.Select(b => (b.Start, b.Start + b.Span - 1)).ToList());
            scheduleRows.Add(ordered[index] with { Bars = own.Select((bar, i) => bar with { Lane = lanes[i] }).ToArray(), Lanes = count });
        }
        var markers = scheduleRows.Where(row => row.Vertical).ToArray();
        foreach (var marker in markers.SelectMany(row => row.Bars))
            for (var i = marker.Start; i < marker.Start + marker.Span; i++)
                if (columns[i].Marker is null) columns[i] = columns[i] with { Marker = marker };

        var groups = new List<(string? Title, List<ScheduleRow> Rows)>();
        var titled = new Dictionary<string, List<ScheduleRow>>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var row in scheduleRows.Where(row => !row.Vertical))
        {
            if (row.Group is null)
            {
                if (groups.Count == 0 || groups[^1].Title is not null) groups.Add((null, []));
                groups[^1].Rows.Add(row);
            }
            else
            {
                if (!titled.TryGetValue(row.Group, out var members)) { titled[row.Group] = members = []; groups.Add((row.Group, members)); }
                members.Add(row);
            }
        }
        return new(window, columns, headerGroups, groups.Select(g => new ScheduleGroup(g.Title, g.Rows)).ToArray(), markers,
            kinds.Select((kind, i) => new ScheduleLegendEntry(kind, i + 1)).ToArray(), scheduleRows.Count - markers.Length, placed.Count, hidden);
    }

    /// <summary>The template with every <c>{name}</c> replaced by the bar's value for that column, URL-encoded; null when there is no template.</summary>
    public static string? Link(string? template, IReadOnlyDictionary<string, object?> bar)
    {
        if (string.IsNullOrWhiteSpace(template)) return null;
        var result = new StringBuilder();
        var text = template.Trim();
        var position = 0;
        while (position < text.Length)
        {
            var open = text.IndexOf('{', position);
            var close = open < 0 ? -1 : text.IndexOf('}', open + 1);
            if (open < 0 || close < 0) { result.Append(text, position, text.Length - position); break; }
            result.Append(text, position, open - position);
            var name = text[(open + 1)..close].Trim();
            var match = bar.Keys.FirstOrDefault(column => column.Equals(name, StringComparison.OrdinalIgnoreCase));
            result.Append(match is null ? "" : Uri.EscapeDataString(ScheduleColumns.Text(bar, match)));
            position = close + 1;
        }
        return result.ToString();
    }
}

/// <summary>How a query result's values are read as the schedule's columns, by column name or by field alias, in any letter case.</summary>
internal static partial class ScheduleColumns
{
    /// <summary>The column the rows carry under the expected name, or null when the result has none.</summary>
    public static string? Find(DataSourceQueryResult result, string expected)
        => (result.Columns.FirstOrDefault(c => c.Name.Equals(expected, StringComparison.OrdinalIgnoreCase))
            ?? result.Columns.FirstOrDefault(c => c.Field.Equals(expected, StringComparison.OrdinalIgnoreCase)))?.Name;

    /// <summary>The columns of <paramref name="expected"/> that <paramref name="available"/> does not offer by name or field alias.</summary>
    public static IReadOnlyList<string> Missing(IEnumerable<string> expected, IEnumerable<string> available)
    {
        var names = new HashSet<string>(available, StringComparer.OrdinalIgnoreCase);
        return expected.Where(name => !names.Contains(name)).ToArray();
    }

    public static string Text(IReadOnlyDictionary<string, object?> row, string? column) => column is null ? "" : Text(row.GetValueOrDefault(column));

    public static string Text(object? value) => value switch {
        null or DBNull => "",
        string text => text.Trim(),
        DateTime moment => moment.TimeOfDay == TimeSpan.Zero ? moment.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : moment.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset moment => Text(moment.LocalDateTime),
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? "",
    };

    /// <summary>
    /// A colour the page can put in a style attribute: <c>#rgb</c> to <c>#rrggbbaa</c>, or a colour name. Anything else,
    /// a function, a URL, a semicolon, is null, so a value from a database row never writes CSS of its own.
    /// </summary>
    public static string? Color(IReadOnlyDictionary<string, object?> row, string? column) => column is null ? null : Color(row.GetValueOrDefault(column));

    public static string? Color(object? value) => Text(value) is { Length: > 0 } text && SafeColor().IsMatch(text) ? text.ToLowerInvariant() : null;

    [GeneratedRegex("^(#([0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})|[a-zA-Z]{3,30})$")]
    private static partial Regex SafeColor();

    /// <summary>A date or a date and time, from the provider's own value or from ISO text as SQLite stores it; null for anything else.</summary>
    public static DateTime? Moment(IReadOnlyDictionary<string, object?> row, string? column) => column is null ? null : Moment(row.GetValueOrDefault(column));

    public static DateTime? Moment(object? value) => value switch {
        DateTime moment => moment,
        DateTimeOffset moment => moment.LocalDateTime,
        DateOnly date => date.ToDateTime(TimeOnly.MinValue),
        string text when DateTime.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var parsed) => parsed,
        _ => null,
    };
}
