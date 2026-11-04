using System.Net;
using System.Text.Json;
using AppBlocks.InteractiveReports.Blocks;
using AppBlocks.InteractiveReports.Setup;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Services;

namespace AppBlocks.InteractiveReports.Tests;

/// <summary>
/// Who sees a report block's create button and open link, as its settings choose: on a real site, through the report's schema as
/// each kind of caller gets it.
/// </summary>
[Collection("Standalone Umbraco")]
public sealed class ReportLinkVisibilityTests
{
    [Theory]
    [InlineData(null, ReportLinkVisibility.FormPageDecides)]
    [InlineData("", ReportLinkVisibility.FormPageDecides)]
    [InlineData("Form page decides", ReportLinkVisibility.FormPageDecides)]
    [InlineData("Public", ReportLinkVisibility.Public)]
    [InlineData("Groups", ReportLinkVisibility.Groups)]
    [InlineData("Hidden", ReportLinkVisibility.Hidden)]
    [InlineData("Something else", ReportLinkVisibility.FormPageDecides)]
    public void A_stored_choice_reads_as_its_visibility(string? stored, ReportLinkVisibility expected)
        => Assert.Equal(expected, ReportLinkRule.Parse(stored));

    [Fact]
    public void A_rule_reads_its_three_properties()
    {
        var values = new Dictionary<string, string?> { ["openVisibility"] = "Groups", ["openUserGroups"] = " editor, admin,,Editor ", ["openMemberGroups"] = "7" };
        var open = ReportLinkRule.Read(ReportLinkRule.Open, values.GetValueOrDefault);
        Assert.Equal((ReportLinkVisibility.Groups, "7", true), (open.Visibility, open.MemberGroups, open.HasGroups));
        Assert.Equal(["editor", "admin"], open.UserGroups);
        var create = ReportLinkRule.Read(ReportLinkRule.Create, values.GetValueOrDefault);
        Assert.Equal((ReportLinkVisibility.FormPageDecides, 0, "", false), (create.Visibility, create.UserGroups.Count, create.MemberGroups, create.HasGroups));
    }

    [Fact]
    public async Task Each_link_is_shown_to_whom_its_setting_says()
    {
        await using var site = await PublicSite.StartAsync();
        var writer = await CreateMemberAsync(site, "writer", PublicSite.WritersGroup);
        var reviewer = await CreateMemberAsync(site, "reviewer", PublicSite.ReviewersGroup);
        var reader = await CreateMemberAsync(site, "reader");
        var editor = await CreateUserAsync(site, "reviewing-editor");
        using var writing = await site.SignedIn("member", writer.Id.ToString(), writer.Username);
        using var reviewing = await site.SignedIn("member", reviewer.Id.ToString(), reviewer.Username);
        using var reading = await site.SignedIn("member", reader.Id.ToString(), reader.Username);
        using var editing = await site.SignedIn("backoffice", editor.ToString(), "reviewing-editor");
        using var visitor = site.Anonymous();
        var callers = new Dictionary<string, HttpClient> {
            ["writer"] = writing, ["reviewer"] = reviewing, ["member outside the groups"] = reading, ["backoffice editor"] = editing, ["anonymous"] = visitor,
        };
        // The data source lets only Writers add and change rows; Groups is the Reviewers member group and the backoffice Editors group.
        var shownTo = new Dictionary<string, string[]> {
            [""] = ["writer"], ["Form page decides"] = ["writer"], ["Public"] = [.. callers.Keys], ["Groups"] = ["reviewer", "backoffice editor"], ["Hidden"] = [],
        };
        foreach (var (link, property, other) in new[] { ("create", "createLink", "editLink"), ("open", "editLink", "createLink") })
            foreach (var choice in PublicSite.LinkChoices)
                foreach (var (who, client) in callers)
                {
                    var schema = await Schema(client, PublicSite.LinkReport(link, choice));
                    Assert.True(shownTo[choice].Contains(who) == Has(schema, property), $"{link} {choice}: {who}");
                    Assert.False(Has(schema, other), $"{link} {choice}: {who} sees the hidden link");
                    if (Has(schema, "createLink")) Assert.Equal("New row", schema.GetProperty("createLink").GetProperty("label").GetString());
                    if (Has(schema, "editLink")) Assert.Contains("?id={id}", schema.GetProperty("editLink").GetProperty("urlTemplate").GetString());
                }

        // The form agrees with Form page decides for each of them, refusing what the link left out.
        foreach (var (who, client) in callers)
        {
            var form = await Load(client, PublicSite.FormUrl(PublicSite.WritersPage, PublicSite.WritersForm));
            Assert.Equal(who == "writer", form.GetProperty("record").GetProperty("capabilities").GetProperty("create").GetBoolean());
        }

        // The answer belongs to each request: joining the group gives the links to someone who asked before.
        await site.WithServices(services => {
#pragma warning disable CS0618
            services.GetRequiredService<IMemberService>().AssignRole(reader.Id, PublicSite.WritersGroup);
#pragma warning restore CS0618
            return Task.CompletedTask;
        });
        Assert.True(Has(await Schema(reading, PublicSite.LinkReport("create", "")), "createLink"));
        Assert.True(Has(await Schema(reading, PublicSite.LinkReport("open", "")), "editLink"));

        // A form class decides through the new record its Initialize returns: this one refuses anyone not signed in, and offers
        // everyone else a record to add but not the right to change one.
        var refusedSchema = await Schema(visitor, PublicSite.RefusingLinks);
        Assert.False(Has(refusedSchema, "createLink") || Has(refusedSchema, "editLink"));
        var signedInSchema = await Schema(reading, PublicSite.RefusingLinks);
        Assert.True(Has(signedInSchema, "createLink"));
        Assert.False(Has(signedInSchema, "editLink"));

        await An_existing_site_gains_the_settings(site);
    }

    /// <summary>The companion's migration step gives a site installed before the settings their properties and data types, as a fresh install has them.</summary>
    private static Task An_existing_site_gains_the_settings(PublicSite site) => site.WithServices(async services =>
    {
        var types = services.GetRequiredService<IContentTypeService>();
        var dataTypes = services.GetRequiredService<IDataTypeService>();
        var report = types.Get("appReport")!;
        var fresh = AddReportLinkVisibility.Properties.Select(alias => report.PropertyTypes.Single(p => p.Alias == alias)).ToArray();
        var group = report.PropertyGroups.Single(g => g.PropertyTypes!.Contains(fresh[0].Alias)).Alias;
        var visibility = (await dataTypes.GetAsync(fresh.Single(p => p.Alias == "createVisibility").DataTypeKey))!;
        var userGroups = (await dataTypes.GetAsync(fresh.Single(p => p.Alias == "createUserGroups").DataTypeKey))!;

        // The site as it was before the step: none of the properties, the companion's two data types gone, and the plan at the step before.
        foreach (var property in fresh) report.RemovePropertyType(property.Alias);
        Assert.True((await types.UpdateAsync(report, Umbraco.Cms.Core.Constants.Security.SuperUserKey)).Success);
        foreach (var dataType in new[] { visibility, userGroups })
            Assert.True((await dataTypes.DeleteAsync(dataType.Key, Umbraco.Cms.Core.Constants.Security.SuperUserKey)).Success);
        var state = Umbraco.Cms.Core.Constants.Conventions.Migrations.KeyValuePrefix + "AppBlocks.InteractiveReports";
        var keyValues = services.GetRequiredService<IKeyValueService>();
        keyValues.SetValue(state, "2026-11-02-package-install");
        await RunPlanAsync();

        var restoredType = types.Get("appReport")!;
        foreach (var expected in fresh)
        {
            var added = restoredType.PropertyTypes.Single(p => p.Alias == expected.Alias);
            Assert.Equal((expected.Key, expected.Name, expected.Description, expected.SortOrder, expected.DataTypeKey), (added.Key, added.Name, added.Description, added.SortOrder, added.DataTypeKey));
            Assert.Equal(group, restoredType.PropertyGroups.Single(g => g.PropertyTypes!.Contains(added.Alias)).Alias);
        }
        foreach (var dataType in new[] { visibility, userGroups })
        {
            var restored = (await dataTypes.GetAsync(dataType.Key))!;
            Assert.Equal((dataType.Name, dataType.EditorAlias, dataType.EditorUiAlias, dataType.ParentId), (restored.Name, restored.EditorAlias, restored.EditorUiAlias, restored.ParentId));
            Assert.Equal(JsonSerializer.Serialize(dataType.ConfigurationData), JsonSerializer.Serialize(restored.ConfigurationData));
        }

        // Run again over a site that has them: nothing changes.
        keyValues.SetValue(state, "2026-11-02-package-install");
        await RunPlanAsync();
        Assert.Equal(AddReportLinkVisibility.Properties.Length, types.Get("appReport")!.PropertyTypes.Count(p => AddReportLinkVisibility.Properties.Contains(p.Alias)));

        async Task RunPlanAsync()
        {
            var results = await services.GetRequiredService<Umbraco.Cms.Infrastructure.Install.PackageMigrationRunner>().RunPackagePlansAsync(["AppBlocks.InteractiveReports"]);
            Assert.All(results, result => Assert.True(result.Successful, result.Exception?.ToString()));
            Assert.Equal("2026-11-04-report-link-visibility", keyValues.GetValue(state));
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

    /// <summary>A backoffice User in Umbraco's Editors group, whose alias is the one Groups picks.</summary>
    private static async Task<Guid> CreateUserAsync(PublicSite site, string name)
    {
        var key = Guid.Empty;
        await site.WithServices(async services => {
            var username = name + "-" + Guid.NewGuid().ToString("N")[..8] + "@example.invalid";
            var result = await services.GetRequiredService<IUserService>().CreateAsync(Umbraco.Cms.Core.Constants.Security.SuperUserKey, new UserCreateModel {
                UserName = username, Email = username, Name = name, Kind = UserKind.Default, UserGroupKeys = new HashSet<Guid> { Umbraco.Cms.Core.Constants.Security.EditorGroupKey },
            }, approveUser: true);
            Assert.True(result.Success, result.Status.ToString());
            key = result.Result.CreatedUser!.Key;
        });
        return key;
    }
}
