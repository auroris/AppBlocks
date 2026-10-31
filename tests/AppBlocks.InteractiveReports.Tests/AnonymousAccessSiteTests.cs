using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AppBlocks.Blocks;

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
        await A_class_that_refuses_shows_the_notice_and_the_engine_always_refuses(site);
        await Public_access_decides_on_a_protected_page_even_when_only_inherited(site);
        await A_report_block_runs_anonymously_without_saved_views(site);
        await A_report_binding_the_user_fails_closed(site);
        await Reports_that_are_not_blocks_stay_refused_and_developers_can_refuse_blocks(site);
        await Files_read_with_the_record_and_write_only_when_the_class_allows(site);
        await Tabular_and_schedule_blocks_ask_for_a_sign_in(site);
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

        // Update and delete are the class's too, and this one has none to offer.
        using (var update = await Send(visitor, HttpMethod.Put, api + "/1", token, new { definitionVersion = form.GetProperty("definitionVersion").GetString(), version = "1", values = new { name = "Pat" }, submissionKey = Guid.NewGuid() }))
            Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
    }

    private static async Task A_class_that_refuses_shows_the_notice_and_the_engine_always_refuses(PublicSite site)
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
    }

    private static async Task A_report_block_runs_anonymously_without_saved_views(PublicSite site)
    {
        var name = PublicSite.ReportName(PublicSite.ReportPage, PublicSite.PublicReport);
        var report = "/api/reports/" + name;
        using var admin = await site.Administrator();
        var schemaForAdmin = await admin.GetFromJsonAsync<JsonElement>(report + "/schema");
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
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.DeleteAsync($"/api/reports/{savedId}")).StatusCode);
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
        foreach (var name in new[] { "__internal", "adminonly", "configured" })
            Assert.False((await visitor.GetAsync($"/api/reports/{name}/default")).IsSuccessStatusCode, name);

        var report = "/api/reports/" + PublicSite.ReportName(PublicSite.ReportPage, PublicSite.PublicReport);
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync(report + "/default")).StatusCode);
        site.Switch.ReportPolicyRefuses = true;
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.GetAsync(report + "/default")).StatusCode);
        site.Switch.ReportPolicyRefuses = false;
        site.Switch.DataPolicyRefuses = true;
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.GetAsync(report + "/default")).StatusCode);
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
        var fileId = (await adminUpload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        var list = await visitor.GetFromJsonAsync<JsonElement>(closed);
        Assert.False(list.GetProperty("canWrite").GetBoolean());
        Assert.Single(list.GetProperty("items").EnumerateArray());
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
        using (var replaced = await Upload(visitor, $"{open}/{id}", openToken, "replaced", HttpMethod.Put)) Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        Assert.Equal("replaced", await visitor.GetStringAsync($"{open}/{id}"));
        using (var removed = await Send(visitor, HttpMethod.Delete, $"{open}/{id}", openToken)) Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

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
