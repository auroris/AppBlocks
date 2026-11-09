using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AppBlocks.InteractiveReports.Setup;
using InteractiveReport.Core.SavedReports;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Core.Services;

namespace AppBlocks.InteractiveReports.Tests;

[Collection("Standalone Umbraco")]
public sealed class ReportAdministrationSiteTests
{
    private const string Management = "/umbraco/management/api/v1/appblocks/report-administration";
    private const string Reports = "/umbraco/reports";

    [Fact]
    public async Task Native_groups_administer_every_users_reports_and_other_accounts_cannot()
    {
        await using var site = await PublicSite.StartAsync();
        var reportAdminKey = Guid.Empty;
        var editorKey = Guid.Empty;
        const string email = "reports.admin@example.test";
        const string editorEmail = "reports.editor@example.test";
        await site.WithServices(async services => {
            var groups = services.GetRequiredService<IUserGroupService>();
            // Installing the companion does not create groups or grant Settings access.
            Assert.Null(await groups.GetAsync(ReportAdministratorGroup.Alias));
            var group = new UserGroup(services.GetRequiredService<IShortStringHelper>()) {
                Alias = ReportAdministratorGroup.Alias, Name = ReportAdministratorGroup.Name,
            };
            group.AddAllowedSection(Constants.Applications.Settings);
            Assert.True((await groups.CreateAsync(group, Constants.Security.SuperUserKey, [])).Success);
            var users = services.GetRequiredService<IUserService>();
            reportAdminKey = await Create(users, email, group.Key);
            editorKey = await Create(users, editorEmail, Constants.Security.EditorGroupKey);
        });
        await site.SetUserPassword(reportAdminKey);
        await site.SetUserPassword(editorKey);
        using var reportAdmin = await site.Administrator(email);
        using var admin = await site.Administrator();
        using var editor = await site.Administrator(editorEmail);
        using var member = await site.SignedIn("member", Guid.NewGuid().ToString(), "Report owner");
        using var anonymous = site.Anonymous();

        foreach (var allowed in new[] { reportAdmin, admin })
        {
            Assert.True((await Json(allowed, Management + "/access")).GetProperty("allowed").GetBoolean());
            var sources = await Json(allowed, Management + "/sources");
            Assert.Contains(sources.EnumerateArray(), source => source.GetProperty("name").GetString() == "configured");
            Assert.Contains(sources.EnumerateArray(), source => source.GetProperty("name").GetString()!.StartsWith("page-"));
            await Json(allowed, Reports + "/__saved-reports/schema");
        }
        Assert.False((await Json(editor, Management + "/access")).GetProperty("allowed").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync(Management + "/sources")).StatusCode);
        foreach (var refused in new[] { editor, member, anonymous })
            Assert.False((await refused.GetAsync(Reports + "/__saved-reports/schema")).IsSuccessStatusCode);

        var privateId = await Save(member, "/api/reports/configured", "User private report", false);
        var publicId = await Save(admin, Reports + "/configured", "Other administrator public report", true);
        var newPublicId = await Save(reportAdmin, Reports + "/configured", "New public report", true);
        var protectedSource = PublicSite.ReportName(PublicSite.ProtectedPage, PublicSite.ProtectedReport);
        // Umbraco's Public Access refuses every backoffice User on the protected page; the site's own rule admits an administrator to save there.
        long protectedId;
        site.Switch.AdmitBackOfficeAdministrators = true;
        try { protectedId = await Save(admin, Reports + "/" + protectedSource, "Private report on protected page", false); }
        finally { site.Switch.AdmitBackOfficeAdministrators = false; }
        Assert.False((await reportAdmin.GetAsync(Reports + "/" + protectedSource + "/schema")).IsSuccessStatusCode);
        long removedSourceId = 0, configuredId = 0;
        await site.WithServices(async services => {
            var store = services.GetRequiredService<ISavedReportStore>();
            var removed = new SavedReport { ReportName = "removed-source", Title = "Removed source report", Owner = "old-account", StateJson = "{}" };
            await store.Create(removed); removedSourceId = removed.Id;
            var configured = new SavedReport { ReportName = "configured", Title = "Configured read-only report", Owner = null, SourceFile = "seed.json", Origin = SavedReportOrigin.Configured, StateJson = "{}" };
            await store.Create(configured); configuredId = configured.Id;
        });
        // Both visibility scopes are returned by the administration listing, including other owners' private reports.
        var listing = await Json(reportAdmin, Management + "/reports");
        Assert.Contains("User private report", listing.GetRawText());
        Assert.Contains("Other administrator public report", listing.GetRawText());
        Assert.Contains("New public report", listing.GetRawText());

        Assert.DoesNotContain("stateJson", listing.GetRawText(), StringComparison.OrdinalIgnoreCase);
        var choices = await Json(reportAdmin, Reports + "/admin/users?search=reports.editor");
        Assert.Contains(choices.GetProperty("items").EnumerateArray(), choice => choice.GetProperty("value").GetString() == editorKey.ToString());
        foreach (var id in new[] { privateId, publicId, protectedId, removedSourceId })
        {
            var original = Row(listing, id);
            var change = new { owner = editorKey.ToString(), modifiedUtc = original.GetProperty("modifiedUtc").GetString() };
            Assert.Equal(HttpStatusCode.Forbidden, (await editor.PutAsJsonAsync($"{Management}/reports/{id}/owner", change)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await editor.DeleteAsync(DeleteUrl(original))).StatusCode);
            using var reassigned = await reportAdmin.PutAsJsonAsync($"{Management}/reports/{id}/owner", change);
            Assert.True(reassigned.IsSuccessStatusCode, await reassigned.Content.ReadAsStringAsync());
            await site.WithServices(async services => Assert.Equal(editorKey.ToString(),
                (await services.GetRequiredService<ISavedReportStore>().Get(id))!.Owner));
            Assert.Equal(HttpStatusCode.Conflict, (await reportAdmin.DeleteAsync(DeleteUrl(original))).StatusCode);
            var current = Row(await Json(reportAdmin, Management + "/reports"), id);
            Assert.Equal(HttpStatusCode.NoContent, (await reportAdmin.DeleteAsync(DeleteUrl(current))).StatusCode);
        }
        var configuredRow = Row(listing, configuredId);
        Assert.True(configuredRow.GetProperty("readOnly").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await reportAdmin.DeleteAsync(DeleteUrl(configuredRow))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reportAdmin.PutAsJsonAsync($"{Management}/reports/{configuredId}/owner", new { owner = editorKey.ToString(), modifiedUtc = configuredRow.GetProperty("modifiedUtc").GetString() })).StatusCode);
        using var deniedPublic = await editor.PostAsJsonAsync(Reports + "/configured/saved",
            new { title = "Not authorized", state = new { page = new { index = 1, size = 10 } }, isGlobal = true });
        Assert.Equal(HttpStatusCode.Forbidden, deniedPublic.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Reports}/{newPublicId}")).StatusCode);

        // A retained login cannot preserve administration after removal from the live group.
        await site.WithServices(services => {
            var users = services.GetRequiredService<IUserService>();
            var user = users.GetByUsername(email)!;
            user.RemoveGroup(ReportAdministratorGroup.Alias);
            users.Save(user);
            return Task.CompletedTask;
        });
        Assert.False((await Json(reportAdmin, Management + "/access")).GetProperty("allowed").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await reportAdmin.GetAsync(Management + "/reports")).StatusCode);
        Assert.False((await reportAdmin.GetAsync(Reports + "/__saved-reports/schema")).IsSuccessStatusCode);
    }

    // Refused at the second request until the core fix in AppPageAuthorizationHandler (AppBlocks/Blocks/AppPageAccess.cs) merges: it
    // compared only the primary identity, so the backoffice-only principal of /umbraco/reports failed once a Member cookie was there too.
    [Fact(Skip = "Waits for the core fix in AppPageAuthorizationHandler (review A2); remove this Skip when the core branch merges.")]
    public async Task An_administrator_signed_in_as_a_member_too_opens_a_report_block_through_the_backoffice_route()
    {
        await using var site = await PublicSite.StartAsync();
        using var admin = await site.Administrator();
        var block = PublicSite.ReportName(PublicSite.ReportPage, PublicSite.PublicReport);
        await Json(admin, Reports + "/" + block + "/schema");
        // The same browser signs in to the site as a Member: the backoffice route still takes the administrator alone.
        await site.SignInAsync(admin, "member", Guid.NewGuid().ToString(), "Administrator's member account");
        await Json(admin, Reports + "/" + block + "/schema");
        using var query = await admin.PostAsJsonAsync(Reports + "/" + block + "/query", new { page = new { index = 1, size = 10 } });
        Assert.True(query.IsSuccessStatusCode, await query.Content.ReadAsStringAsync());
    }

    private static JsonElement Row(JsonElement listing, long id) => listing.EnumerateArray().Single(row => row.GetProperty("id").GetString() == id.ToString());
    private static string DeleteUrl(JsonElement row) => $"{Management}/reports/{row.GetProperty("id").GetString()}?modifiedUtc={Uri.EscapeDataString(row.GetProperty("modifiedUtc").GetString()!)}";

    private static async Task<Guid> Create(IUserService users, string email, Guid group)
    {
        var created = await users.CreateAsync(Constants.Security.SuperUserKey, new UserCreateModel {
            UserName = email, Email = email, Name = email, Kind = UserKind.Default, UserGroupKeys = new HashSet<Guid> { group },
        }, approveUser: true);
        Assert.True(created.Success, created.Status.ToString());
        return created.Result.CreatedUser!.Key;
    }

    private static async Task<long> Save(HttpClient client, string source, string title, bool isGlobal)
    {
        using var response = await client.PostAsJsonAsync(source + "/saved", new { title, isGlobal, state = new { page = new { index = 1, size = 10 } } });
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonSerializer.Deserialize<JsonElement>(body).GetProperty("id").GetInt64();
    }

    private static async Task<JsonElement> Json(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path}: {response.StatusCode} {body}");
        return JsonSerializer.Deserialize<JsonElement>(body);
    }
}
