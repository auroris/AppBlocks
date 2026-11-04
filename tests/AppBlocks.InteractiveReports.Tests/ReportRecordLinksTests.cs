using System.Net;
using System.Text.Json;
using AppBlocks.InteractiveReports.Blocks;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace AppBlocks.InteractiveReports.Tests;

/// <summary>
/// The report block's Record links setting, and its create button offered only to those the form page would let create a record:
/// on a real site, through the report's schema as each kind of caller gets it.
/// </summary>
[Collection("Standalone Umbraco")]
public sealed class ReportRecordLinksTests
{
    [Theory]
    [InlineData(null, ReportRecordLinks.OpenAndCreate, true, true)]
    [InlineData("", ReportRecordLinks.OpenAndCreate, true, true)]
    [InlineData("Open and create", ReportRecordLinks.OpenAndCreate, true, true)]
    [InlineData("Open only", ReportRecordLinks.OpenOnly, true, false)]
    [InlineData("Create only", ReportRecordLinks.CreateOnly, false, true)]
    [InlineData("None", ReportRecordLinks.None, false, false)]
    [InlineData("Something else", ReportRecordLinks.OpenAndCreate, true, true)]
    public void A_stored_choice_reads_as_its_links(string? stored, ReportRecordLinks expected, bool opens, bool creates)
    {
        var links = ReportRecordLinkChoices.Parse(stored);
        Assert.Equal(expected, links);
        Assert.Equal(opens, links.Opens());
        Assert.Equal(creates, links.Creates());
    }

    [Fact]
    public async Task Record_links_follow_the_block_and_create_follows_the_caller()
    {
        await using var site = await PublicSite.StartAsync();
        var writer = await CreateMemberAsync(site, "writer", PublicSite.WritersGroup);
        using var writing = await site.SignedIn("member", writer.Id.ToString(), writer.Username);

        // Someone who may create gets exactly the links each choice names; no value at all is Open and create.
        foreach (var (choice, opens, creates) in new[] { ("", true, true), ("Open and create", true, true), ("Open only", true, false), ("Create only", false, true), ("None", false, false) })
        {
            var schema = await Schema(writing, PublicSite.LinkReports[choice]);
            Assert.True(opens == Has(schema, "editLink"), $"{choice}: open link");
            Assert.True(creates == Has(schema, "createLink"), $"{choice}: create button");
            if (creates) Assert.Equal("New row", schema.GetProperty("createLink").GetProperty("label").GetString());
            if (opens) Assert.Contains("?id={id}", schema.GetProperty("editLink").GetProperty("urlTemplate").GetString());
        }

        // A reader the data source does not let write keeps the view link to each record, without a button for a new one: an
        // anonymous visitor on the public data source, and a Member outside its write groups.
        var reader = await CreateMemberAsync(site, "reader");
        using var reading = await site.SignedIn("member", reader.Id.ToString(), reader.Username);
        using var visitor = site.Anonymous();
        foreach (var (client, who) in new[] { (visitor, "anonymous"), (reading, "member outside Writers") })
        {
            foreach (var choice in new[] { "", "Open and create", "Create only" })
                Assert.False(Has(await Schema(client, PublicSite.LinkReports[choice]), "createLink"), $"{who}, {choice}: create button");
            Assert.True(Has(await Schema(client, PublicSite.LinkReports["Open and create"]), "editLink"), $"{who}: open link");
            // The form refuses them too, which is what the missing button saves them from finding out.
            var form = await Load(client, PublicSite.FormUrl(PublicSite.WritersPage, PublicSite.WritersForm));
            Assert.False(form.GetProperty("record").GetProperty("capabilities").GetProperty("create").GetBoolean(), who);
        }

        // The answer belongs to each request: the writer still gets the button after the others asked, and joining the group gives it.
        Assert.True(Has(await Schema(writing, PublicSite.LinkReports[""]), "createLink"));
        await site.WithServices(services => {
#pragma warning disable CS0618
            services.GetRequiredService<IMemberService>().AssignRole(reader.Id, PublicSite.WritersGroup);
#pragma warning restore CS0618
            return Task.CompletedTask;
        });
        Assert.True(Has(await Schema(reading, PublicSite.LinkReports[""]), "createLink"));

        // A C# form class decides in its Initialize: this one refuses anyone not signed in.
        Assert.False(Has(await Schema(visitor, PublicSite.RefusingLinks), "createLink"));
        Assert.True(Has(await Schema(reading, PublicSite.RefusingLinks), "createLink"));

        await An_existing_site_gains_the_setting(site);
    }

    /// <summary>The companion's migration step gives a site installed before the setting its property and data type, as a fresh install has them.</summary>
    private static Task An_existing_site_gains_the_setting(PublicSite site) => site.WithServices(async services =>
    {
        var types = services.GetRequiredService<IContentTypeService>();
        var dataTypes = services.GetRequiredService<IDataTypeService>();
        var report = types.Get("appReport")!;
        var fresh = report.PropertyTypes.Single(p => p.Alias == ReportRecordLinkChoices.Property);
        var freshGroup = report.PropertyGroups.Single(g => g.PropertyTypes!.Contains(fresh.Alias)).Alias;
        var choices = (await dataTypes.GetAsync(fresh.DataTypeKey))!;

        // The site as it was before the step: no property, no data type, and the plan at the step before.
        report.RemovePropertyType(ReportRecordLinkChoices.Property);
        Assert.True((await types.UpdateAsync(report, Umbraco.Cms.Core.Constants.Security.SuperUserKey)).Success);
        Assert.True((await dataTypes.DeleteAsync(choices.Key, Umbraco.Cms.Core.Constants.Security.SuperUserKey)).Success);
        var state = Umbraco.Cms.Core.Constants.Conventions.Migrations.KeyValuePrefix + "AppBlocks.InteractiveReports";
        var keyValues = services.GetRequiredService<IKeyValueService>();
        keyValues.SetValue(state, "2026-11-02-package-install");
        await RunPlanAsync();

        var added = types.Get("appReport")!.PropertyTypes.Single(p => p.Alias == ReportRecordLinkChoices.Property);
        Assert.Equal((fresh.Key, fresh.Name, fresh.Description, fresh.SortOrder, fresh.DataTypeKey), (added.Key, added.Name, added.Description, added.SortOrder, added.DataTypeKey));
        Assert.Equal(freshGroup, types.Get("appReport")!.PropertyGroups.Single(g => g.PropertyTypes!.Contains(added.Alias)).Alias);
        var restored = (await dataTypes.GetAsync(fresh.DataTypeKey))!;
        Assert.Equal((choices.Name, choices.EditorAlias, choices.EditorUiAlias, choices.ParentId), (restored.Name, restored.EditorAlias, restored.EditorUiAlias, restored.ParentId));
        Assert.Equal(JsonSerializer.Serialize(choices.ConfigurationData["items"]), JsonSerializer.Serialize(restored.ConfigurationData["items"]));

        // Run again over a site that has it: nothing changes.
        keyValues.SetValue(state, "2026-11-02-package-install");
        await RunPlanAsync();
        Assert.Single(types.Get("appReport")!.PropertyTypes, p => p.Alias == ReportRecordLinkChoices.Property);

        async Task RunPlanAsync()
        {
            var results = await services.GetRequiredService<Umbraco.Cms.Infrastructure.Install.PackageMigrationRunner>().RunPackagePlansAsync(["AppBlocks.InteractiveReports"]);
            Assert.All(results, result => Assert.True(result.Successful, result.Exception?.ToString()));
            Assert.Equal("2026-11-04-report-record-links", keyValues.GetValue(state));
        }
    });

    private static bool Has(JsonElement schema, string link) => schema.TryGetProperty(link, out var value) && value.ValueKind != JsonValueKind.Null;

    private static Task<JsonElement> Schema(HttpClient client, Guid report)
        => Load(client, "/api/reports/" + PublicSite.ReportName(PublicSite.LinksPage, report) + "/schema");

    private static async Task<JsonElement> Load(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {response.StatusCode} {body}");
        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    private static async Task<IMember> CreateMemberAsync(PublicSite site, string name, string? group = null)
    {
        IMember? created = null;
        await site.WithServices(services => {
            var members = services.GetRequiredService<IMemberService>();
            var username = name + "-" + Guid.NewGuid().ToString("N")[..8];
            created = members.CreateMemberWithIdentity(username, username + "@example.invalid", username, "Member", isApproved: true);
            members.Save(created);
#pragma warning disable CS0618
            if (group is not null) members.AssignRole(created.Id, group);
#pragma warning restore CS0618
            return Task.CompletedTask;
        });
        return created!;
    }
}
