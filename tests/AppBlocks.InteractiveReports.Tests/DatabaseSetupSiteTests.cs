using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AppBlocks.Tests;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace AppBlocks.InteractiveReports.Tests;

/// <summary>
/// The owner's scenario on a real site, through its HTTP endpoints. AppBlocks' database is on LocalDB and its connection signs
/// in as a real SQL Server login that holds no DDL rights (see <see cref="RestrictedSqlServer"/>), so every refusal is the
/// server's own. What this proves: the rights the setup screen prints are the rights that are needed, and they work on a
/// running site. What it does not: nothing here runs on PostgreSQL or Oracle, and LocalDB is not a full SQL Server instance.
/// </summary>
[Collection("Standalone Umbraco")]
public sealed class DatabaseSetupSiteTests
{
    private const string Setup = "/umbraco/management/api/v1/appblocks/database-setup";
    private static string ReportUrl(string suffix = "") => $"/api/reports/{SetupSite.ReportName}{suffix}";

    [LocalDbFact]
    public Task Reports_and_forms_work_without_rights_and_the_printed_grant_brings_the_rest_back_without_a_restart()
        => Exercise(async (database, status, _) => await database.Admin(status.Saved.GetProperty("grantSql").GetString()!), byHand: false);

    [LocalDbFact]
    public Task The_printed_scripts_make_everything_work_for_an_account_that_never_gets_a_ddl_right() => Exercise(async (database, _, admin) =>
    {
        foreach (var feature in new[] { "uploads", "saved-views" })
        {
            using var response = await admin.GetAsync($"{Setup}/{feature}/script");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await database.Admin(await response.Content.ReadAsStringAsync());
        }
    }, byHand: true);

    private static async Task Exercise(Func<RestrictedSqlServer, Status, HttpClient, Task> remedy, bool byHand)
    {
        await using var database = await RestrictedSqlServer.CreateAsync();
        try
        {
            await using var site = await SetupSite.StartAsync(database);
            using var anonymous = site.Anonymous();
            using var editor = await site.Editor();
            using var admin = await site.Administrator();

            // The account really cannot create anything.
            await Assert.ThrowsAsync<SqlException>(() => database.AccountScalar<int>("CREATE TABLE PROOF (ID int); SELECT 1"));
            Assert.Equal(1, await database.TableCount());

            // The screen is for administrators, and looking at it creates nothing.
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Setup)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync(Setup)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync($"{Setup}/uploads/script")).StatusCode);
            var status = await ReadStatus(admin, database);
            AssertFeature(status.Uploads, "action", "APP_ATTACHMENT", "missing");
            AssertFeature(status.Saved, "action", "IR_SAVED_REPORTS", "missing");
            Assert.Contains($"GRANT CREATE TABLE TO [{database.Account}]", status.Uploads.GetProperty("grantSql").GetString());
            using (var script = await admin.GetAsync($"{Setup}/uploads/script"))
            {
                var text = await script.Content.ReadAsStringAsync();
                Assert.Contains("CREATE TABLE APP_ATTACHMENT_LINK", text);
                Assert.Contains($"GRANT SELECT, INSERT, UPDATE, DELETE ON APP_ATTACHMENT TO [{database.Account}]", text);
                Assert.DoesNotContain(database.Password, text);
            }
            Assert.Equal(1, await database.TableCount());

            // A report loads, queries and exports, with saving out of its features and no error for the reader.
            await AssertReportWorks(editor, savedViews: false);
            using (var refused = await editor.PostAsJsonAsync(ReportUrl("/saved"), new { title = "Mine", state = new { }, isGlobal = false }))
                Assert.True((int)refused.StatusCode is >= 400 and < 500, refused.StatusCode.ToString());

            // Only an administrator is told saving is off, by an answer the page's browser asks for, so the page itself says nothing a cache could keep.
            const string setupNote = "/api/reports/__saved-views-setup";
            foreach (var client in new[] { anonymous, editor }) Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync(setupNote)).StatusCode);
            using (var note = await admin.GetAsync(setupNote))
            {
                Assert.Equal(HttpStatusCode.OK, note.StatusCode);
                Assert.True(note.Headers.CacheControl?.NoStore);
                Assert.Contains("/umbraco/section/settings/workspace/appblocks/view/setup", (await note.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("setupUrl").GetString());
            }

            // The form keeps working through the same account, and the block beside it says only that storage is not set up.
            var token = await AssertFormWorks(editor, "Renamed without file storage");
            var notice = await Json(await editor.GetAsync(SetupSite.FilesUrl("1")));
            Assert.Empty(notice.GetProperty("items").EnumerateArray());
            Assert.False(notice.GetProperty("canWrite").GetBoolean());
            Assert.Equal(JsonValueKind.Null, notice.GetProperty("storage").GetProperty("setupUrl").ValueKind);
            AssertPlain(notice.GetProperty("storage").GetProperty("message").GetString()!, database);
            var forAdmin = await Json(await admin.GetAsync(SetupSite.FilesUrl("1")));
            Assert.Contains("/umbraco/section/settings/workspace/appblocks/view/setup", forAdmin.GetProperty("storage").GetProperty("setupUrl").GetString());
            using (var upload = await Upload(editor, token))
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, upload.StatusCode);
                AssertPlain(await upload.Content.ReadAsStringAsync(), database);
            }
            Assert.Equal(1, await database.TableCount());

            // The same requests again log nothing new: one line each, with what to do.
            await AssertReportWorks(editor, savedViews: false);
            await editor.GetAsync(SetupSite.FilesUrl("1"));
            var warnings = site.Logs.Entries.Where(e => e.Level == LogLevel.Warning).ToArray();
            var uploadsWarning = Assert.Single(warnings, e => e.Category == "AppBlocks.Attachments.AttachmentStore");
            var savedWarning = Assert.Single(warnings, e => e.Category == "AppBlocks.InteractiveReports.Reports.SavedViewStore");
            Assert.Contains("Database setup", uploadsWarning.Message);
            Assert.Contains("Database setup", savedWarning.Message);
            Assert.DoesNotContain(database.Password, uploadsWarning.Message + savedWarning.Message);

            // The remedy, while the site keeps running, on connections the pool already holds open.
            await remedy(database, status, admin);
            if (byHand)
            {
                Assert.Equal(0, await database.AccountScalar<int>("SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE TABLE')"));
                await Assert.ThrowsAsync<SqlException>(() => database.AccountScalar<int>("CREATE TABLE PROOF (ID int); SELECT 1"));
            }

            // Saved views work on the next request.
            await AssertReportWorks(editor, savedViews: true);
            Assert.Equal(HttpStatusCode.NoContent, (await admin.GetAsync(setupNote)).StatusCode);
            var saved = await Json(await editor.PostAsJsonAsync(ReportUrl("/saved"), new { title = "Mine", state = new { page = new { index = 1, size = 5 } }, isGlobal = false }), HttpStatusCode.Created);
            var id = saved.GetProperty("id").GetInt64();
            Assert.Equal(id, Assert.Single((await Json(await editor.GetAsync(ReportUrl()))).EnumerateArray()).GetProperty("id").GetInt64());
            Assert.Equal(2, (await Json(await editor.GetAsync(ReportUrl($"/{id}")))).GetProperty("result").GetProperty("rows").GetArrayLength());
            Assert.Equal("Renamed", (await Json(await editor.PutAsJsonAsync($"/api/reports/{id}", new { title = "Renamed" }))).GetProperty("title").GetString());
            Assert.Equal(HttpStatusCode.NoContent, (await editor.DeleteAsync($"/api/reports/{id}")).StatusCode);
            Assert.Empty((await Json(await editor.GetAsync(ReportUrl()))).EnumerateArray());

            // The administration page's own table works as well.
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/reports/admin/administrators")).StatusCode);

            // Uploads work too: every store operation, through this account.
            var files = await Json(await editor.GetAsync(SetupSite.FilesUrl("1")));
            Assert.False(files.TryGetProperty("storage", out _));
            Assert.True(files.GetProperty("canWrite").GetBoolean());
            var fileToken = files.GetProperty("antiforgeryToken").GetString()!;
            var uploaded = await Json(await Upload(editor, fileToken), HttpStatusCode.Created);
            var fileId = uploaded.GetProperty("id").GetInt64();
            using (var download = await editor.GetAsync($"/api/attachments/{SetupSite.PageKey}/{SetupSite.FilesKey}/{fileId}?taskId=1"))
                Assert.Equal("first file", await download.Content.ReadAsStringAsync());
            using (var form = Multipart())
            using (var replace = new HttpRequestMessage(HttpMethod.Put, $"/api/attachments/{SetupSite.PageKey}/{SetupSite.FilesKey}/{fileId}?taskId=1") { Content = form })
            {
                replace.Headers.Add("RequestVerificationToken", fileToken);
                using var replaced = await editor.SendAsync(replace);
                Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
                fileId = (await replaced.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
            }
            using (var remove = new HttpRequestMessage(HttpMethod.Delete, $"/api/attachments/{SetupSite.PageKey}/{SetupSite.FilesKey}/{fileId}?taskId=1"))
            {
                remove.Headers.Add("RequestVerificationToken", fileToken);
                Assert.Equal(HttpStatusCode.NoContent, (await editor.SendAsync(remove)).StatusCode);
            }
            await AssertFormWorks(editor, "Renamed with file storage");

            var ready = await ReadStatus(admin, database);
            AssertFeature(ready.Uploads, "ready", "APP_ATTACHMENT", "ready");
            AssertFeature(ready.Saved, "ready", "IR_SAVED_REPORTS", "ready");
            AssertFeature(ready.Saved, "ready", "IR_ADMINISTRATORS", "ready");

            // The hand-made case needs no version table; an account that may create things has one, as before.
            Assert.Equal(byHand ? 0 : 1, await database.AdminScalar<int>("SELECT COUNT(*) FROM sys.tables WHERE name = 'APP_SCHEMA'"));

            // Each said it recovered, and nothing was ever unhandled.
            var entries = site.Logs.Entries;
            Assert.Contains(entries, e => e.Category == "AppBlocks.Attachments.AttachmentStore" && e.Level == LogLevel.Information && e.Message.Contains("work again"));
            Assert.Contains(entries, e => e.Category == "AppBlocks.InteractiveReports.Reports.SavedViewStore" && e.Level == LogLevel.Information && e.Message.Contains("work again"));
            Assert.DoesNotContain(entries, e => e.Category.Contains("ExceptionHandlerMiddleware") || e.Category.Contains("DeveloperExceptionPageMiddleware"));
        }
        finally { SqlConnection.ClearAllPools(); }
    }

    private static async Task AssertReportWorks(HttpClient client, bool savedViews)
    {
        var schema = await Json(await client.GetAsync(ReportUrl("/schema")));
        var features = schema.GetProperty("features").EnumerateArray().Select(f => f.GetString()).ToArray();
        Assert.Equal(savedViews, features.Contains("savedReports"));
        Assert.Contains("download", features);
        var loaded = await Json(await client.GetAsync(ReportUrl("/default")));
        Assert.Equal(2, loaded.GetProperty("result").GetProperty("rows").GetArrayLength());
        Assert.Empty((await Json(await client.GetAsync(ReportUrl()))).EnumerateArray());
        Assert.Equal(2, (await Json(await client.PostAsJsonAsync(ReportUrl("/query"), new { }))).GetProperty("rows").GetArrayLength());
        using var csv = await client.PostAsJsonAsync($"/api/download/{SetupSite.ReportName}/csv", new { });
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Contains("Second task", await csv.Content.ReadAsStringAsync());
    }

    /// <summary>Loads the form's record, saves a new title through it, and returns the antiforgery token it handed out.</summary>
    private static async Task<string> AssertFormWorks(HttpClient client, string title)
    {
        var form = await Json(await client.GetAsync(SetupSite.FormUrl("1")));
        var token = form.GetProperty("antiforgeryToken").GetString()!;
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/forms/{SetupSite.PageKey}/{SetupSite.FormKey}/1?taskId=1") {
            Content = JsonContent.Create(new {
                values = new { title }, definitionVersion = form.GetProperty("definitionVersion").GetString(),
                checksum = form.GetProperty("record").GetProperty("checksum").GetString(), submissionKey = Guid.NewGuid().ToString(),
            }),
        };
        request.Headers.Add("RequestVerificationToken", token);
        using var saved = await client.SendAsync(request);
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        Assert.Equal(title, (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("record").GetProperty("values").GetProperty("title").GetString());
        return token;
    }

    private static MultipartFormDataContent Multipart()
    {
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("first file"));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        return new MultipartFormDataContent { { file, "file", "note.txt" } };
    }

    private static async Task<HttpResponseMessage> Upload(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, SetupSite.FilesUrl("1")) { Content = Multipart() };
        request.Headers.Add("RequestVerificationToken", token);
        return await client.SendAsync(request);
    }

    /// <summary>What a visitor is told names nothing about the database.</summary>
    private static void AssertPlain(string text, RestrictedSqlServer database)
    {
        Assert.Contains("File storage isn't set up", text);
        foreach (var word in new[] { "CREATE", "permission", "AppBlocksDb", "APP_", database.Database, database.Account, "SqlException" })
            Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record Status(JsonElement Uploads, JsonElement Saved);

    private static async Task<Status> ReadStatus(HttpClient admin, RestrictedSqlServer database)
    {
        using var response = await admin.GetAsync(Setup);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        // Names and SQL only: no connection string, in whole or in part.
        foreach (var secret in new[] { database.Password, "User Id", "Initial Catalog", "Data Source", "Server=" }) Assert.DoesNotContain(secret, body);
        var features = JsonSerializer.Deserialize<JsonElement>(body).EnumerateArray().ToArray();
        return new(features.Single(f => f.GetProperty("alias").GetString() == "uploads"), features.Single(f => f.GetProperty("alias").GetString() == "saved-views"));
    }

    private static void AssertFeature(JsonElement feature, string state, string table, string tableState)
    {
        Assert.Equal(state, feature.GetProperty("state").GetString());
        Assert.Equal("sqlServer", feature.GetProperty("provider").GetString());
        Assert.Equal("AppBlocks database (AppBlocksDb)", feature.GetProperty("connection").GetString());
        Assert.Equal(tableState, feature.GetProperty("tables").EnumerateArray().Single(t => t.GetProperty("name").GetString() == table).GetProperty("state").GetString());
        Assert.True(feature.GetProperty("hasScript").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(feature.GetProperty("lost").GetString()));
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == expected, $"{response.RequestMessage?.RequestUri}: {response.StatusCode} {body}");
            return JsonSerializer.Deserialize<JsonElement>(body);
        }
    }
}

[CollectionDefinition("Standalone Umbraco", DisableParallelization = true)]
public sealed class StandaloneUmbracoCollection;
