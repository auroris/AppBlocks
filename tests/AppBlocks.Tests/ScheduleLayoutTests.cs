using Microsoft.Extensions.Configuration;
using AppBlocks.DataSources;
using AppBlocks.Schedules;

namespace AppBlocks.Tests;

/// <summary>
/// The Schedule block's layout apart from a site: the lane packing, the window from the address or the settings, the
/// bars placed, clamped and grouped, the legend, and the link template.
/// </summary>
public sealed class ScheduleLayoutTests
{
    // Thursday 29 October 2026 in the afternoon; the default window below runs from Tuesday the 27th to Monday 2 November.
    private static readonly DateTime Now = new(2026, 10, 29, 14, 5, 0);
    private static readonly ScheduleSettings Settings = new(null, null, "rows", "bars", 2, 4, ScheduleScale.Days, null);

    [Fact]
    public void Bars_take_the_first_free_lane_so_overlapping_bars_stay_apart()
    {
        // 0-2 and 1-3 overlap; 3-4 fits back into the first lane behind 0-2; 6-6 shares a column with 5-6 and moves down.
        var (lanes, count) = ScheduleLanes.Pack([(0, 2), (1, 3), (3, 4), (5, 6), (6, 6)]);
        Assert.Equal(new[] { 0, 1, 0, 0, 1 }, lanes);
        Assert.Equal(2, count);
        var empty = ScheduleLanes.Pack([]);
        Assert.Empty(empty.Lanes);
        Assert.Equal(1, empty.Count);
        // A span written backwards is one column.
        Assert.Equal(new[] { 0, 1 }, ScheduleLanes.Pack([(4, 2), (4, 4)]).Lanes);
        Assert.Equal(new[] { 0, 0 }, ScheduleLanes.Pack([(4, 2), (5, 5)]).Lanes);
    }

    [Fact]
    public void The_window_comes_from_the_address_or_the_settings_and_never_runs_wild()
    {
        var defaults = ScheduleWindow.Resolve(Settings, null, Now);
        Assert.Equal((new DateOnly(2026, 10, 27), new DateOnly(2026, 11, 2), 7), (defaults.From, defaults.To, defaults.Columns));
        Assert.Equal(2, defaults.TodayColumn);
        var asked = ScheduleWindow.Resolve(Settings, Query(("FROM", "2026-06-01"), ("to", "2026-06-30T10:00:00")), Now);
        Assert.Equal((new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30)), (asked.From, asked.To));
        Assert.Null(asked.TodayColumn);
        // A parameter that is not a date is ignored; an end before the start becomes the start.
        var odd = ScheduleWindow.Resolve(Settings, Query(("from", "yesterday"), ("to", "2026-01-01")), Now);
        Assert.Equal((new DateOnly(2026, 10, 27), new DateOnly(2026, 10, 27), 1), (odd.From, odd.To, odd.Columns));
        var wide = ScheduleWindow.Resolve(Settings, Query(("from", "2020-01-01"), ("to", "2030-01-01")), Now);
        Assert.Equal(ScheduleSettings.MaxDays, wide.Columns);
        var hours = ScheduleWindow.Resolve(Settings with { Scale = ScheduleScale.Hours }, Query(("from", "2026-10-01"), ("to", "2026-12-31")), Now);
        Assert.Equal(ScheduleSettings.MaxHourDays * 24, hours.Columns);
        // The dates shown reach the sources as :PARAM.from and :PARAM.to, beside the page's own parameters.
        var bound = defaults.Parameters(Query(("id", "4")));
        Assert.Equal(("2026-10-27", "2026-11-02", "4"), (bound["from"], bound["TO"], bound["id"]));
        Assert.Equal((new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 9)), (defaults.Shift(1).From, defaults.Shift(1).To));
        Assert.Equal(new DateOnly(2026, 10, 20), defaults.Shift(-1).From);
    }

    [Fact]
    public void Settings_read_their_defaults_and_scale()
    {
        var settings = ScheduleSettings.Read(alias => alias switch {
            ScheduleSettings.RowsSourceAlias => " rooms ", ScheduleSettings.BarsSourceAlias => "stays", ScheduleSettings.DaysBeforeAlias => "",
            ScheduleSettings.DaysAfterAlias => "x", ScheduleSettings.ColumnsAlias => "hours", _ => null,
        });
        Assert.Equal(("rooms", "stays", ScheduleSettings.DefaultDaysBefore, ScheduleSettings.DefaultDaysAfter, ScheduleScale.Hours),
            (settings.RowsSource, settings.BarsSource, settings.DaysBefore, settings.DaysAfter, settings.Scale));
        Assert.Null(settings.LinkTemplate);
        Assert.Equal(ScheduleScale.Days, ScheduleSettings.ParseScale(null));
        Assert.Null(ScheduleSettings.ParseScale("weeks"));
    }

    [Fact]
    public void Bars_are_placed_in_lanes_clamped_to_the_window_and_grouped_with_a_legend()
    {
        var window = ScheduleWindow.Resolve(Settings, null, Now);
        var rows = Result([("KEY", "key"), ("LABEL", "label"), ("GROUP", "group")],
            Row(("KEY", "101"), ("LABEL", "Room 101"), ("GROUP", "Main building")),
            Row(("KEY", "102"), ("LABEL", "Room 102"), ("GROUP", "Main building")),
            Row(("KEY", "201"), ("LABEL", null), ("GROUP", "Annex")),
            Row(("KEY", "101"), ("LABEL", "Repeated key"), ("GROUP", "Annex")),
            Row(("KEY", ""), ("LABEL", "No key"), ("GROUP", null)));
        // The bars' key column is STAY_ID mapped to the field alias key, as a data source's mapping would do.
        var bars = Result([("STAY_ID", "key"), ("ROW", "row"), ("START", "start"), ("END", "end"), ("LABEL", "label"), ("KIND", "kind"), ("LINK", "link")],
            Bar(1, "101", "2026-10-27", "2026-10-28", "Smith", "Stay", null),
            Bar(2, "101", "2026-10-28", "2026-10-30", "Jones", "Stay", null),
            Bar(3, "101", "2026-10-31", null, "Repair", "Repair", "/repairs/3"),
            Bar(4, "102", "2026-10-20", "2026-10-28", "Early", "Stay", null),
            Bar(5, "102", new DateTime(2026, 11, 1, 18, 0, 0), "2026-11-10", "Late", "Block", null),
            Bar(6, "201", "2026-09-01", "2026-09-05", "Gone", "Stay", null),
            Bar(7, "999", "2026-10-29", "2026-10-29", "Lost", "Stay", null),
            Bar(8, "201", "2026-10-29", "2026-10-28", "Backwards", "", null));
        var layout = ScheduleLayout.Build(window, rows, bars, "/stays/?id={key}&room={row}", "/wing");

        Assert.Equal((3, 6), (layout.RowCount, layout.BarCount));
        Assert.Equal(new[] { "Main building", "Annex" }, layout.Groups.Select(g => g.Title));
        var room101 = layout.Groups[0].Rows[0];
        Assert.Equal(("101", "Room 101", 2), (room101.Key, room101.Label, room101.Lanes));
        var smith = room101.Bars.Single(b => b.Label == "Smith");
        Assert.Equal((0, 2, 0), (smith.Start, smith.Span, smith.Lane));
        Assert.Equal("/wing/stays/?id=1&room=101", smith.Link);
        var jones = room101.Bars.Single(b => b.Label == "Jones");
        Assert.Equal((1, 3, 1), (jones.Start, jones.Span, jones.Lane));
        var repair = room101.Bars.Single(b => b.Label == "Repair");
        Assert.True(repair.OpenEnded);
        Assert.False(repair.ClippedEnd);
        Assert.Equal((4, 3, 0), (repair.Start, repair.Span, repair.Lane));
        Assert.Equal("/wing/repairs/3", repair.Link);
        var room102 = layout.Groups[0].Rows[1];
        Assert.Equal(1, room102.Lanes);
        var early = room102.Bars.Single(b => b.Label == "Early");
        Assert.True(early.ClippedStart);
        Assert.Equal((0, 2), (early.Start, early.Span));
        var late = room102.Bars.Single(b => b.Label == "Late");
        Assert.True(late.ClippedEnd);
        Assert.False(late.OpenEnded);
        Assert.Equal((5, 2), (late.Start, late.Span));
        // A row without a label shows its key; a bar whose end precedes its start covers one column and has no kind.
        var annex = Assert.Single(layout.Groups[1].Rows);
        Assert.Equal("201", annex.Label);
        var backwards = Assert.Single(annex.Bars);
        Assert.Equal((2, 1, null, 0), (backwards.Start, backwards.Span, backwards.Kind, backwards.KindIndex));
        Assert.Equal(new[] { "Stay", "Repair", "Block" }, layout.Legend.Select(l => l.Kind));
        Assert.Equal(new[] { 1, 2, 3 }, layout.Legend.Select(l => l.Index));
        Assert.Equal((1, 2, 3), (smith.KindIndex, repair.KindIndex, late.KindIndex));
        // Saturday 31 October and Sunday 1 November are weekends; today is the third column; October has five columns, November two.
        Assert.Equal(new[] { 4, 5 }, layout.Columns.Where(c => c.IsWeekend).Select(c => c.Index));
        Assert.Equal(2, layout.TodayColumn);
        Assert.Single(layout.Columns, c => c.IsToday);
        Assert.Equal(new[] { (10, 5), (11, 2) }, layout.HeaderGroups.Select(g => (g.Start.Month, g.Span)));
    }

    [Fact]
    public void Hour_columns_span_each_day_of_the_window_and_place_bars_by_the_hour()
    {
        var window = ScheduleWindow.Resolve(Settings with { Scale = ScheduleScale.Hours, DaysBefore = 0, DaysAfter = 1 }, null, Now);
        var layout = ScheduleLayout.Build(window,
            Result([("KEY", "key"), ("LABEL", "label"), ("GROUP", "group")], Row(("KEY", "A"), ("LABEL", "Hangar A"), ("GROUP", null))),
            Result([("KEY", "key"), ("ROW", "row"), ("START", "start"), ("END", "end"), ("LABEL", "label"), ("KIND", "kind"), ("LINK", "link")],
                Bar(1, "A", "2026-10-29 09:30:00", "2026-10-29 11:00:00", "Inspection", "", null)));
        Assert.Equal(48, layout.Columns.Count);
        Assert.Equal(new[] { (29, 24), (30, 24) }, layout.HeaderGroups.Select(g => (g.Start.Day, g.Span)));
        Assert.Equal(14, layout.TodayColumn);
        var bar = Assert.Single(Assert.Single(layout.Rows).Bars);
        Assert.Equal((9, 3), (bar.Start, bar.Span));
        Assert.Null(Assert.Single(layout.Groups).Title);
        Assert.Empty(layout.Legend);
    }

    [Theory]
    [InlineData("/orders-form/?id={key}", "/orders-form/?id=4")]
    [InlineData("https://example.test/{KEY}/{ row }", "https://example.test/4/Acme%20Corp")]
    [InlineData("/x?a={missing}&b={label}", "/x?a=&b=Order%204")]
    [InlineData("{key", "{key")]
    public void A_link_template_takes_the_bars_columns_encoded(string template, string expected)
        => Assert.Equal(expected, ScheduleLayout.Link(template, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["KEY"] = 4L, ["ROW"] = "Acme Corp", ["LABEL"] = "Order 4" }));

    [Fact]
    public void Vertical_rows_become_marker_bands_that_shade_their_columns_and_colours_pass_from_rows_to_bars()
    {
        // The default window: Tuesday 27 October to Monday 2 November, seven columns.
        var window = ScheduleWindow.Resolve(Settings, null, Now);
        var rows = Result([("KEY", "key"), ("LABEL", "label"), ("GROUP", "group"), ("ORIENTATION", "orientation"), ("COLOR", "color"), ("TEXTCOLOR", "textColor"), ("DESCRIPTION", "description")],
            Row(("KEY", "holidays"), ("LABEL", "Holidays"), ("GROUP", null), ("ORIENTATION", 1L), ("COLOR", "#B00020"), ("TEXTCOLOR", "#FFF"), ("DESCRIPTION", "Statutory holidays")),
            Row(("KEY", "101"), ("LABEL", "Room 101"), ("GROUP", "Main"), ("ORIENTATION", 0L), ("COLOR", "navy"), ("TEXTCOLOR", "url(evil)"), ("DESCRIPTION", "")),
            Row(("KEY", "102"), ("LABEL", "Room 102"), ("GROUP", "Main"), ("ORIENTATION", "vertical"), ("COLOR", "red; x"), ("TEXTCOLOR", null), ("DESCRIPTION", null)));
        var bars = Result([("KEY", "key"), ("ROW", "row"), ("START", "start"), ("END", "end"), ("LABEL", "label"), ("KIND", "kind"), ("LINK", "link"), ("COLOR", "color"), ("DESCRIPTION", "description")],
            Row(("KEY", 1L), ("ROW", "holidays"), ("START", "2026-10-28"), ("END", null), ("LABEL", "Founders' Day"), ("KIND", "Holiday"), ("LINK", null), ("COLOR", null), ("DESCRIPTION", "Offices closed.")),
            Row(("KEY", 2L), ("ROW", "holidays"), ("START", "2026-10-31"), ("END", "2026-11-01"), ("LABEL", "Long weekend"), ("KIND", "Holiday"), ("LINK", "/x"), ("COLOR", "#123456"), ("DESCRIPTION", null)),
            Row(("KEY", 3L), ("ROW", "holidays"), ("START", "2026-10-28"), ("END", "2026-10-28"), ("LABEL", "Second"), ("KIND", ""), ("LINK", null), ("COLOR", null), ("DESCRIPTION", null)),
            Row(("KEY", 4L), ("ROW", "101"), ("START", "2026-10-27"), ("END", "2026-10-27"), ("LABEL", "Stay"), ("KIND", "Stay"), ("LINK", null), ("COLOR", "#00FF00"), ("DESCRIPTION", "Two nights")),
            Row(("KEY", 5L), ("ROW", "101"), ("START", "2026-10-29"), ("END", null), ("LABEL", "Repair"), ("KIND", "Repair"), ("LINK", null), ("COLOR", null), ("DESCRIPTION", null)));
        var layout = ScheduleLayout.Build(window, rows, bars);

        // Vertical rows sit apart from the groups, in source order, even one with no bar in the window.
        Assert.Equal(new[] { "Holidays", "Room 102" }, layout.Markers.Select(m => m.Label));
        Assert.Equal((1, 5, 0), (layout.RowCount, layout.BarCount, layout.HiddenRowCount));
        Assert.Equal("Main", Assert.Single(layout.Groups).Title);
        Assert.False(layout.IsEmpty);
        var holidays = layout.Markers[0];
        Assert.True(holidays.Vertical);
        Assert.Equal(("#b00020", "#fff", "Statutory holidays", 2), (holidays.Color, holidays.TextColor, holidays.Description, holidays.Lanes));
        // A marker without an end covers its start column only, and takes its row's colours and keeps its description.
        var founders = holidays.Bars.Single(b => b.Label == "Founders' Day");
        Assert.Equal((1, 1, 0, false), (founders.Start, founders.Span, founders.Lane, founders.OpenEnded));
        Assert.Equal(("#b00020", "#fff", "Offices closed."), (founders.Color, founders.TextColor, founders.Description));
        Assert.Equal(1, holidays.Bars.Single(b => b.Label == "Second").Lane);
        var weekend = holidays.Bars.Single(b => b.Label == "Long weekend");
        Assert.Equal((4, 2, "#123456", "#fff"), (weekend.Start, weekend.Span, weekend.Color, weekend.TextColor));
        // A column remembers the first marker over it, so the view can shade it; other columns stay plain.
        Assert.Equal("Founders' Day", layout.Columns[1].Marker?.Label);
        Assert.Equal(new[] { "Long weekend", "Long weekend" }, layout.Columns.Skip(4).Take(2).Select(c => c.Marker?.Label));
        Assert.All(new[] { 0, 2, 3, 6 }, index => Assert.Null(layout.Columns[index].Marker));
        // Only a hex value or a colour name gets through; a bar's own colour wins over its row's.
        var room = Assert.Single(layout.Rows);
        Assert.Equal(("navy", null, null), (room.Color, room.TextColor, room.Description));
        Assert.Null(layout.Markers[1].Color);
        Assert.Equal(("#00ff00", "Two nights"), (room.Bars[0].Color, room.Bars[0].Description));
        var repair = room.Bars[1];
        Assert.Equal(("navy", null, true), (repair.Color, repair.Description, repair.OpenEnded));

        // Hiding empty rows drops Room 102; with no bars at all, nothing is left and the count says why.
        var busy = ScheduleLayout.Build(window, rows, bars, hideEmptyRows: true);
        Assert.Equal("Holidays", Assert.Single(busy.Markers).Label);
        Assert.Equal((1, 1), (busy.RowCount, busy.HiddenRowCount));
        var quiet = ScheduleLayout.Build(window, rows, Result([("KEY", "key"), ("ROW", "row"), ("START", "start")]), hideEmptyRows: true);
        Assert.True(quiet.IsEmpty);
        Assert.Equal(3, quiet.HiddenRowCount);
        Assert.False(ScheduleLayout.Build(window, rows, Result([("KEY", "key"), ("ROW", "row"), ("START", "start")])).IsEmpty);
    }

    [Fact]
    public void Months_and_quarters_to_jump_to_start_from_now_and_keep_to_the_scales_limit()
    {
        var window = ScheduleWindow.Resolve(Settings, null, Now);
        var months = window.Months(3).ToArray();
        Assert.Equal((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)), (months[0].From, months[0].To));
        Assert.Equal((new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 31)), (months[2].From, months[2].To));
        var quarters = window.Quarters(2).ToArray();
        Assert.Equal((new DateOnly(2026, 10, 1), new DateOnly(2026, 12, 31), "Q4 2026"), (quarters[0].From, quarters[0].To, quarters[0].QuarterName));
        Assert.Equal((new DateOnly(2027, 1, 1), new DateOnly(2027, 3, 31), "Q1 2027"), (quarters[1].From, quarters[1].To, quarters[1].QuarterName));
        Assert.True(months[0].SameDates(ScheduleWindow.Resolve(Settings, Query(("from", "2026-10-01"), ("to", "2026-10-31")), Now)));
        Assert.False(window.SameDates(months[0]));
        // Hour columns cut a quarter to a month of days, as the address would be.
        Assert.Equal(ScheduleSettings.MaxHourDays, (window with { Scale = ScheduleScale.Hours }).Quarters(1).Single().Days);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData(" Vertical ", true)]
    [InlineData("yes", true)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_flag_reads_the_ways_a_toggle_or_a_column_writes_it(string? value, bool expected)
    {
        Assert.Equal(expected, ScheduleSettings.ParseFlag(value));
        Assert.Equal(expected, ScheduleSettings.Read(alias => alias == ScheduleSettings.HideEmptyRowsAlias ? value : null).HideEmptyRows);
    }

    [Theory]
    [InlineData("/stays/4", "/stays/4")]
    [InlineData("  /stays/4?x=1:2 ", "/stays/4?x=1:2")]
    [InlineData("/", "/")]
    [InlineData("./stays/4", null)]
    [InlineData("../stays", null)]
    [InlineData("stays/4", null)]
    [InlineData("?id=4", null)]
    [InlineData("#bar", null)]
    [InlineData("https://example.org/a:b", "https://example.org/a:b")]
    [InlineData("HTTP://example.org", "HTTP://example.org")]
    [InlineData("mailto:desk@example.org", "mailto:desk@example.org")]
    [InlineData("tel:+15551234567", "tel:+15551234567")]
    [InlineData("javascript:alert(document.cookie)", null)]
    [InlineData(" JavaScript:alert(1)", null)]
    [InlineData("java\tscript:alert(1)", null)]
    [InlineData("\u0001javascript:alert(1)", null)]
    [InlineData("data:text/html,<script>alert(1)</script>", null)]
    [InlineData("vbscript:msgbox", null)]
    [InlineData("ftp://example.org/file", null)]
    [InlineData("//evil.example/", null)]
    [InlineData("\\\\evil.example/", null)]
    [InlineData("/\\evil.example/", null)]
    [InlineData("/stays/\n4", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void A_bar_link_passes_only_root_paths_and_web_mail_and_phone_addresses(string? value, string? expected)
        => Assert.Equal(expected, ScheduleColumns.Link(value));

    [Fact]
    public void A_refused_link_falls_back_to_the_template_or_to_no_link()
    {
        var window = ScheduleWindow.Resolve(Settings, null, Now);
        var rows = Result([("KEY", "key"), ("LABEL", "label"), ("GROUP", "group")], Row(("KEY", "101"), ("LABEL", "Room 101"), ("GROUP", null)));
        var bars = Result([("STAY_ID", "key"), ("ROW", "row"), ("START", "start"), ("END", "end"), ("LABEL", "label"), ("KIND", "kind"), ("LINK", "link")],
            Bar(1, "101", "2026-10-27", "2026-10-27", "Script", "", "javascript:alert(1)"),
            Bar(2, "101", "2026-10-28", "2026-10-28", "Safe", "", "https://example.org/stays/2"));
        var templated = ScheduleLayout.Build(window, rows, bars, "/stays/?id={key}", "/wing").Rows.Single().Bars;
        Assert.Equal("/wing/stays/?id=1", templated.Single(b => b.Label == "Script").Link);
        Assert.Equal("https://example.org/stays/2", templated.Single(b => b.Label == "Safe").Link);
        Assert.Null(ScheduleLayout.Build(window, rows, bars).Rows.Single().Bars.Single(b => b.Label == "Script").Link);
    }

    [Fact]
    public void The_limits_and_the_weekend_come_from_the_options_with_todays_values_as_defaults()
    {
        var defaults = new ScheduleOptions();
        Assert.Equal((400, 31, 2000, 5000), (defaults.MaxDays, defaults.MaxHourDays, defaults.MaxRows, defaults.MaxBars));
        Assert.Equal(new[] { DayOfWeek.Saturday, DayOfWeek.Sunday }, defaults.Weekend);

        var bound = new ScheduleOptions();
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["AppBlocks:Schedule:MaxDays"] = "60", ["AppBlocks:Schedule:MaxHourDays"] = "7",
            ["AppBlocks:Schedule:WeekendDays:0"] = "Friday", ["AppBlocks:Schedule:WeekendDays:1"] = "Saturday",
        }).Build().GetSection(ScheduleOptions.Section).Bind(bound);
        Assert.Equal(new[] { DayOfWeek.Friday, DayOfWeek.Saturday }, bound.Weekend);

        var wide = ScheduleWindow.Resolve(Settings with { DaysBefore = 100, DaysAfter = 100 }, Query(("from", "2020-01-01"), ("to", "2030-01-01")), Now, bound);
        Assert.Equal(60, wide.Columns);
        Assert.Equal(7 * 24, ScheduleWindow.Resolve(Settings with { Scale = ScheduleScale.Hours }, Query(("from", "2026-10-01"), ("to", "2026-12-31")), Now, bound).Columns);
        Assert.Equal(new DateOnly(2026, 10, 29).AddDays(-60), ScheduleWindow.Resolve(Settings with { DaysBefore = 100 }, null, Now, bound).From);
        Assert.Equal(60, ScheduleWindow.Resolve(Settings, null, Now).Quarters(1, bound).Single().Days);
        Assert.Contains(ScheduleValidation.Validate(Settings with { DaysBefore = 61 }, null, null, bound), e => e.Contains("from 0 to 60"));
        Assert.DoesNotContain(ScheduleValidation.Validate(Settings with { DaysBefore = 61 }, null), e => e.Contains("days before"));

        // Friday 30 and Saturday 31 October are the weekend of the default window under these options.
        var rows = Result([("KEY", "key"), ("LABEL", "label"), ("GROUP", "group")], Row(("KEY", "101"), ("LABEL", "Room 101"), ("GROUP", null)));
        var layout = ScheduleLayout.Build(ScheduleWindow.Resolve(Settings, null, Now), rows, Result([("KEY", "key"), ("ROW", "row"), ("START", "start")]), weekend: bound.Weekend);
        Assert.Equal(new[] { 3, 4 }, layout.Columns.Where(c => c.IsWeekend).Select(c => c.Index));
    }

    private static Dictionary<string, string> Query(params (string Name, string Value)[] values)
        => values.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase);

    private static DataSourceQueryResult Result((string Column, string Field)[] columns, params Dictionary<string, object?>[] rows)
        => new(columns.Select(c => new DataSourceQueryColumn(c.Column, c.Field, "text")).ToArray(), rows);

    private static Dictionary<string, object?> Row(params (string Column, object? Value)[] values)
        => values.ToDictionary(v => v.Column, v => v.Value, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, object?> Bar(long key, string row, object? start, object? end, string label, string kind, string? link)
        => Row(("STAY_ID", key), ("KEY", key), ("ROW", row), ("START", start), ("END", end), ("LABEL", label), ("KIND", kind), ("LINK", link));
}
