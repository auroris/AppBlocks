using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AppBlocks.Blocks;
using AppBlocks.DataSources;
using AppBlocks.Forms;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Services;

namespace AppBlocks.InteractiveReports.Tests;

/// <summary>
/// The owner's rule on a real site, through its HTTP endpoints, with real clients holding no account of any kind: Umbraco's
/// Public Access is the baseline, and on a page without it the class or engine behind each block decides.
/// </summary>
[Collection("Standalone Umbraco")]
public sealed class AnonymousAccessSiteTests
{
    private const string Notice = "Sign in to use this form.";

    [Fact]
    public async Task An_unprotected_page_is_open_to_anonymous_visitors_and_each_blocks_class_or_engine_decides()
    {
        await using var site = await PublicSite.StartAsync();
        await A_contact_form_works_for_a_visitor_with_no_account(site);
        await A_class_that_refuses_and_a_private_data_source_show_the_notice(site);
        await Public_access_decides_on_a_protected_page_even_when_only_inherited(site);
        await A_report_block_runs_anonymously_without_saved_views(site);
        await A_report_binding_the_user_fails_closed(site);
        await Reports_that_are_not_blocks_stay_refused_and_developers_can_refuse_blocks(site);
        await Files_read_with_the_record_and_write_only_when_the_class_allows(site);
        await Tabular_and_schedule_blocks_ask_for_a_sign_in(site);
        await An_account_that_presented_itself_and_failed_is_refused_not_taken_for_anonymous(site);
        await Both_sessions_reach_forms_and_reports_as_distinct_accounts(site);
        await A_report_block_saved_without_a_page_size_uses_the_reports_default(site);
        await Public_SQL_forms_accept_anonymous_submissions_only_with_the_source_opt_in(site);
        await Public_data_sources_deliver_reads_through_forms_tabular_and_schedules(site);
    }

    private static async Task Public_SQL_forms_accept_anonymous_submissions_only_with_the_source_opt_in(PublicSite site)
    {
        using var visitor = site.Anonymous();
        site.Switch.PublicEngine = site.Switch.AnonymousEngineWrites = true;
        try
        {
            var api = PublicSite.FormUrl(PublicSite.EnginePage, PublicSite.EngineForm);
            var form = await Load(visitor, api);
            Assert.True(form.GetProperty("record").GetProperty("capabilities").GetProperty("create").GetBoolean());
            var token = form.GetProperty("antiforgeryToken").GetString();
            using var missing = await Post(visitor, api, null, form, new { title = "No token" }, new Dictionary<string, object>());
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            using var submitted = await Post(visitor, api, token, form, new { title = "Anonymous contact" }, new Dictionary<string, object>());
            Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
            var record = (await submitted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("record");
            Assert.Equal("Anonymous contact", record.GetProperty("values").GetProperty("title").GetString());
            Assert.True(site.Switch.LastDataAccount!.IsAnonymous);

            site.Switch.DataPolicyRefuses = true;
            using var denied = await Post(visitor, api, token, form, new { title = "Policy refusal" }, new Dictionary<string, object>());
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        finally { site.Switch.PublicEngine = site.Switch.AnonymousEngineWrites = site.Switch.DataPolicyRefuses = false; }
    }

    [Theory]
    [InlineData("All pages", true)]
    [InlineData("App Blocks Pages", false)]
    [InlineData("Off", false)]
    public async Task Page_cache_setting_controls_ordinary_pages(string mode, bool noStore)
    {
        await using var site = await PublicSite.StartAsync(noStorePages: mode);
        using var visitor = site.Anonymous();
        using var response = await visitor.GetAsync("/sign-in/");
        Assert.Equal(noStore, response.Headers.CacheControl?.NoStore == true);
        // Public Access alone does not opt an unrelated page into the default mode.
        using var protectedPlainResponse = await visitor.GetAsync("/members/");
        Assert.Equal(noStore, protectedPlainResponse.Headers.CacheControl?.NoStore == true);
        // The protected branch has an AppBlocks page. Even its substituted login page follows the selected mode.
        using var protectedResponse = await visitor.GetAsync("/members/inside/");
        Assert.Equal(mode != "Off", protectedResponse.Headers.CacheControl?.NoStore == true);
    }

    /// <summary>
    /// A site's own output cache goes by its policies, not by no-store, and it is asked before anyone is known. A page that depends on who asked
    /// stays out of it, or the next visitor would be served this person's page; a page of plain content stays in, so it is the cache and not the test that decides.
    /// </summary>
    [Fact]
    public async Task A_sites_output_cache_never_keeps_a_page_that_depends_on_who_asked()
    {
        await using var site = await PublicSite.StartAsync(outputCache: true);
        var member = await CreateMemberAsync(site, "cached");
        await site.WithServices(services => {
            var access = services.GetRequiredService<IPublicAccessService>();
            var entry = access.GetEntryForContent(services.GetRequiredService<IContentService>().GetById(PublicSite.MembersPage)!)!;
            entry.AddRule(member.Username, Umbraco.Cms.Core.Constants.Conventions.PublicAccess.MemberUsernameRuleType);
            Assert.True(access.Save(entry).Success);
            return Task.CompletedTask;
        });
        using var person = await site.SignedIn("member", member.Id.ToString(), member.Username);
        using var editor = await site.SignedIn("backoffice", (await CreateUserAsync(site, "cached")).ToString(), "cached");
        using var visitor = site.Anonymous();
        foreach (var path in new[] { "/reports/", "/contact/", "/files/", "/data/", "/members/inside/", "/reports/?component=Native", "/reports/?component=AppBlocks" })
            foreach (var (client, who) in new[] { (person, "a member"), (editor, "a backoffice user"), (visitor, "a returning visitor") })
            {
                // Twice, so the second has no new antiforgery cookie, which is all that keeps the first out by accident.
                for (var i = 0; i < 2; i++) (await client.GetAsync(path)).EnsureSuccessStatusCode();
                using var stranger = site.Anonymous();
                using var response = await stranger.GetAsync(path);
                var html = await response.Content.ReadAsStringAsync();
                Assert.True(response.Headers.Age is null, $"{path} after {who} was served from the cache: {html}");
            }

        // The cache really is on: a page of plain content is kept.
        using (var first = site.Anonymous()) { using var page = await first.GetAsync("/sign-in/"); Assert.True(page.IsSuccessStatusCode, await page.Content.ReadAsStringAsync()); }
        using (var second = site.Anonymous()) Assert.NotNull((await second.GetAsync("/sign-in/")).Headers.Age);
    }

    private static async Task A_contact_form_works_for_a_visitor_with_no_account(PublicSite site)
    {
        var api = PublicSite.FormUrl(PublicSite.ContactPage, PublicSite.ContactForm);

        // The page is the visitor's first request: it renders the form, sets the antiforgery cookie and is never cached.
        foreach (var component in new[] { "", "?component=AppBlocks", "?component=Native" })
        {
            using var firstVisit = site.Anonymous();
            using var page = await firstVisit.GetAsync("/contact/" + component);
            var html = await page.Content.ReadAsStringAsync();
            Assert.True(page.IsSuccessStatusCode, component + ": " + html);
            Assert.Contains("data-app-form", html);
            Assert.Contains("data-anonymous=\"true\"", html);
            Assert.DoesNotContain(Notice, html);
            Assert.True(page.Headers.CacheControl?.NoStore, component);
            Assert.Contains(page.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith(".AspNetCore.Antiforgery", StringComparison.Ordinal));

            // The token the page's cookie goes with comes from the form's load, as parallel blocks fetch theirs.
            var tokens = await Task.WhenAll(Load(firstVisit, api), Load(firstVisit, api));
            using var created = await Post(firstVisit, api, tokens[1].GetProperty("antiforgeryToken").GetString(), tokens[0], new { name = "Pat" }, new Dictionary<string, object> { ["g-recaptcha-response"] = "good-token" });
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        }

        // A default about the person resolves to nothing; one from the address still applies.
        using (var defaults = site.Anonymous())
        {
            var html = await defaults.GetStringAsync("/contact/?topic=hello");
            Assert.Contains("data-default=\"hello\"", html);
            Assert.DoesNotContain(":USER_NAME", html);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "data-default="));
        }

        // A first request straight at the form, with nothing before it: the class sees an unauthenticated principal, never null.
        using var visitor = site.Anonymous();
        var form = await Load(visitor, api);
        var token = form.GetProperty("antiforgeryToken").GetString();
        Assert.False(string.IsNullOrEmpty(token));
        Assert.True(form.GetProperty("record").GetProperty("capabilities").GetProperty("create").GetBoolean());
        Assert.All(site.Forms.Initialized, member => Assert.False(member.Identity?.IsAuthenticated));

        var submissionKey = Guid.NewGuid().ToString();
        var longToken = new string('t', 8000);
        using (var created = await Post(visitor, api, token, form, new { name = "Pat", message = "Hello" }, new Dictionary<string, object> {
            ["g-recaptcha-response"] = "good-token", ["website"] = "", ["long-token"] = longToken, ["repeated"] = new[] { "a", "b" }, ["__RequestVerificationToken"] = "not-for-the-class",
        }, submissionKey))
        {
            Assert.True(created.StatusCode == HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
            Assert.True(created.Headers.CacheControl?.NoStore);
        }
        var received = site.Forms.Created.Last();
        Assert.NotNull(received.Member);
        Assert.False(received.Member.Identity?.IsAuthenticated);
        using (var values = JsonDocument.Parse(received.Values))
        {
            // The values are the fields alone: none of the other controls reached validation or binding.
            Assert.Equal(new[] { "name", "message" }, values.RootElement.EnumerateObject().Select(p => p.Name));
            Assert.Equal("Pat", values.RootElement.GetProperty("name").GetString());
        }
        Assert.Equal(submissionKey, received.SubmissionKey);
        Assert.Equal("good-token", received.FormData["g-recaptcha-response"]);
        Assert.Equal("", received.FormData["website"]);
        Assert.Equal(longToken, received.FormData["long-token"]);
        Assert.False(received.FormData.ContainsKey("repeated"));
        Assert.False(received.FormData.ContainsKey("__RequestVerificationToken"));

        // A bad token is the class's own validation message.
        using (var bad = await Post(visitor, api, token, form, new { name = "Pat" }, new Dictionary<string, object> { ["g-recaptcha-response"] = "bad-token" }))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
            using var errors = JsonDocument.Parse(await bad.Content.ReadAsStringAsync());
            Assert.Equal("Please confirm you are not a robot.", errors.RootElement.GetProperty("errors").GetProperty("")[0].GetString());
        }
        // Validation still runs on the fields, before the class is asked.
        var calls = site.Forms.Created.Count;
        using (var invalid = await Post(visitor, api, token, form, new { message = "No name" }, new Dictionary<string, object> { ["g-recaptcha-response"] = "good-token" }))
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal(calls, site.Forms.Created.Count);

        // Without the antiforgery token nothing reaches the class, for a visitor with a cookie or for one without.
        using (var missing = await Post(visitor, api, null, form, new { name = "Pat" }, new Dictionary<string, object> { ["g-recaptcha-response"] = "good-token" }))
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using (var stranger = site.Anonymous())
        using (var forged = await Post(stranger, api, token, form, new { name = "Pat" }, new Dictionary<string, object> { ["g-recaptcha-response"] = "good-token" }))
            Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Equal(calls, site.Forms.Created.Count);

        // There is nothing to update: an update loads its record first, and this class keeps none.
        using (var update = await Send(visitor, HttpMethod.Put, api + "/1", token, new { definitionVersion = form.GetProperty("definitionVersion").GetString(), version = "1", values = new { name = "Pat" }, submissionKey = Guid.NewGuid() }))
            Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
    }

    private static async Task A_class_that_refuses_and_a_private_data_source_show_the_notice(PublicSite site)
    {
        using var visitor = site.Anonymous();
        // The page says so at once, so there is no broken form to see first, even with no script at all.
        foreach (var path in new[] { "/refusing/", "/refusing/?component=Native", "/engine/", "/engine/?id=1" })
        {
            var html = await visitor.GetStringAsync(path);
            Assert.Contains(Notice, html);
            Assert.DoesNotContain("data-app-form", html);
        }
        var refusing = PublicSite.FormUrl(PublicSite.RefusingPage, PublicSite.RefusingForm);
        var engine = PublicSite.FormUrl(PublicSite.EnginePage, PublicSite.EngineForm);
        Assert.Equal(HttpStatusCode.Forbidden, (await visitor.GetAsync(refusing)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await visitor.GetAsync(engine)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await visitor.GetAsync(engine + "?id=1")).StatusCode);

        // The same forms still work for someone signed in.
        using var admin = await site.Administrator();
        Assert.DoesNotContain(Notice, await admin.GetStringAsync("/refusing/"));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(refusing)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(engine + "?id=1")).StatusCode);

        // A token from elsewhere cannot make the engine write: its class refuses after a valid antiforgery check.
        var contact = await Load(visitor, PublicSite.FormUrl(PublicSite.ContactPage, PublicSite.ContactForm));
        var definition = (await admin.GetFromJsonAsync<JsonElement>(engine + "?id=1")).GetProperty("definitionVersion").GetString();
        using var write = await Send(visitor, HttpMethod.Post, engine, contact.GetProperty("antiforgeryToken").GetString(), new { definitionVersion = definition, values = new { title = "Nope" }, submissionKey = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    private static async Task Public_access_decides_on_a_protected_page_even_when_only_inherited(PublicSite site)
    {
        using var visitor = site.Anonymous();
        var html = await visitor.GetStringAsync("/members/inside/");
        Assert.DoesNotContain("data-app-form", html);
        Assert.DoesNotContain("data-app-report", html);
        Assert.Equal(HttpStatusCode.Forbidden, (await visitor.GetAsync(PublicSite.FormUrl(PublicSite.ProtectedPage, PublicSite.ProtectedForm))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await visitor.GetAsync(PublicSite.FilesUrl(PublicSite.ProtectedPage, PublicSite.ProtectedFiles))).StatusCode);
        var report = "/api/reports/" + PublicSite.ReportName(PublicSite.ProtectedPage, PublicSite.ProtectedReport);
        foreach (var path in new[] { "/default", "/schema", "" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.GetAsync(report + path)).StatusCode);
        // A token got from an open page does not open a protected form.
        var open = await Load(visitor, PublicSite.FormUrl(PublicSite.ContactPage, PublicSite.ContactForm));
        using var posted = await Post(visitor, PublicSite.FormUrl(PublicSite.ProtectedPage, PublicSite.ProtectedForm), open.GetProperty("antiforgeryToken").GetString(), open, new { name = "Pat" }, new Dictionary<string, object>());
        Assert.Equal(HttpStatusCode.Forbidden, posted.StatusCode);

        // A backoffice session is no Member session: Umbraco refuses even an administrator, and AppBlocks admits no one it refuses.
        using var admin = await site.Administrator();
        html = await admin.GetStringAsync("/members/inside/");
        Assert.DoesNotContain("data-app-form", html);
        Assert.DoesNotContain("data-app-report", html);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync(PublicSite.FormUrl(PublicSite.ProtectedPage, PublicSite.ProtectedForm))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync(PublicSite.FilesUrl(PublicSite.ProtectedPage, PublicSite.ProtectedFiles))).StatusCode);
        // The report engine answers someone signed in that it refuses as it answers a report that is not there.
        foreach (var path in new[] { "/default", "/schema", "" })
            Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(report + path)).StatusCode);

        // Only a rule the host registers admits someone Umbraco refuses, and then the page and every block endpoint on it agree.
        site.Switch.AdmitBackOfficeAdministrators = true;
        try
        {
            html = await admin.GetStringAsync("/members/inside/");
            Assert.Contains("data-app-form", html);
            Assert.Contains("data-app-report", html);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(PublicSite.FormUrl(PublicSite.ProtectedPage, PublicSite.ProtectedForm))).StatusCode);
            var files = await admin.GetFromJsonAsync<JsonElement>(PublicSite.FilesUrl(PublicSite.ProtectedPage, PublicSite.ProtectedFiles));
            Assert.True(files.GetProperty("canWrite").GetBoolean());
            foreach (var path in new[] { "/default", "/schema", "" })
                Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(report + path)).StatusCode);
            // The rule admits backoffice administrators, not someone with no account.
            Assert.DoesNotContain("data-app-form", await visitor.GetStringAsync("/members/inside/"));
            Assert.Equal(HttpStatusCode.Forbidden, (await visitor.GetAsync(PublicSite.FormUrl(PublicSite.ProtectedPage, PublicSite.ProtectedForm))).StatusCode);
        }
        finally { site.Switch.AdmitBackOfficeAdministrators = false; }
    }

    private static async Task A_report_block_runs_anonymously_without_saved_views(PublicSite site)
    {
        // A page whose only interactive block is a report depends on who asks too, however the site renders it.
        foreach (var path in new[] { "/reports/", "/reports/?component=AppBlocks", "/reports/?component=Native", "/sizes/" })
        {
            using var first = site.Anonymous();
            using var page = await first.GetAsync(path);
            Assert.True(page.Headers.CacheControl?.NoStore, path);
            using var admitted = await site.Administrator();
            using var adminPage = await admitted.GetAsync(path);
            Assert.True(adminPage.Headers.CacheControl?.NoStore, path);
            Assert.DoesNotContain("Open database setup", await adminPage.Content.ReadAsStringAsync());
        }
        var name = PublicSite.ReportName(PublicSite.ReportPage, PublicSite.PublicReport);
        var report = "/api/reports/" + name;
        using var admin = await site.Administrator();
        var schemaForAdmin = await admin.GetFromJsonAsync<JsonElement>(report + "/schema");
        // Binding the block's report told the :APP. providers which page and block it is, not the definition's own alias.
        Assert.Contains(site.Bindings.Contexts, context => context.PageKey == PublicSite.ReportPage && context.FormKey == PublicSite.PublicReport);
        Assert.Contains("savedReports", schemaForAdmin.GetProperty("features").EnumerateArray().Select(f => f.GetString()));
        using var saved = await admin.PostAsJsonAsync(report + "/saved", new { title = "Shared by an administrator", state = new { page = new { index = 1, size = 1 } }, isGlobal = true });
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        var savedId = (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        using var visitor = site.Anonymous();
        var html = await visitor.GetStringAsync("/reports/");
        Assert.Contains("data-app-report", html);
        Assert.DoesNotContain("report=\"" + PublicSite.ReportName(PublicSite.ReportPage, PublicSite.PersonalReport) + "\"", html);

        var schema = await visitor.GetFromJsonAsync<JsonElement>(report + "/schema");
        Assert.DoesNotContain("savedReports", schema.GetProperty("features").EnumerateArray().Select(f => f.GetString()));
        var loaded = await visitor.GetFromJsonAsync<JsonElement>(report + "/default");
        Assert.Equal(2, loaded.GetProperty("result").GetProperty("rows").GetArrayLength());
        Assert.Contains("First public row", loaded.GetRawText());
        using var query = await visitor.PostAsJsonAsync(report + "/query", schema.GetProperty("defaultState"));
        Assert.True(query.StatusCode == HttpStatusCode.OK, await query.Content.ReadAsStringAsync());
        using var csv = await visitor.PostAsJsonAsync("/api/download/" + name + "/csv", schema.GetProperty("defaultState"));
        Assert.True(csv.StatusCode == HttpStatusCode.OK, await csv.Content.ReadAsStringAsync());
        Assert.Contains("Second public row", await csv.Content.ReadAsStringAsync());

        // Saved views are simply not there, with no error for the reader's page to show.
        using (var listed = await visitor.GetAsync(report))
        {
            Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
            Assert.Empty((await listed.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        }
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"{report}/{savedId}")).StatusCode);
        // Saving has no owner to give it, and printing no account to deliver for.
        using (var save = await visitor.PostAsJsonAsync(report + "/saved", new { title = "Mine", state = new { }, isGlobal = false }))
            Assert.Equal(HttpStatusCode.Unauthorized, save.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.DeleteAsync($"/api/reports/{savedId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.GetAsync(report + "/print/options")).StatusCode);

        // Nothing leaked into the shared state: someone signed in keeps their saved views and features.
        Assert.Single((await admin.GetFromJsonAsync<JsonElement>(report)).EnumerateArray());
        Assert.Contains("savedReports", (await admin.GetFromJsonAsync<JsonElement>(report + "/schema")).GetProperty("features").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/reports/{savedId}")).StatusCode);
    }

    private static async Task A_report_binding_the_user_fails_closed(PublicSite site)
    {
        var name = PublicSite.ReportName(PublicSite.ReportPage, PublicSite.PersonalReport);
        var report = "/api/reports/" + name;
        using var visitor = site.Anonymous();
        Assert.Contains("Sign in to use this report.", await visitor.GetStringAsync("/reports/"));
        // Its SQL would read every row if :USER_KEY were bound to nothing, so it is never run for someone anonymous.
        foreach (var path in new[] { "/default", "/schema", "" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.GetAsync(report + path)).StatusCode);
        using (var query = await visitor.PostAsJsonAsync(report + "/query", new { page = new { index = 1, size = 10 } }))
            Assert.Equal(HttpStatusCode.Unauthorized, query.StatusCode);
        using (var csv = await visitor.PostAsJsonAsync("/api/download/" + name + "/csv", new { page = new { index = 1, size = 10 } }))
            Assert.Equal(HttpStatusCode.Unauthorized, csv.StatusCode);

        using var admin = await site.Administrator();
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(report + "/default")).StatusCode);
    }

    private static async Task Reports_that_are_not_blocks_stay_refused_and_developers_can_refuse_blocks(PublicSite site)
    {
        using var visitor = site.Anonymous();
        foreach (var name in new[] { "__saved-reports", "adminonly", "configured" })
            Assert.False((await visitor.GetAsync($"/api/reports/{name}/default")).IsSuccessStatusCode, name);

        var report = "/api/reports/" + PublicSite.ReportName(PublicSite.ReportPage, PublicSite.PublicReport);
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync(report + "/default")).StatusCode);
        site.Switch.ReportPolicyRefuses = true;
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.GetAsync(report + "/default")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.GetAsync(report + "/schema")).StatusCode);
        // The page says so at once, in the notice, with no widget to fail first.
        foreach (var path in new[] { "/reports/", "/reports/?component=AppBlocks", "/reports/?component=Native" })
        {
            var html = await visitor.GetStringAsync(path);
            Assert.Contains("Sign in to use this report.", html);
            Assert.DoesNotContain("report=\"" + PublicSite.ReportName(PublicSite.ReportPage, PublicSite.PublicReport) + "\"", html);
        }
        site.Switch.ReportPolicyRefuses = false;
        Assert.Contains("report=\"" + PublicSite.ReportName(PublicSite.ReportPage, PublicSite.PublicReport) + "\"", await visitor.GetStringAsync("/reports/"));
        site.Switch.DataPolicyRefuses = true;
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.GetAsync(report + "/default")).StatusCode);
        // The schema answers the same, because it is what the page's script asks before it builds the widget, to show the notice instead.
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.GetAsync(report + "/schema")).StatusCode);
        site.Switch.DataPolicyRefuses = false;
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync(report + "/default")).StatusCode);
    }

    private static async Task Files_read_with_the_record_and_write_only_when_the_class_allows(PublicSite site)
    {
        using var visitor = site.Anonymous();
        using var admin = await site.Administrator();
        var closed = PublicSite.FilesUrl(PublicSite.FilesPage, PublicSite.ClosedFiles);
        var open = PublicSite.FilesUrl(PublicSite.FilesPage, PublicSite.OpenFiles);
        var hidden = PublicSite.FilesUrl(PublicSite.FilesPage, PublicSite.HiddenFiles);
        Assert.Contains("data-app-attachments", await visitor.GetStringAsync("/files/"));

        // No IAttachmentOwner: someone signed in writes as before, an anonymous visitor reads and is refused.
        var adminList = await admin.GetFromJsonAsync<JsonElement>(closed);
        Assert.True(adminList.GetProperty("canWrite").GetBoolean());
        using var adminUpload = await Upload(admin, closed, adminList.GetProperty("antiforgeryToken").GetString(), "from-admin");
        Assert.Equal(HttpStatusCode.Created, adminUpload.StatusCode);
        var uploadedByAdmin = await adminUpload.Content.ReadFromJsonAsync<JsonElement>();
        var fileId = uploadedByAdmin.GetProperty("id").GetInt64();
        var accountName = uploadedByAdmin.GetProperty("uploadedBy").GetString()!;
        Assert.NotEqual("anonymous", accountName);
        Assert.Equal(accountName, uploadedByAdmin.GetProperty("updatedBy").GetString());

        var list = await visitor.GetFromJsonAsync<JsonElement>(closed);
        Assert.False(list.GetProperty("canWrite").GetBoolean());
        Assert.Single(list.GetProperty("items").EnumerateArray());
        // Someone with no account reads who filed it as someone signed in, not as which account. Someone signed in still sees the name.
        var seen = list.GetProperty("items")[0];
        Assert.Equal("a signed-in user", seen.GetProperty("uploadedBy").GetString());
        Assert.Equal("a signed-in user", seen.GetProperty("updatedBy").GetString());
        Assert.DoesNotContain(accountName, list.GetRawText());
        Assert.Equal(accountName, (await admin.GetFromJsonAsync<JsonElement>(closed)).GetProperty("items")[0].GetProperty("uploadedBy").GetString());
        Assert.Equal("from-admin", await visitor.GetStringAsync($"{closed}/{fileId}"));
        var token = list.GetProperty("antiforgeryToken").GetString();
        using (var upload = await Upload(visitor, closed, token, "from-visitor")) Assert.Equal(HttpStatusCode.Forbidden, upload.StatusCode);
        using (var replace = await Upload(visitor, $"{closed}/{fileId}", token, "from-visitor", HttpMethod.Put)) Assert.Equal(HttpStatusCode.Forbidden, replace.StatusCode);
        using (var remove = await Send(visitor, HttpMethod.Delete, $"{closed}/{fileId}", token)) Assert.Equal(HttpStatusCode.Forbidden, remove.StatusCode);

        // With it: the class let them, so they add, replace and remove, on the antiforgery token as anyone would.
        var openList = await visitor.GetFromJsonAsync<JsonElement>(open);
        Assert.True(openList.GetProperty("canWrite").GetBoolean());
        var openToken = openList.GetProperty("antiforgeryToken").GetString();
        using (var noToken = await Upload(visitor, open, null, "no token")) Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        using var added = await Upload(visitor, open, openToken, "from-visitor");
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var item = await added.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("anonymous", item.GetProperty("uploadedBy").GetString());
        var id = item.GetProperty("id").GetInt64();
        using var replaced = await Upload(visitor, $"{open}/{id}", openToken, "replaced", HttpMethod.Put);
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        // A replacement is a new revision with its own id.
        id = (await replaced.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        Assert.Equal("replaced",await visitor.GetStringAsync($"{open}/{id}"));
        using (var removed = await Send(visitor, HttpMethod.Delete, $"{open}/{id}", openToken)) Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        // A visitor replacing an account's file is told about its earlier revision, without the name, in the answer to the write as in the list.
        var openForAdmin = await admin.GetFromJsonAsync<JsonElement>(open);
        using var filedByAdmin = await Upload(admin, open, openForAdmin.GetProperty("antiforgeryToken").GetString(), "from-admin");
        Assert.Equal(HttpStatusCode.Created, filedByAdmin.StatusCode);
        var filedId = (await filedByAdmin.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        using var replacedAccounts = await Upload(visitor, $"{open}/{filedId}", openToken, "from-visitor", HttpMethod.Put);
        Assert.Equal(HttpStatusCode.OK, replacedAccounts.StatusCode);
        var afterReplace = await replacedAccounts.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("anonymous", afterReplace.GetProperty("updatedBy").GetString());
        Assert.Equal("a signed-in user", afterReplace.GetProperty("revisions")[0].GetProperty("uploadedBy").GetString());
        Assert.DoesNotContain(accountName, afterReplace.GetRawText());
        using (var cleared = await Send(visitor, HttpMethod.Delete, $"{open}/{afterReplace.GetProperty("id").GetInt64()}", openToken)) Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);

        // A record that does not load for them has no files for them, whatever its write rule says.
        Assert.Equal(HttpStatusCode.Forbidden, (await visitor.GetAsync(hidden)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(hidden)).StatusCode);
    }

    private static async Task Tabular_and_schedule_blocks_ask_for_a_sign_in(PublicSite site)
    {
        using var visitor = site.Anonymous();
        var html = await visitor.GetStringAsync("/data/");
        Assert.Contains("Sign in to see the rows.", html);
        Assert.Contains("Sign in to see the schedule.", html);
        Assert.DoesNotContain("data-app-tabular", html);
        Assert.DoesNotContain("data-app-schedule", html);
        var rows = $"/api/tabular/{PublicSite.DataPage}/{PublicSite.Rows}";
        Assert.Equal(HttpStatusCode.Forbidden, (await visitor.GetAsync(rows)).StatusCode);
        using (var save = await visitor.PostAsJsonAsync(rows, new { submissionKey = Guid.NewGuid(), changes = Array.Empty<object>() }))
            Assert.Equal(HttpStatusCode.Forbidden, save.StatusCode);

        using var admin = await site.Administrator();
        var adminHtml = await admin.GetStringAsync("/data/");
        Assert.Contains("data-app-tabular", adminHtml);
        Assert.Contains("data-app-schedule", adminHtml);
    }

    private static async Task Public_data_sources_deliver_reads_through_forms_tabular_and_schedules(PublicSite site)
    {
        using var visitor = site.Anonymous();
        site.Switch.PublicEngine = true;
        try
        {
            var engine = PublicSite.FormUrl(PublicSite.EnginePage, PublicSite.EngineForm);
            var html = await visitor.GetStringAsync("/engine/?id=1");
            Assert.Contains("data-app-form", html);
            Assert.DoesNotContain(Notice, html);
            var loaded = await Load(visitor, engine + "?id=1");
            Assert.Contains("First public row", loaded.GetRawText());
            Assert.False(loaded.GetProperty("record").GetProperty("capabilities").GetProperty("update").GetBoolean());
            using var write = await Send(visitor, HttpMethod.Post, engine, loaded.GetProperty("antiforgeryToken").GetString(), new {
                definitionVersion = loaded.GetProperty("definitionVersion").GetString(), values = new { title = "Nope" }, submissionKey = Guid.NewGuid(),
            });
            Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);

            var rowsUrl = $"/api/tabular/{PublicSite.DataPage}/{PublicSite.Rows}";
            Assert.Contains("data-app-tabular", await visitor.GetStringAsync("/data/"));
            var rows = await visitor.GetFromJsonAsync<JsonElement>(rowsUrl);
            Assert.False(rows.GetProperty("canAdd").GetBoolean());
            Assert.False(rows.GetProperty("canUpdate").GetBoolean());
            Assert.False(rows.GetProperty("canDelete").GetBoolean());
            using var batch = await Send(visitor, HttpMethod.Post, rowsUrl, rows.GetProperty("antiforgeryToken").GetString(), new {
                submissionKey = Guid.NewGuid(), changes = new[] { new { operation = "create", values = new { title = "Nope" } } },
            });
            Assert.Equal(HttpStatusCode.Forbidden, batch.StatusCode);

            // Real published definitions: a second request sees a changed access setting without restarting.
            await site.WithServices(services => {
                var content = services.GetRequiredService<IContentService>();
                var types = services.GetRequiredService<IContentTypeService>();
                Assert.Contains(types.Get("sqlDataSource")!.PropertyTypes, property => property.Alias == "access");
                var sources = content.GetPagedOfType(types.Get("sqlDataSource")!.Id, 0, 100, out _, null!);
                foreach (var source in sources.Where(source => source.GetValue<string>("alias")?.StartsWith("public.schedule.") == true)) {
                    source.SetValue("access", "Public");
                    Assert.True(content.Save(source).Success);
                    Assert.True(content.Publish(source, ["*"]).Success);
                }
                return Task.CompletedTask;
            });
            var schedule = await visitor.GetStringAsync("/data/");
            Assert.Contains("data-app-schedule", schedule);
            Assert.DoesNotContain("Sign in to see the schedule.", schedule);

            site.Switch.DataPolicyRefuses = true;
            Assert.Equal(HttpStatusCode.Forbidden, (await visitor.GetAsync(engine + "?id=1")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await visitor.GetAsync(rowsUrl)).StatusCode);
            Assert.Contains("You do not have access to this schedule's data.", WebUtility.HtmlDecode(await visitor.GetStringAsync("/data/")));

            // A Public source still sits behind the calling page's Public Access, including direct API requests.
            site.Switch.DataPolicyRefuses = false;
            await site.WithServices(services => {
                var content = services.GetRequiredService<IContentService>();
                var page = content.GetById(PublicSite.EnginePage)!;
                var login = content.GetById(PublicSite.MembersPage)!;
                services.GetRequiredService<IPublicAccessService>().Save(new PublicAccessEntry(page, login, login, []));
                return Task.CompletedTask;
            });
            Assert.Equal(HttpStatusCode.Forbidden, (await visitor.GetAsync(engine + "?id=1")).StatusCode);
            Assert.DoesNotContain("data-app-form", await visitor.GetStringAsync("/engine/?id=1"));
            // Umbraco refuses a backoffice User there too, until a rule the host registers admits them.
            using var admin = await site.Administrator();
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync(engine + "?id=1")).StatusCode);
            site.Switch.AdmitBackOfficeAdministrators = true;
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(engine + "?id=1")).StatusCode);
        }
        finally { site.Switch.PublicEngine = false; site.Switch.DataPolicyRefuses = false; site.Switch.AdmitBackOfficeAdministrators = false; }
    }

    private static async Task An_account_that_presented_itself_and_failed_is_refused_not_taken_for_anonymous(PublicSite site)
    {
        var contact = PublicSite.FormUrl(PublicSite.ContactPage, PublicSite.ContactForm);
        var files = PublicSite.FilesUrl(PublicSite.FilesPage, PublicSite.ClosedFiles);
        // The page and its form are open to someone with no account, so a refusal here can only be about the account.
        async Task AssertOpenForVisitor()
        {
            using var visitor = site.Anonymous();
            Assert.Contains("data-app-form", await visitor.GetStringAsync("/contact/"));
            Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync(contact)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync(files)).StatusCode);
        }
        async Task AssertRefused(HttpClient client, string who)
        {
            var html = await client.GetStringAsync("/contact/");
            Assert.True(!html.Contains("data-app-form"), who + ": " + html);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(contact)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(files)).StatusCode);
            // The report block's rule asks the same thing, so they do not get the public report either.
            var report = "/api/reports/" + PublicSite.ReportName(PublicSite.ReportPage, PublicSite.PublicReport);
            Assert.False((await client.GetAsync(report + "/default")).IsSuccessStatusCode, who);
        }
        await AssertOpenForVisitor();

        var locked = await CreateMemberAsync(site, "locked");
        using var lockedClient = await site.SignedIn("member", locked.Id.ToString(), locked.Username);
        var unapproved = await CreateMemberAsync(site, "unapproved");
        using var unapprovedClient = await site.SignedIn("member", unapproved.Id.ToString(), unapproved.Username);
        var disabled = await CreateUserAsync(site, "disabled");
        using var disabledClient = await site.SignedIn("backoffice", disabled.ToString(), "disabled");

        // Each works while the account is in good standing, as itself, and the same cookie stops working when it is not.
        foreach (var (client, who) in new[] { (lockedClient, "locked"), (unapprovedClient, "unapproved"), (disabledClient, "disabled") })
        {
            await Load(client, contact);
            Assert.True(site.Forms.Initialized.Last().Identity?.IsAuthenticated, who);
        }
        await site.WithServices(async services => {
            var members = services.GetRequiredService<IMemberService>();
            var lockedOut = members.GetById(locked.Key)!; lockedOut.IsLockedOut = true; members.Save(lockedOut);
            var notApproved = members.GetById(unapproved.Key)!; notApproved.IsApproved = false; members.Save(notApproved);
            var users = services.GetRequiredService<IUserService>();
            var user = (await users.GetAsync(disabled))!; user.IsApproved = false; users.Save(user);
        });
        await AssertRefused(lockedClient, "locked");
        await AssertRefused(unapprovedClient, "unapproved");
        await AssertRefused(disabledClient, "disabled");
        await AssertOpenForVisitor();
    }

    private static async Task A_report_block_saved_without_a_page_size_uses_the_reports_default(PublicSite site)
    {
        using var visitor = site.Anonymous();
        foreach (var (block, how) in new[] { (PublicSite.UnsizedReport, "never filled in"), (PublicSite.ZeroSizedReport, "saved as 0"), (PublicSite.EmptySizedReport, "saved empty") })
        {
            var report = "/api/reports/" + PublicSite.ReportName(PublicSite.SizesPage, block);
            using var schema = await visitor.GetAsync(report + "/schema");
            var body = await schema.Content.ReadAsStringAsync();
            Assert.True(schema.StatusCode == HttpStatusCode.OK, how + ": " + body);
            var loaded = await visitor.GetFromJsonAsync<JsonElement>(report + "/default");
            Assert.Equal(2, loaded.GetProperty("result").GetProperty("rows").GetArrayLength());
        }
        // The page offers all three, with no size to start from, where a block with one says so.
        var html = await visitor.GetStringAsync("/sizes/");
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(html, "data-app-report").Count);
        Assert.DoesNotContain("initial-page-size", html);
        Assert.Contains("initial-page-size=\"10\"", await visitor.GetStringAsync("/reports/"));
    }

    private static async Task Both_sessions_reach_forms_and_reports_as_distinct_accounts(PublicSite site)
    {
        var member = await CreateMemberAsync(site, "combined");
        var userKey = await CreateUserAsync(site, "combined");
        using var client = await site.SignedIn("member", member.Id.ToString(), member.Username);
        await site.SignInAsync(client, "backoffice", userKey.ToString(), "combined-editor");
        await Load(client, $"/api/forms/{PublicSite.ContactPage}/{PublicSite.ContactForm}");
        var principal = site.Forms.Initialized.Last();
        Assert.Equal(2, principal.Identities.Count());
        Assert.Equal(Microsoft.AspNetCore.Identity.IdentityConstants.ApplicationScheme, principal.Identity!.AuthenticationType);
        await site.WithServices(async services => {
            var account = await services.GetRequiredService<IDataSourceUserResolver>().ResolveAsync(
                new FormExecutionContext(principal, PublicSite.ContactPage, PublicSite.ContactForm, "1"), default);
            Check(account!);
        });
        var report = "/api/reports/" + PublicSite.ReportName(PublicSite.ReportPage, PublicSite.PublicReport);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(report + "/default")).StatusCode);
        Check(site.Switch.LastDataAccount!);

        await site.WithServices(async services => {
            var users = services.GetRequiredService<IUserService>();
            var user = (await users.GetAsync(userKey))!; user.IsApproved = false; users.Save(user);
        });
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(report + "/default")).StatusCode);
        Assert.True(site.Switch.LastDataAccount!.IsMember);
        Assert.False(site.Switch.LastDataAccount.IsBackOfficeUser);
        Assert.False(site.Switch.LastDataAccount.CanEditPage);
        Assert.Null(site.Switch.LastDataAccount.BackOfficeUser);

        void Check(DataSourceUser account)
        {
            Assert.True(account.IsMember && account.IsBackOfficeUser);
            Assert.Equal(member.Id.ToString(), account.Key);
            Assert.Equal(member.Username, account.Member!.Name);
            Assert.Equal("backoffice:" + userKey, account.BackOfficeUser!.Key);
            Assert.NotEqual(account.Member.Email, account.BackOfficeUser.Email);
            Assert.True(account.IsInUserGroup("editor"));
            Assert.True(account.CanEditPage && account.BackOfficeUser.CanEditPage);
        }
    }

    private static async Task<IMember> CreateMemberAsync(PublicSite site, string name)
    {
        IMember? created = null;
        await site.WithServices(services => {
            var members = services.GetRequiredService<IMemberService>();
            var username = name + "-" + Guid.NewGuid().ToString("N")[..8];
            created = members.CreateMemberWithIdentity(username, username + "@example.invalid", username, "Member", isApproved: true);
            members.Save(created);
            return Task.CompletedTask;
        });
        return created!;
    }

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

    private static async Task<JsonElement> Load(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {response.StatusCode} {body}");
        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    private static Task<HttpResponseMessage> Post(HttpClient client, string url, string? token, JsonElement form, object values, Dictionary<string, object> formData, string? submissionKey = null)
        => Send(client, HttpMethod.Post, url, token, new { definitionVersion = form.GetProperty("definitionVersion").GetString(), version = (string?)null, values, submissionKey = submissionKey ?? Guid.NewGuid().ToString(), formData });

    private static Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string url, string? token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        if (token is not null) request.Headers.Add("RequestVerificationToken", token);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> Upload(HttpClient client, string url, string? token, string text, HttpMethod? method = null)
    {
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        var request = new HttpRequestMessage(method ?? HttpMethod.Post, url) { Content = new MultipartFormDataContent { { file, "file", "note.txt" } } };
        if (token is not null) request.Headers.Add("RequestVerificationToken", token);
        return client.SendAsync(request);
    }
}
