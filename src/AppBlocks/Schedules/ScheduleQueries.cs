using System.Security.Claims;
using AppBlocks.DataSources;
using AppBlocks.Forms;

namespace AppBlocks.Schedules;

/// <summary>A schedule's source resolved to the data source it reads: the alias an editor wrote and the definition to query.</summary>
/// <param name="FromReport">True when the alias named a published report definition rather than the data source itself.</param>
public sealed record ScheduleSource(string Alias, DataSourceDefinition Definition, bool FromReport);

/// <summary>
/// Resolves the aliases a Schedule block names. A source is a published report definition that selects a data source,
/// or a data source; both are read through the data source engine, so the source's read groups, row restriction and
/// policies decide what a person sees. A report definition with inline SQL is not accepted: its own authorization lives
/// in the report, which this path would not apply.
/// </summary>
public interface IScheduleSourceProvider
{
    bool TryResolve(string alias, out ScheduleSource? source);
    IReadOnlyList<string>? Columns(ScheduleSource source);
}

public sealed class ScheduleSources(PublishedDataCatalog catalog, DataSourceRuntime runtime, IEnumerable<IScheduleSourceProvider>? providers = null)
{
    public ScheduleSource? Resolve(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias)) return null;
        alias = alias.Trim();
        foreach (var provider in providers ?? []) if (provider.TryResolve(alias, out var source)) return source;
        return catalog.ResolveSource(alias) is { } definition ? new(alias, definition, FromReport: false) : null;
    }

    /// <summary>
    /// The names a source's rows carry, column names and field aliases, read from the schema: the catalogue's columns for a
    /// report definition, the data source's plan otherwise. Null when the schema cannot be read right now.
    /// </summary>
    public IReadOnlyList<string>? Columns(ScheduleSource source)
    {
        foreach (var provider in providers ?? []) if (provider.Columns(source) is { } columns) return columns;
        var plan = runtime.Plan(source.Definition).Plan;
        return plan?.Columns.SelectMany(column => new[] { column.Column, column.Field }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>What publishing asks about an alias: null for no such source, a schema without columns when it cannot be read.</summary>
    public ScheduleValidation.SourceSchema? Schema(string alias) => Resolve(alias) is { } source ? new(Columns(source)) : null;
}

/// <summary>Both query results, or the short message the block shows instead of them.</summary>
public sealed record ScheduleData(DataSourceQueryResult? Rows, DataSourceQueryResult? Bars, string? Problem)
{
    public static ScheduleData Failed(string problem) => new(null, null, problem);
}

/// <summary>
/// Reads a schedule's rows and bars for the signed-in person, through <see cref="DataSourceQueries"/> like a list of
/// values or a paper report. A source that cannot be read becomes a message, never a failed page.
/// </summary>
public sealed class ScheduleQueries(ScheduleSources sources, DataSourceQueries queries, ILogger<ScheduleQueries> logger)
{
    /// <summary>Rows beyond this are a problem the editor must narrow, not a slower page.</summary>
    public const int MaxRows = 2000;
    public const int MaxBars = 5000;

    public async Task<ScheduleData> LoadAsync(ScheduleSettings settings, ScheduleWindow window, ClaimsPrincipal person, Guid page, Guid block,
        IReadOnlyDictionary<string, string>? parameters, CancellationToken ct)
    {
        if (sources.Resolve(settings.RowsSource) is not { } rows) return ScheduleData.Failed(Unknown("rows", settings.RowsSource));
        if (sources.Resolve(settings.BarsSource) is not { } bars) return ScheduleData.Failed(Unknown("bars", settings.BarsSource));
        var bound = window.Parameters(parameters);
        try
        {
            var rowData = await queries.QueryAsync(rows.Definition, Context(rows), MaxRows, ct);
            var barData = await queries.QueryAsync(bars.Definition, Context(bars), MaxBars, ct);
            return new(rowData, barData, null);
        }
        catch (UnauthorizedAccessException) { return ScheduleData.Failed("You do not have access to this schedule's data."); }
        catch (DataSourceException error) { return ScheduleData.Failed("The schedule's data is not available. " + error.Message); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "Schedule block {Block} on page {Page} could not read {Rows} and {Bars}.", block, page, settings.RowsSource, settings.BarsSource);
            return ScheduleData.Failed("The schedule's data could not be read.");
        }

        FormExecutionContext Context(ScheduleSource source) => new(person, page, block, source.Definition.ComputeFingerprint(), Parameters: bound);
    }

    private static string Unknown(string role, string alias) => alias.Length == 0
        ? $"This schedule has no {role} source."
        : $"The schedule's {role} source '{alias}' is not a published report definition with a data source or a published data source.";
}

/// <summary>
/// Publish-time checks of a Schedule block, apart from Umbraco: the sources exist and carry the expected columns, and
/// the settings are within range. <c>columnsOf</c> answers for an alias with the source's column names, with a schema
/// whose columns are null when it cannot be read, or null when the alias names no source; a null function, as in a
/// validator without a catalogue, skips the source checks.
/// </summary>
public static class ScheduleValidation
{
    public sealed record SourceSchema(IReadOnlyList<string>? Columns);

    public static IReadOnlyList<string> Validate(ScheduleSettings settings, Func<string, SourceSchema?>? columnsOf, string? scaleText = null)
    {
        var errors = new List<string>();
        Source("rows", settings.RowsSource, ScheduleSettings.RowColumns);
        Source("bars", settings.BarsSource, ScheduleSettings.BarColumns);
        if (settings.DaysBefore < 0 || settings.DaysBefore > ScheduleSettings.MaxDays || settings.DaysAfter < 0 || settings.DaysAfter > ScheduleSettings.MaxDays)
            errors.Add($"The schedule's days before and after today must be whole numbers from 0 to {ScheduleSettings.MaxDays}.");
        if (ScheduleSettings.ParseScale(scaleText) is null) errors.Add($"The schedule's columns must be {ScheduleSettings.DaysOption} or {ScheduleSettings.HoursOption}.");
        return errors;

        void Source(string role, string alias, string[] expected)
        {
            if (alias.Length == 0) { errors.Add($"Select the schedule's {role} source: a published report definition that selects a data source, or a published data source."); return; }
            if (columnsOf is null) return;
            var schema = columnsOf(alias);
            if (schema is null) { errors.Add($"The schedule's {role} source '{alias}' is not a published report definition with a data source or a published data source."); return; }
            if (schema.Columns is null) { errors.Add($"The schedule's {role} source '{alias}' cannot read its database schema."); return; }
            var missing = ScheduleColumns.Missing(expected, schema.Columns);
            if (missing.Count > 0)
                errors.Add($"The schedule's {role} source '{alias}' has no column {string.Join(", ", missing)}; its rows need {Join(expected)}, in any letter case.");
        }
    }

    private static string Join(string[] names) => string.Join(", ", names[..^1]) + " and " + names[^1];
}

public static class ScheduleExtensions
{
    /// <summary>The Schedule block's services: the source resolver publishing and rendering share, and the two-query read.</summary>
    public static IUmbracoBuilder AddSchedules(this IUmbracoBuilder builder)
    {
        builder.Services.AddScoped<ScheduleSources>();
        builder.Services.AddScoped<ScheduleQueries>();
        return builder;
    }
}
