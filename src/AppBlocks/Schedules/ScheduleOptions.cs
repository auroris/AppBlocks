namespace AppBlocks.Schedules;

/// <summary>
/// The <c>AppBlocks:Schedule</c> configuration section: how large a schedule may grow and which days it shades as the
/// weekend. Every value has a default, so a site configures only what it changes.
/// </summary>
public sealed class ScheduleOptions
{
    public const string Section = "AppBlocks:Schedule";

    /// <summary>The defaults, for callers without the configuration.</summary>
    public static readonly ScheduleOptions Default = new();

    private static readonly DayOfWeek[] SaturdayAndSunday = [DayOfWeek.Saturday, DayOfWeek.Sunday];

    /// <summary>The most days a window of day columns spans, whatever the address or the block asks for. 400 unless configured.</summary>
    public int MaxDays { get; set; } = ScheduleSettings.MaxDays;

    /// <summary>The most days a window of hour columns spans: 31 days are 744 columns. 31 unless configured.</summary>
    public int MaxHourDays { get; set; } = ScheduleSettings.MaxHourDays;

    /// <summary>The most rows a schedule reads; beyond it the block reports a problem the editor must narrow. 2000 unless configured.</summary>
    public int MaxRows { get; set; } = ScheduleQueries.MaxRows;

    /// <summary>The most bars a schedule reads. 5000 unless configured.</summary>
    public int MaxBars { get; set; } = ScheduleQueries.MaxBars;

    /// <summary>
    /// The days shaded as the weekend, such as <c>["Friday", "Saturday"]</c>. Unset is Saturday and Sunday. The property
    /// starts empty because the configuration binder adds to a list it finds rather than replacing it.
    /// </summary>
    public List<DayOfWeek> WeekendDays { get; set; } = [];

    /// <summary>The days shaded as the weekend: <see cref="WeekendDays"/>, or Saturday and Sunday when it is empty.</summary>
    public IReadOnlyCollection<DayOfWeek> Weekend => WeekendDays.Count > 0 ? WeekendDays : SaturdayAndSunday;
}
