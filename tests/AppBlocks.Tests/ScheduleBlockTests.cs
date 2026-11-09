using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using AppBlocks.Blocks;
using AppBlocks.Forms;
using AppBlocks.Schedules;

namespace AppBlocks.Tests;

/// <summary>
/// Publishing a Schedule block: its sources must exist and carry the expected columns, its settings must be in range,
/// and the block is accepted at the page level, in content areas and inside forms. The catalogue's part is stood in for
/// by a dictionary of schemas.
/// </summary>
public sealed class ScheduleBlockTests
{
    private static ScheduleSettings Settings(string rows = "rooms", string bars = "stays") => new(null, null, rows, bars, 7, 14, ScheduleScale.Days, null);

    // Column names and field aliases, as the resolver lists them for a data source; a report definition lists column names only.
    private static readonly Dictionary<string, ScheduleValidation.SourceSchema?> Catalogue = new(StringComparer.OrdinalIgnoreCase)
    {
        ["rooms"] = new(["ROOM_NO", "key", "ROOM_NAME", "label", "BUILDING", "group"]),
        ["stays"] = new(["KEY", "ROW", "START", "END", "LABEL", "KIND", "LINK"]),
        ["repairs"] = new(["KEY", "ROW", "START", "LABEL"]),
        ["offline"] = new(null),
    };
    private static ScheduleValidation.SourceSchema? Schema(string alias) => Catalogue.GetValueOrDefault(alias);

    [Fact]
    public void Sources_must_exist_and_carry_the_expected_columns()
    {
        Assert.Empty(ScheduleValidation.Validate(Settings(), Schema));
        Assert.Contains(ScheduleValidation.Validate(Settings(bars: "repairs"), Schema), error => error.Contains("'repairs' has no column end, kind, link"));
        Assert.Contains(ScheduleValidation.Validate(Settings(rows: "stays"), Schema), error => error.Contains("'stays' has no column group; its rows need key, label and group"));
        Assert.Contains(ScheduleValidation.Validate(Settings(rows: "nowhere"), Schema), error => error.Contains("'nowhere' is not a published report definition with a data source or a published data source"));
        Assert.Contains(ScheduleValidation.Validate(Settings(bars: "offline"), Schema), error => error.Contains("'offline' cannot read its database schema"));
        Assert.Contains(ScheduleValidation.Validate(Settings(rows: ""), Schema), error => error.StartsWith("Select the schedule's rows source"));
        // Without a catalogue only the settings are judged.
        Assert.Empty(ScheduleValidation.Validate(Settings(rows: "nowhere"), null));
        Assert.Contains(ScheduleValidation.Validate(Settings() with { DaysBefore = -1 }, Schema), error => error.Contains("days before and after today"));
        Assert.Contains(ScheduleValidation.Validate(Settings() with { DaysAfter = 401 }, Schema), error => error.Contains("from 0 to 400"));
        Assert.Contains(ScheduleValidation.Validate(Settings(), Schema, "weeks"), error => error.Contains("Days or Hours"));
        Assert.Empty(ScheduleValidation.Validate(Settings(), Schema, "hours"));
    }

    [Fact]
    public async Task Publishing_accepts_the_sample_schedule_at_the_page_level_and_in_website_content()
    {
        using var provider = Services();
        using var scope = provider.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<AppBlocksValidator>();
        await validator.LoadAsync();
        var page = BlockPages.Schedule().ToJsonString();
        Assert.Empty(validator.Validate(page));
        Assert.Empty(validator.Validate(page));
        // Inside a form the block is content like an Attachments block.
        var form = BlockPages.OrdersForm();
        var schedule = JsonNode.Parse(page)!["contentData"]![0]!.DeepClone();
        form["contentData"]!.AsArray().Add(schedule);
        form["expose"]!.AsArray().Add(new JsonObject { ["contentKey"] = schedule["key"]!.GetValue<string>(), ["culture"] = null, ["segment"] = null });
        form["layout"]!["Umbraco.BlockGrid"]![0]!["areas"]![0]!["items"]!.AsArray().Add(new JsonObject {
            ["areas"] = new JsonArray(), ["columnSpan"] = 12, ["contentKey"] = schedule["key"]!.GetValue<string>(), ["rowSpan"] = 1, ["settingsKey"] = null,
        });
        Assert.Empty(validator.Validate(form.ToJsonString()));
        // The settings are judged even without a catalogue: a schedule without a rows source is refused.
        var broken = JsonNode.Parse(page)!;
        foreach (var value in broken["contentData"]![0]!["values"]!.AsArray()) if (value!["alias"]!.GetValue<string>() == ScheduleSettings.RowsSourceAlias) value["value"] = "";
        Assert.Contains(validator.Validate(broken.ToJsonString()), error => error.StartsWith("Select the schedule's rows source"));
    }

    [Fact]
    public void The_schedule_type_keeps_its_key_and_its_settings_in_order()
    {
        var key = AppBlockSchema.Key(AppBlockSchema.ScheduleAlias).ToString();
        var type = new AppBlocks.Setup.AppBlocksSchema(typeof(AppBlocks.AppBlocksExtensions).Assembly).Definitions("ContentTypes").Single(definition => definition.Attribute("Alias")!.Value == AppBlockSchema.ScheduleAlias);
        Assert.Equal(key, type.Attribute("Key")!.Value);
        Assert.Equal(AppBlockSchema.ScheduleAlias, type.Attribute("Alias")!.Value);
        var aliases = type.Element("GenericProperties")!.Elements("GenericProperty").Select(p => p.Element("Alias")!.Value).ToArray();
        Assert.Equal(new[] { ScheduleSettings.HeadingAlias, ScheduleSettings.HelpAlias, ScheduleSettings.RowsSourceAlias, ScheduleSettings.BarsSourceAlias,
            ScheduleSettings.DaysBeforeAlias, ScheduleSettings.DaysAfterAlias, ScheduleSettings.ColumnsAlias, ScheduleSettings.LinkTemplateAlias, ScheduleSettings.HideEmptyRowsAlias }, aliases);
    }

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddScoped<IFormResource, SampleOrdersResource>();
        services.AddScoped<FormResourceRegistry>();
        services.AddScoped<AppBlocksValidator>();
        return services.BuildServiceProvider();
    }
}
