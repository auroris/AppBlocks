using System.Globalization;
using System.Text;
using UmbracoAppHost.DataSources;

namespace UmbracoAppHost.Schedules;

/// <summary>One column of the table: a day or an hour.</summary>
public sealed record ScheduleColumn(int Index, DateTime Start, bool IsWeekend, bool IsToday);

/// <summary>A span of the first header row: a month over day columns, a day over hour columns.</summary>
public sealed record ScheduleHeaderGroup(DateTime Start, int Span);

/// <param name="Start">The first column the bar covers, after clamping to the window.</param>
/// <param name="Span">How many columns it covers.</param>
/// <param name="Lane">Its sub-row within the row, from zero.</param>
/// <param name="KindIndex">The one-based position of its kind in the legend; zero for a bar without a kind.</param>
/// <param name="OpenEnded">A bar without an end, which runs to the window's edge.</param>
/// <param name="ClippedStart">The bar began before the window.</param>
/// <param name="ClippedEnd">The bar ends after the window.</param>
public sealed record ScheduleBar(string Key, string Label, string? Kind, int KindIndex, string? Link, DateTime StartsAt, DateTime? EndsAt,
    int Start, int Span, int Lane, bool OpenEnded, bool ClippedStart, bool ClippedEnd);

/// <param name="Lanes">How many sub-rows the row needs, at least one.</param>
public sealed record ScheduleRow(string Key, string Label, string? Group, IReadOnlyList<ScheduleBar> Bars, int Lanes);

/// <summary>Rows gathered under one group heading; the title is null for rows without a group.</summary>
public sealed record ScheduleGroup(string? Title, IReadOnlyList<ScheduleRow> Rows);

public sealed record ScheduleLegendEntry(string Kind, int Index);

/// <summary>
/// The table a schedule renders, computed once from the two query results and the window: the columns and their
/// header groups, the rows under their groups with the bars placed in lanes, and the legend of kinds. It is the
/// activity schedule's layout in C#: bars are clamped to the window, an open-ended bar runs to its edge, and the
/// bars of one row are packed with <see cref="ScheduleLanes"/>. Nothing here reads a database or renders HTML.
/// </summary>
public sealed class ScheduleLayout
{
    private ScheduleLayout(ScheduleWindow window, IReadOnlyList<ScheduleColumn> columns, IReadOnlyList<ScheduleHeaderGroup> headerGroups,
        IReadOnlyList<ScheduleGroup> groups, IReadOnlyList<ScheduleLegendEntry> legend, int rowCount, int barCount)
    {
        Window = window; Columns = columns; HeaderGroups = headerGroups; Groups = groups; Legend = legend; RowCount = rowCount; BarCount = barCount;
    }

    public ScheduleWindow Window { get; }
    public IReadOnlyList<ScheduleColumn> Columns { get; }
    public IReadOnlyList<ScheduleHeaderGroup> HeaderGroups { get; }
    public IReadOnlyList<ScheduleGroup> Groups { get; }
    public IReadOnlyList<ScheduleLegendEntry> Legend { get; }
    public int RowCount { get; }
    /// <summary>The bars placed in the window; bars outside it or on unknown rows are not counted.</summary>
    public int BarCount { get; }
    public int? TodayColumn => Window.TodayColumn;
    public IEnumerable<ScheduleRow> Rows => Groups.SelectMany(group => group.Rows);

    /// <summary>
    /// Lays the schedule out. Rows keep the order their source returned them, gathered under their group in the order
    /// the groups first appear; a row without a key, or with a key seen before, is left out. A bar needs a start and a
    /// row that exists; one that ends before the window or begins after it is left out, one that crosses an edge is
    /// clamped and marked, and one without an end runs to the window's last column.
    /// </summary>
    /// <param name="linkTemplate">Where a bar without a <c>link</c> value leads; <c>{key}</c> and the other columns of the bar in braces are replaced, URL-encoded.</param>
    /// <param name="pathBase">Put before a link that starts with <c>/</c>, so templates are written from the site's root.</param>
    public static ScheduleLayout Build(ScheduleWindow window, DataSourceQueryResult rows, DataSourceQueryResult bars, string? linkTemplate = null, string pathBase = "")
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
        var ordered = new List<(string Key, string Label, string? Group)>();
        var known = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Rows)
        {
            var key = ScheduleColumns.Text(row, rowKey);
            if (key.Length == 0 || known.ContainsKey(key)) continue;
            known[key] = ordered.Count;
            var label = ScheduleColumns.Text(row, rowLabel);
            var group = ScheduleColumns.Text(row, rowGroup);
            ordered.Add((key, label.Length == 0 ? key : label, group.Length == 0 ? null : group));
        }

        var barKey = ScheduleColumns.Find(bars, "key"); var barRow = ScheduleColumns.Find(bars, "row"); var barStart = ScheduleColumns.Find(bars, "start");
        var barEnd = ScheduleColumns.Find(bars, "end"); var barLabel = ScheduleColumns.Find(bars, "label"); var barKind = ScheduleColumns.Find(bars, "kind");
        var barLink = ScheduleColumns.Find(bars, "link");
        var placed = new List<(int Row, ScheduleBar Bar)>();
        var kinds = new List<string>();
        var last = columns.Length - 1;
        foreach (var bar in bars.Rows)
        {
            if (!known.TryGetValue(ScheduleColumns.Text(bar, barRow), out var rowIndex)) continue;
            if (ScheduleColumns.Moment(bar, barStart) is not { } startsAt) continue;
            var endsAt = ScheduleColumns.Moment(bar, barEnd);
            if (endsAt is { } ends && ends < startsAt) endsAt = startsAt;
            var start = window.ColumnOf(startsAt);
            var end = endsAt is { } moment ? window.ColumnOf(moment) : last;
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
            var first = Math.Max(0, start);
            placed.Add((rowIndex, new(key, label.Length == 0 ? key : label, kind.Length == 0 ? null : kind, kindIndex, link.Length == 0 ? null : link,
                startsAt, endsAt, first, Math.Min(last, end) - first + 1, 0,
                OpenEnded: endsAt is null, ClippedStart: start < 0, ClippedEnd: endsAt is not null && end > last)));
        }

        var scheduleRows = new ScheduleRow[ordered.Count];
        for (var index = 0; index < ordered.Count; index++)
        {
            var (key, label, group) = ordered[index];
            var own = placed.Where(p => p.Row == index).Select(p => p.Bar)
                .OrderBy(b => b.Start).ThenBy(b => b.Start + b.Span).ThenBy(b => b.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
            var (lanes, count) = ScheduleLanes.Pack(own.Select(b => (b.Start, b.Start + b.Span - 1)).ToList());
            scheduleRows[index] = new(key, label, group, own.Select((bar, i) => bar with { Lane = lanes[i] }).ToArray(), count);
        }
        var groups = new List<(string? Title, List<ScheduleRow> Rows)>();
        var titled = new Dictionary<string, List<ScheduleRow>>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var row in scheduleRows)
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
        return new(window, columns, headerGroups, groups.Select(g => new ScheduleGroup(g.Title, g.Rows)).ToArray(),
            kinds.Select((kind, i) => new ScheduleLegendEntry(kind, i + 1)).ToArray(), ordered.Count, placed.Count);
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
internal static class ScheduleColumns
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
