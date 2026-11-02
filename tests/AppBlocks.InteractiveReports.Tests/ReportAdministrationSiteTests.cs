using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AppBlocks.InteractiveReports.Setup;
using InteractiveReport.Core.SavedReports;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Notifications;
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
            var group = await groups.GetAsync(ReportAdministratorGroup.Alias);
            Assert.NotNull(group);
            Assert.Equal(ReportAdministratorGroup.Name, group.Name);
            Assert.Contains(Constants.Applications.Settings, group.AllowedSections);
            var originalKey = group.Key;
            // Running startup again neither duplicates the group nor replaces it.
            await ActivatorUtilities.CreateInstance<ReportAdministratorGroup>(services)
                .HandleAsync(new UmbracoApplicationStartedNotification(false), default);
            Assert.Equal(originalKey, (await groups.GetAsync(ReportAdministratorGroup.Alias))!.Key);
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
        // Both visibility scopes are returned by the administration listing, including other owners' private reports.
        var listing = await Json(reportAdmin, Reports + "/__saved-reports/default");
        Assert.Contains("User private report", listing.GetRawText());
        Assert.Contains("Other administrator public report", listing.GetRawText());
        Assert.Contains("New public report", listing.GetRawText());

        foreach (var id in new[] { privateId, publicId })
        {
            Assert.False((await editor.PutAsJsonAsync($"{Reports}/{id}", new { owner = editorKey.ToString() })).IsSuccessStatusCode);
            Assert.False((await editor.DeleteAsync($"{Reports}/{id}")).IsSuccessStatusCode);
            using var reassigned = await reportAdmin.PutAsJsonAsync($"{Reports}/{id}", new { owner = editorKey.ToString() });
            Assert.True(reassigned.IsSuccessStatusCode, await reassigned.Content.ReadAsStringAsync());
            await site.WithServices(async services => Assert.Equal(editorKey.ToString(),
                (await services.GetRequiredService<ISavedReportStore>().Get(id))!.Owner));
            Assert.Equal(HttpStatusCode.NoContent, (await reportAdmin.DeleteAsync($"{Reports}/{id}")).StatusCode);
        }
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
        Assert.False((await reportAdmin.GetAsync(Reports + "/__saved-reports/schema")).IsSuccessStatusCode);
    }

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
