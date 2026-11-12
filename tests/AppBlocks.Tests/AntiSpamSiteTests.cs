using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AppBlocks.AntiSpam;
using AppBlocks.Blocks;
using AppBlocks.Forms;
using Microsoft.Extensions.Logging;

namespace AppBlocks.Tests;

/// <summary>
/// The Anti-spam block on a real site with AppBlocks alone, through its HTTP endpoints. Stand-ins answer in place of the
/// services, so none of this reaches the network; <see cref="AntiSpamRealServiceTests"/> does, when asked.
/// </summary>
[Collection("Standalone Umbraco")]
public sealed class AntiSpamSiteTests
{
    private static readonly string Standin = AntiSpamSite.FormUrl(AntiSpamSite.StandInPage, AntiSpamSite.StandInForm);
    private static readonly string Honeypot = AntiSpamSite.FormUrl(AntiSpamSite.HoneypotPage, AntiSpamSite.HoneypotForm);
    private static readonly string Plain = AntiSpamSite.FormUrl(AntiSpamSite.PlainPage, AntiSpamSite.PlainForm);
    private static readonly string Turnstile = AntiSpamSite.FormUrl(AntiSpamSite.TurnstilePage, AntiSpamSite.TurnstileForm);
    private static readonly string Missing = AntiSpamSite.FormUrl(AntiSpamSite.MissingPage, AntiSpamSite.MissingForm);
    private const string Settings = "/umbraco/management/api/v1/appblocks/anti-spam";

    [Fact]
    public async Task A_form_with_the_block_is_protected_on_the_server_and_its_secrets_stay_there()
    {
        await using var site = await AntiSpamSite.StartAsync();
        Publishing_judged_the_block(site);
        await The_block_renders_inside_the_form_with_its_public_keys_only(site);
        await A_service_decides_before_the_class_runs_on_create(site);
        await A_service_decides_before_the_class_runs_on_update(site);
        await An_outage_refuses_kindly_and_is_logged_once_and_shown(site);
        await The_honeypot_refuses_the_same_way_with_no_service(site);
        await A_form_without_the_block_still_hands_the_class_its_controls(site);
        await A_real_adapter_posts_to_its_own_address_with_the_secret_from_the_screen(site);
        await A_missing_secret_refuses_and_says_so(site);
        await Someone_signed_in_goes_through_the_same_check(site);
        await An_editor_previewing_sees_a_note_and_a_cookie_alone_gets_nothing(site);
        await A_submission_has_a_body_limit_on_every_write(site);
        await The_settings_screen_is_for_administrators(site);
        await Secrets_appear_in_no_answer_log_or_file(site);
    }

    private static void Publishing_judged_the_block(AntiSpamSite site)
    {
        Assert.Contains(site.Refused, message => message.Contains("Place the Anti-spam block inside the form it protects."));
        Assert.Contains(site.Refused, message => message.Contains("Use one Anti-spam block per form."));
        Assert.Contains(site.Refused, message => message.Contains("'nobody' is not an anti-spam service this site offers."));
        Assert.Contains(site.Refused, message => message.Contains("Enter the site key for Cloudflare Turnstile"));
        Assert.Contains(site.Refused, message => message.Contains("which must never be in a block"));
        Assert.Contains(site.Refused, message => message.Contains("score threshold must be a number from 0 to 1"));
        Assert.DoesNotContain(site.Refused, message => message.Contains(AntiSpamSite.PastedSecret));
        // Publishing goes ahead without a secret key, because content may arrive first, and says what that means.
        Assert.Contains(site.PublishMessages, message => message.Contains("hCaptcha has no secret key yet"));
    }

    private static async Task The_block_renders_inside_the_form_with_its_public_keys_only(AntiSpamSite site)
    {
        using var visitor = site.Anonymous();
        var standin = await visitor.GetStringAsync("/standin/");
        var form = Regex.Match(standin, "<form .*?</form>", RegexOptions.Singleline).Value;
        Assert.Contains("data-app-antispam", form);
        Assert.Contains("data-site-key=\"standin-site-key\"", form);
        Assert.Contains("data-api=\"standin\"", form);
        Assert.Contains("data-token-field=\"standin-token\"", form);
        Assert.Contains("theme", WebUtility.HtmlDecode(Regex.Match(form, "data-options=\"([^\"]*)\"").Groups[1].Value));
        Assert.Contains("<script src=\"https://standin.example.test/api.js?render=explicit\" async defer", standin);
        Assert.DoesNotContain("hidden", Regex.Match(form, "<div class=\"app-antispam\"[^>]*>").Value);
        Assert.Contains("data-content-element-type-alias=\"appAntiSpam\"", form);

        // Each service's own script, once however many forms use it, and only for a form that can be submitted.
        var widgets = await visitor.GetStringAsync("/widgets/");
        Assert.Single(Regex.Matches(widgets, Regex.Escape("<script src=\"https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit\"")));
        Assert.Contains("<script src=\"https://www.google.com/recaptcha/api.js?render=explicit\"", widgets);
        Assert.Contains("<script src=\"https://js.hcaptcha.com/1/api.js?render=explicit&amp;recaptchacompat=off\"", widgets);
        Assert.Contains("data-site-key=\"0xTurnstileOne\"", widgets);
        Assert.Contains("data-site-key=\"0xTurnstileTwo\"", widgets);
        Assert.Contains("data-token-field=\"cf-turnstile-response\"", widgets);
        Assert.Contains("data-token-field=\"g-recaptcha-response\"", widgets);
        Assert.Contains("data-token-field=\"h-captcha-response\"", widgets);
        var threes = await visitor.GetStringAsync("/threes/");
        Assert.Contains("<script src=\"https://www.google.com/recaptcha/api.js?render=recaptcha-three-key\"", threes);
        Assert.Contains("data-action=\"appblocks_form\"", threes);
        Assert.DoesNotContain("data-content-element-type-alias=\"appAntiSpam\"", threes);
        var reading = await visitor.GetStringAsync("/reading/");
        Assert.Contains("data-app-form", reading);
        Assert.DoesNotContain("data-app-antispam", reading);
        Assert.DoesNotContain("challenges.cloudflare.com", reading);

        // The honeypot needs nothing from outside, and is a control of the form like any other.
        var honeypot = await visitor.GetStringAsync("/honeypot/");
        form = Regex.Match(honeypot, "<form .*?</form>", RegexOptions.Singleline).Value;
        Assert.Contains($"name=\"{AntiSpamSite.HoneypotField}\"", form);
        Assert.Contains("tabindex=\"-1\"", form);
        Assert.DoesNotContain("data-app-antispam", honeypot);
        Assert.DoesNotContain("<script src=\"http", honeypot);
    }

    private static async Task A_service_decides_before_the_class_runs_on_create(AntiSpamSite site)
    {
        using var visitor = site.Anonymous();
        var form = await Load(visitor, Standin);
        var creates = site.Calls.Count("create");

        // A pass reaches the class with the fields alone, and the token stops at the check.
        var key = Guid.NewGuid().ToString();
        using (var passed = await Submit(visitor, HttpMethod.Post, Standin, form, new { name = "Pat" }, new Dictionary<string, object> { [StandInService.Field] = "good", ["topic"] = "Sales" }, key))
            Assert.True(passed.StatusCode == HttpStatusCode.OK, await passed.Content.ReadAsStringAsync());
        Assert.Equal(creates + 1, site.Calls.Count("create"));
        var received = site.Calls.All.Last();
        Assert.Equal("{\"name\":\"Pat\"}", received.Values);
        Assert.Equal("Sales", received.FormData["topic"]);
        Assert.False(received.FormData.ContainsKey(StandInService.Field));
        var asked = site.StandIn.Asked.Last();
        Assert.Equal(AntiSpamSite.StandInSecret, asked.SecretKey);
        Assert.Equal("standin-site-key", asked.SiteKey);
        Assert.Equal(AntiSpamBlock.DefaultScoreThreshold, asked.ScoreThreshold);
        Assert.Equal(site.Address.Host, asked.Hostname); // The host the form was sent to, for the service to compare.

        // A fail is a message to the visitor, and the class never hears of it.
        var failed = await RefusedAsync(visitor, Standin, form, new Dictionary<string, object> { [StandInService.Field] = "bad" });
        Assert.Equal(AntiSpamGate.RefusedMessage, failed);
        Assert.Equal(creates + 1, site.Calls.Count("create"));

        // Enforcement does not depend on the browser: leaving the token out, or blanking it, or sending it twice, or as a
        // list, is refused and asks the service nothing. There is nothing a request can say to take the block away.
        var askedBefore = site.StandIn.Asked.Count;
        foreach (var body in new[] {
            """{"values":{"name":"Pat"},"definitionVersion":"$v"}""",
            """{"values":{"name":"Pat"},"definitionVersion":"$v","formData":null}""",
            """{"values":{"name":"Pat"},"definitionVersion":"$v","formData":{}}""",
            """{"values":{"name":"Pat"},"definitionVersion":"$v","formData":{"standin-token":""}}""",
            """{"values":{"name":"Pat"},"definitionVersion":"$v","formData":{"standin-token":"  "}}""",
            """{"values":{"name":"Pat"},"definitionVersion":"$v","formData":{"standin-token":["good"]}}""",
            """{"values":{"name":"Pat"},"definitionVersion":"$v","formData":{"standin-token":"good","STANDIN-TOKEN":"good"}}""",
            """{"values":{"name":"Pat"},"definitionVersion":"$v","formData":{"antiSpam":"off","service":"honeypot"},"antiSpam":false}""",
        })
        {
            using var refused = await Raw(visitor, HttpMethod.Post, Standin, form, body.Replace("$v", form.GetProperty("definitionVersion").GetString()));
            Assert.True(refused.StatusCode == HttpStatusCode.UnprocessableEntity, body + ": " + refused.StatusCode);
            Assert.Equal(AntiSpamGate.RefusedMessage, Message(await refused.Content.ReadAsStringAsync()));
        }
        // A token too long to be real is left out of the form's controls, so it is no token at all.
        Assert.Equal(AntiSpamGate.RefusedMessage, await RefusedAsync(visitor, Standin, form, new Dictionary<string, object> { [StandInService.Field] = new string('t', FormRequestValues.MaxFormDataValueLength + 1) }));
        Assert.Equal(askedBefore, site.StandIn.Asked.Count);
        Assert.Equal(creates + 1, site.Calls.Count("create"));

        // A field problem comes first, so a typo does not spend a token that works once.
        using (var invalid = await Submit(visitor, HttpMethod.Post, Standin, form, new { name = new string('n', 101) }, new Dictionary<string, object> { [StandInService.Field] = "good" }))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
            Assert.Contains("name", await invalid.Content.ReadAsStringAsync());
        }
        Assert.Equal(askedBefore, site.StandIn.Asked.Count);

        // And the antiforgery check before that.
        using var stranger = site.Anonymous();
        using (var forged = await Submit(stranger, HttpMethod.Post, Standin, form, new { name = "Pat" }, new Dictionary<string, object> { [StandInService.Field] = "good" }))
            Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Equal(askedBefore, site.StandIn.Asked.Count);
    }

    private static async Task A_service_decides_before_the_class_runs_on_update(AntiSpamSite site)
    {
        using var visitor = site.Anonymous();
        var form = await Load(visitor, Standin + "?id=1");
        var (loads, updates) = (site.Calls.Count("load"), site.Calls.Count("update"));
        object Body(params (string Name, string Value)[] controls) => new {
            values = new { name = "Pat" }, definitionVersion = form.GetProperty("definitionVersion").GetString(), checksum = "1", formData = controls.ToDictionary(c => c.Name, c => c.Value),
        };
        // A form loaded under another definition is refused before anything else, and the answer's status agrees with the response's.
        using (var stale = await Json(visitor, HttpMethod.Put, Standin + "/1", form, new { values = new { name = "Pat" }, definitionVersion = "stale", checksum = "1" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            var body = await stale.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(409, body.GetProperty("status").GetInt32());
            Assert.Equal("This form has changed. Reload the page before saving.", body.GetProperty("errors").GetProperty("").EnumerateArray().Single().GetString());
        }
        using (var missing = await Json(visitor, HttpMethod.Put, Standin + "/1", form, Body()))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
            Assert.Equal(AntiSpamGate.RefusedMessage, Message(await missing.Content.ReadAsStringAsync()));
        }
        using (var failed = await Json(visitor, HttpMethod.Put, Standin + "/1", form, Body((StandInService.Field, "bad"))))
            Assert.Equal(HttpStatusCode.UnprocessableEntity, failed.StatusCode);
        Assert.Equal((loads, updates), (site.Calls.Count("load"), site.Calls.Count("update")));

        using (var passed = await Json(visitor, HttpMethod.Put, Standin + "/1", form, Body((StandInService.Field, "good"))))
            Assert.True(passed.StatusCode == HttpStatusCode.OK, await passed.Content.ReadAsStringAsync());
        Assert.Equal(updates + 1, site.Calls.Count("update"));
        Assert.False(site.Calls.All.Last().FormData.ContainsKey(StandInService.Field));

        // A delete acts on a record the class allows and needs no check; a token sent with one still stops short of the class.
        using (var deleted = await Json(visitor, HttpMethod.Delete, Standin + "/1", form, Body((StandInService.Field, "whatever"))))
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.False(site.Calls.All.Last().FormData.ContainsKey(StandInService.Field));
    }

    private static async Task An_outage_refuses_kindly_and_is_logged_once_and_shown(AntiSpamSite site)
    {
        using var visitor = site.Anonymous();
        var form = await Load(visitor, Standin);
        using var admin = await site.Administrator();
        var calls = site.Calls.All.Count;

        for (var i = 0; i < 3; i++)
            Assert.Equal(AntiSpamGate.UnavailableMessage, await RefusedAsync(visitor, Standin, form, new Dictionary<string, object> { [StandInService.Field] = "outage" }));
        Assert.Single(site.Log.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("The stand-in is down"));
        var problem = (await Status(admin)).Single(service => service.GetProperty("alias").GetString() == "standin").GetProperty("problem").GetString()!;
        Assert.Contains("The stand-in is down", problem);
        // Even an adapter careless enough to quote them does not get them onto the screen or into the log.
        Assert.DoesNotContain(AntiSpamSite.StandInSecret, problem);
        Assert.DoesNotContain("outage", problem);
        Assert.Contains("[secret key]", problem);

        // An adapter that throws, and one that hangs, are outages too, within the short timeout.
        Assert.Equal(AntiSpamGate.UnavailableMessage, await RefusedAsync(visitor, Standin, form, new Dictionary<string, object> { [StandInService.Field] = "throw" }));
        var started = DateTime.UtcNow;
        Assert.Equal(AntiSpamGate.UnavailableMessage, await RefusedAsync(visitor, Standin, form, new Dictionary<string, object> { [StandInService.Field] = "hang" }));
        Assert.InRange((DateTime.UtcNow - started).TotalSeconds, AntiSpamGate.Timeout.TotalSeconds - 1, AntiSpamGate.Timeout.TotalSeconds + 5);
        Assert.Equal(calls, site.Calls.All.Count);
        Assert.Contains("did not answer within 5 seconds", (await Status(admin)).Single(service => service.GetProperty("alias").GetString() == "standin").GetProperty("problem").GetString());

        // An answer ends it, and the next outage is news.
        using (var passed = await Submit(visitor, HttpMethod.Post, Standin, form, new { name = "Pat" }, new Dictionary<string, object> { [StandInService.Field] = "good" }))
            Assert.Equal(HttpStatusCode.OK, passed.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await Status(admin)).Single(service => service.GetProperty("alias").GetString() == "standin").GetProperty("problem").ValueKind);
        Assert.Equal(AntiSpamGate.UnavailableMessage, await RefusedAsync(visitor, Standin, form, new Dictionary<string, object> { [StandInService.Field] = "outage" }));
        Assert.Equal(2, site.Log.Entries.Count(entry => entry.Level == LogLevel.Error && entry.Message.Contains("The stand-in is down")));
        using (var cleared = await Submit(visitor, HttpMethod.Post, Standin, form, new { name = "Pat" }, new Dictionary<string, object> { [StandInService.Field] = "good" }))
            Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
    }

    private static async Task The_honeypot_refuses_the_same_way_with_no_service(AntiSpamSite site)
    {
        using var visitor = site.Anonymous();
        var form = await Load(visitor, Honeypot);
        var creates = site.Calls.Count("create");
        var field = AntiSpamSite.HoneypotField;

        // A person's browser sends it empty.
        using (var person = await Submit(visitor, HttpMethod.Post, Honeypot, form, new { name = "Pat" }, new Dictionary<string, object> { [field] = "", ["topic"] = "Sales" }))
            Assert.True(person.StatusCode == HttpStatusCode.OK, await person.Content.ReadAsStringAsync());
        Assert.Equal(creates + 1, site.Calls.Count("create"));
        Assert.False(site.Calls.All.Last().FormData.ContainsKey(field));
        Assert.Equal("Sales", site.Calls.All.Last().FormData["topic"]);

        // Filled in, missing or not text: one answer, identical to a failed token, so nothing says which it was.
        var answers = new List<string>();
        foreach (var controls in new[] { new Dictionary<string, object> { [field] = "https://spam.example" }, [], new Dictionary<string, object> { [field] = new[] { "", "" } } })
        {
            using var bot = await Submit(visitor, HttpMethod.Post, Honeypot, form, new { name = "Pat" }, controls);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, bot.StatusCode);
            answers.Add(await bot.Content.ReadAsStringAsync());
        }
        Assert.Single(answers.Distinct());
        Assert.Equal(AntiSpamGate.RefusedMessage, Message(answers[0]));
        var standin = await Load(visitor, Standin);
        using (var token = await Submit(visitor, HttpMethod.Post, Standin, standin, new { name = "Pat" }, new Dictionary<string, object> { [StandInService.Field] = "bad" }))
            Assert.Equal(answers[0], await token.Content.ReadAsStringAsync());
        Assert.Equal(creates + 1, site.Calls.Count("create"));
    }

    private static async Task A_form_without_the_block_still_hands_the_class_its_controls(AntiSpamSite site)
    {
        using var visitor = site.Anonymous();
        var form = await Load(visitor, Plain);
        using var created = await Submit(visitor, HttpMethod.Post, Plain, form, new { name = "Pat" }, new Dictionary<string, object> { ["g-recaptcha-response"] = "its-own-token", ["website"] = "" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var received = site.Calls.All.Last();
        Assert.Equal("its-own-token", received.FormData["g-recaptcha-response"]);
        Assert.Equal("", received.FormData["website"]);
    }

    private static async Task A_real_adapter_posts_to_its_own_address_with_the_secret_from_the_screen(AntiSpamSite site)
    {
        using var visitor = site.Anonymous();
        var form = await Load(visitor, Turnstile);
        var creates = site.Calls.Count("create");
        var controls = new Dictionary<string, object> { ["cf-turnstile-response"] = "a-turnstile-token" };

        // No secret yet: refused without bothering the service.
        Assert.Equal(AntiSpamGate.UnavailableMessage, await RefusedAsync(visitor, Turnstile, form, controls));
        Assert.Empty(site.Verifier.Requests);
        using var admin = await site.Administrator();
        var status = (await Status(admin)).Single(service => service.GetProperty("alias").GetString() == "turnstile");
        Assert.Equal("none", status.GetProperty("secret").GetString());
        Assert.Contains("No secret key is set", status.GetProperty("problem").GetString());

        // The screen takes it, encrypts it and from then on says only that it is set.
        using (var saved = await admin.PostAsJsonAsync(Settings, new SaveAntiSpamKeys("turnstile", SecretKey: AntiSpamSite.SavedSecret)))
        {
            Assert.True(saved.StatusCode == HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
            var text = await saved.Content.ReadAsStringAsync();
            Assert.DoesNotContain(AntiSpamSite.SavedSecret, text);
            var row = JsonDocument.Parse(text).RootElement.EnumerateArray().Single(service => service.GetProperty("alias").GetString() == "turnstile");
            Assert.Equal("saved", row.GetProperty("secret").GetString());
            Assert.Equal(JsonValueKind.Null, row.GetProperty("problem").ValueKind);
        }
        var stored = File.ReadAllText(Path.Combine(site.Root, "App_Data", "AppBlocks", "anti-spam.json"));
        Assert.DoesNotContain(AntiSpamSite.SavedSecret, stored);
        Assert.Contains("turnstile", stored);

        using (var passed = await Submit(visitor, HttpMethod.Post, Turnstile, form, new { name = "Pat" }, controls))
            Assert.True(passed.StatusCode == HttpStatusCode.OK, await passed.Content.ReadAsStringAsync());
        Assert.Equal(creates + 1, site.Calls.Count("create"));
        var sent = Assert.Single(site.Verifier.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("https://challenges.cloudflare.com/turnstile/v0/siteverify", sent.Url.ToString());
        Assert.Equal($"secret={AntiSpamSite.SavedSecret}&response=a-turnstile-token", sent.Body);

        // The service says no.
        site.Verifier.Reply = () => new(HttpStatusCode.OK) { Content = new StringContent("""{"success":false,"error-codes":["timeout-or-duplicate"]}""") };
        Assert.Equal(AntiSpamGate.RefusedMessage, await RefusedAsync(visitor, Turnstile, form, controls));
        // The service says the keys are wrong, which is not the visitor's doing.
        site.Verifier.Reply = () => new(HttpStatusCode.OK) { Content = new StringContent("""{"success":false,"error-codes":["invalid-input-secret"]}""") };
        Assert.Equal(AntiSpamGate.UnavailableMessage, await RefusedAsync(visitor, Turnstile, form, controls));
        Assert.Contains("invalid-input-secret", (await Status(admin)).Single(service => service.GetProperty("alias").GetString() == "turnstile").GetProperty("problem").GetString());
        // The service cannot be reached, or answers nonsense.
        site.Verifier.Reply = () => throw new HttpRequestException("No such host is known.");
        Assert.Equal(AntiSpamGate.UnavailableMessage, await RefusedAsync(visitor, Turnstile, form, controls));
        site.Verifier.Reply = () => new(HttpStatusCode.OK) { Content = new StringContent("<html>not json</html>") };
        Assert.Equal(AntiSpamGate.UnavailableMessage, await RefusedAsync(visitor, Turnstile, form, controls));
        site.Verifier.Reply = () => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://elsewhere.example/") } };
        Assert.Equal(AntiSpamGate.UnavailableMessage, await RefusedAsync(visitor, Turnstile, form, controls));
        // A token solved on another site is refused; one solved here, the host the form was sent to, passes.
        site.Verifier.Reply = () => new(HttpStatusCode.OK) { Content = new StringContent("""{"success":true,"hostname":"elsewhere.example"}""") };
        Assert.Equal(AntiSpamGate.RefusedMessage, await RefusedAsync(visitor, Turnstile, form, controls));
        Assert.Equal(creates + 1, site.Calls.Count("create"));
        site.Verifier.Reply = () => new(HttpStatusCode.OK) { Content = new StringContent($$"""{"success":true,"hostname":"{{site.Address.Host}}"}""") };
        using (var here = await Submit(visitor, HttpMethod.Post, Turnstile, form, new { name = "Pat" }, controls))
            Assert.True(here.StatusCode == HttpStatusCode.OK, await here.Content.ReadAsStringAsync());
        Assert.Equal(creates + 2, site.Calls.Count("create"));
        // Every one of them went only where the adapter says.
        Assert.All(site.Verifier.Requests, request => Assert.Equal("https://challenges.cloudflare.com/turnstile/v0/siteverify", request.Url.ToString()));
        site.Verifier.Reply = () => new(HttpStatusCode.OK) { Content = new StringContent("""{"success":true}""") };
    }

    private static async Task A_missing_secret_refuses_and_says_so(AntiSpamSite site)
    {
        using var visitor = site.Anonymous();
        var form = await Load(visitor, Missing);
        var creates = site.Calls.Count("create");
        Assert.Equal(AntiSpamGate.UnavailableMessage, await RefusedAsync(visitor, Missing, form, new Dictionary<string, object> { ["h-captcha-response"] = "a-token" }));
        // With or without a token: nothing can be checked either way, and the honest answer is the same.
        Assert.Equal(AntiSpamGate.UnavailableMessage, await RefusedAsync(visitor, Missing, form, []));
        Assert.Equal(creates, site.Calls.Count("create"));
        using var admin = await site.Administrator();
        Assert.Contains("No secret key is set", (await Status(admin)).Single(service => service.GetProperty("alias").GetString() == "hcaptcha").GetProperty("problem").GetString());
        Assert.Single(site.Log.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("hcaptcha") && entry.Message.Contains("No secret key is set"));
    }

    private static async Task Someone_signed_in_goes_through_the_same_check(AntiSpamSite site)
    {
        using var admin = await site.Administrator();
        var form = await Load(admin, Standin);
        var creates = site.Calls.Count("create");
        Assert.Equal(AntiSpamGate.RefusedMessage, await RefusedAsync(admin, Standin, form, []));
        Assert.Equal(AntiSpamGate.RefusedMessage, await RefusedAsync(admin, Standin, form, new Dictionary<string, object> { [StandInService.Field] = "bad" }));
        Assert.Equal(creates, site.Calls.Count("create"));
        using (var passed = await Submit(admin, HttpMethod.Post, Standin, form, new { name = "Pat" }, new Dictionary<string, object> { [StandInService.Field] = "good" }))
            Assert.Equal(HttpStatusCode.OK, passed.StatusCode);
        Assert.Equal(creates + 1, site.Calls.Count("create"));
    }

    private static async Task An_editor_previewing_sees_a_note_and_a_cookie_alone_gets_nothing(AntiSpamSite site)
    {
        // Entering preview as Umbraco's preview service does, once signed in: its cookie, holding a token made for this editor.
        using var signedIn = await site.Administrator();
        using var editor = await site.Administrator();
        var token = await site.PreviewTokenAsync();
        site.SetCookie(editor, Umbraco.Cms.Core.Constants.Web.PreviewCookieName, token);
        var page = await editor.GetStringAsync("/standin/");
        Assert.Contains("app-antispam-preview", page);
        Assert.Contains("a submission from preview skips it", WebUtility.HtmlDecode(page));
        Assert.DoesNotContain("data-app-antispam", page);
        Assert.DoesNotContain("standin.example.test", page);
        // What would turn visitors away is said where the editor is looking.
        Assert.Contains("no secret key yet", WebUtility.HtmlDecode(await editor.GetStringAsync("/missing/")));

        var form = await Load(editor, Standin);
        var creates = site.Calls.Count("create");
        using (var passed = await Submit(editor, HttpMethod.Post, Standin, form, new { name = "Pat" }, []))
            Assert.True(passed.StatusCode == HttpStatusCode.OK, await passed.Content.ReadAsStringAsync());
        Assert.Equal(creates + 1, site.Calls.Count("create"));

        // Anyone can send the cookie. Without a token Umbraco issued, it buys them the ordinary page and the ordinary check.
        foreach (var value in new[] { "preview", token[..^4] + "AAAA", Convert.ToBase64String(new byte[64]) })
        {
            using var visitor = site.Anonymous((Umbraco.Cms.Core.Constants.Web.PreviewCookieName, value));
            var ordinary = await visitor.GetStringAsync("/standin/");
            Assert.Contains("data-app-antispam", ordinary);
            Assert.DoesNotContain("app-antispam-preview", ordinary);
            var theirs = await Load(visitor, Standin);
            Assert.Equal(AntiSpamGate.RefusedMessage, await RefusedAsync(visitor, Standin, theirs, []));
            Assert.Equal(creates + 1, site.Calls.Count("create"));
        }

        // An editor who is signed in and not previewing is back to the ordinary check, as under Someone_signed_in_goes_through_the_same_check.
        Assert.Contains("data-app-antispam", await signedIn.GetStringAsync("/standin/"));
        Assert.Equal(AntiSpamGate.RefusedMessage, await RefusedAsync(signedIn, Standin, await Load(signedIn, Standin), []));
        // Nor does a signed-in editor with a cookie Umbraco did not issue get to skip it.
        using var bogus = await site.Administrator((Umbraco.Cms.Core.Constants.Web.PreviewCookieName, "preview"));
        Assert.Equal(AntiSpamGate.RefusedMessage, await RefusedAsync(bogus, Standin, await Load(bogus, Standin), []));
    }

    private static async Task A_submission_has_a_body_limit_on_every_write(AntiSpamSite site)
    {
        using var visitor = site.Anonymous();
        var form = await Load(visitor, Plain);
        var version = form.GetProperty("definitionVersion").GetString();
        var creates = site.Calls.Count("create");
        string Body(int length) => JsonSerializer.Serialize(new { values = new { message = new string('m', length) }, definitionVersion = version, checksum = "1" });

        // Generous: a very long message, at the limit but not over it, is parsed and reaches the class.
        var room = (int)FormEndpoints.DefaultMaxBodySize - Body(0).Length;
        using (var fits = await Raw(visitor, HttpMethod.Post, Plain, form, Body(room)))
            Assert.True(fits.StatusCode == HttpStatusCode.OK, fits.StatusCode.ToString());
        Assert.Equal(creates + 1, site.Calls.Count("create"));

        // One byte over, whether it says so up front or arrives in chunks.
        using (var over = await Raw(visitor, HttpMethod.Post, Plain, form, Body(room + 1)))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over.StatusCode);
        using (var chunked = new HttpRequestMessage(HttpMethod.Post, Plain) { Content = new PushStreamContent(Encoding.UTF8.GetBytes(Body(room + 1))) })
        {
            chunked.Headers.Add("RequestVerificationToken", form.GetProperty("antiforgeryToken").GetString());
            using var answer = await visitor.SendAsync(chunked);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, answer.StatusCode);
        }
        // Far over: the server does not read it all to find that out. It answers and closes, so a client still sending may see only the close.
        var updates = site.Calls.Count("update");
        try
        {
            using var huge = await Raw(visitor, HttpMethod.Put, Plain + "/1", form, Body(8 * 1024 * 1024));
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, huge.StatusCode);
        }
        catch (HttpRequestException) { }
        Assert.Equal(updates, site.Calls.Count("update"));
        // A delete reads its own body, under the same limit, and bad JSON there is a bad request rather than a crash.
        using (var delete = await Raw(visitor, HttpMethod.Delete, Plain + "/1", form, Body(room + 1)))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, delete.StatusCode);
        using (var nonsense = await Raw(visitor, HttpMethod.Delete, Plain + "/1", form, "{not json"))
            Assert.Equal(HttpStatusCode.BadRequest, nonsense.StatusCode);
        Assert.Equal(creates + 1, site.Calls.Count("create"));
    }

    private static async Task The_settings_screen_is_for_administrators(AntiSpamSite site)
    {
        using var visitor = site.Anonymous();
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.GetAsync(Settings)).StatusCode);
        using (var post = await visitor.PostAsJsonAsync(Settings, new SaveAntiSpamKeys("turnstile", SecretKey: "x")))
            Assert.Equal(HttpStatusCode.Unauthorized, post.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.GetAsync("/umbraco/management/api/v1/application-blocks/catalogue")).StatusCode);

        using var admin = await site.Administrator();
        // A secret from configuration cannot be overwritten here, and the service must be one the site offers.
        using (var managed = await admin.PostAsJsonAsync(Settings, new SaveAntiSpamKeys("standin", SecretKey: "another")))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, managed.StatusCode);
            Assert.Contains("configuration", await managed.Content.ReadAsStringAsync());
        }
        using (var unknown = await admin.PostAsJsonAsync(Settings, new SaveAntiSpamKeys("nobody", SecretKey: "x")))
            Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        using (var secretAsSiteKey = await admin.PostAsJsonAsync(Settings, new SaveAntiSpamKeys("turnstile", SiteKey: AntiSpamSite.SavedSecret)))
            Assert.Equal(HttpStatusCode.UnprocessableEntity, secretAsSiteKey.StatusCode);
        // The site-wide site key is shown, being public, and the picker learns only whether a service is ready.
        using (var siteKey = await admin.PostAsJsonAsync(Settings, new SaveAntiSpamKeys("recaptchaV2", SiteKey: "site-wide-site-key", SecretKey: "recaptcha-secret-to-remove")))
        {
            Assert.True(siteKey.IsSuccessStatusCode, await siteKey.Content.ReadAsStringAsync());
            Assert.Contains("site-wide-site-key", await siteKey.Content.ReadAsStringAsync());
        }
        var catalogue = await admin.GetFromJsonAsync<JsonElement>("/umbraco/management/api/v1/application-blocks/catalogue");
        var services = catalogue.GetProperty("antiSpamServices").EnumerateArray().ToArray();
        Assert.Equal(new[] { "honeypot", "turnstile", "recaptchaV2", "recaptchaV3", "hcaptcha", "standin" }, services.Select(service => service.GetProperty("alias").GetString()));
        Assert.All(services, service => Assert.Equal(new[] { "alias", "name", "external", "ready" }, service.EnumerateObject().Select(property => property.Name)));
        Assert.True(services.Single(service => service.GetProperty("alias").GetString() == "recaptchaV2").GetProperty("ready").GetBoolean());
        Assert.False(services.Single(service => service.GetProperty("alias").GetString() == "hcaptcha").GetProperty("ready").GetBoolean());
        // Removing it leaves the site key.
        using (var removed = await admin.PostAsJsonAsync(Settings, new SaveAntiSpamKeys("recaptchaV2", RemoveSecret: true)))
        {
            var row = JsonDocument.Parse(await removed.Content.ReadAsStringAsync()).RootElement.EnumerateArray().Single(service => service.GetProperty("alias").GetString() == "recaptchaV2");
            Assert.Equal("none", row.GetProperty("secret").GetString());
            Assert.Equal("site-wide-site-key", row.GetProperty("siteKey").GetString());
        }
    }

    private static async Task Secrets_appear_in_no_answer_log_or_file(AntiSpamSite site)
    {
        // The other places a secret might surface, to go with every answer the tests above were given.
        using var admin = await site.Administrator();
        foreach (var path in new[] { Settings, "/umbraco/management/api/v1/appblocks/connections", "/umbraco/management/api/v1/appblocks/forms",
            "/umbraco/management/api/v1/application-blocks/catalogue", $"/umbraco/management/api/v1/document/{AntiSpamSite.TurnstilePage}", $"/umbraco/management/api/v1/document/{AntiSpamSite.StandInPage}" })
            Assert.True((await admin.GetAsync(path)).IsSuccessStatusCode, path);
        string[] secrets = [AntiSpamSite.StandInSecret, AntiSpamSite.SavedSecret, "recaptcha-secret-to-remove"];
        Assert.True(site.Answers.Count > 60, "Fewer answers than the tests above were given: " + site.Answers.Count);
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(site.Answers, answer => answer.Contains(secret));
            Assert.DoesNotContain(site.Log.Entries, entry => entry.Message.Contains(secret));
            // Nothing under the site's folder holds one in clear: its database, which is what an export reads, its logs, its saved settings.
            foreach (var file in Directory.EnumerateFiles(site.Root, "*", SearchOption.AllDirectories))
            {
                // Umbraco keeps its own MainDom lock file shut, and it holds nothing.
                if (file.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) continue;
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.Latin1);
                Assert.False((await reader.ReadToEndAsync()).Contains(secret), $"{Path.GetRelativePath(site.Root, file)} holds a secret key.");
            }
        }
        // Nor the tokens, which are spent and stored nowhere.
        Assert.DoesNotContain(site.Log.Entries, entry => entry.Message.Contains("a-turnstile-token"));
        // The real adapter's secret went where it was supposed to, so this search was looking for the right thing.
        Assert.Contains(site.Verifier.Requests, request => request.Body.Contains(AntiSpamSite.SavedSecret));
    }

    private static async Task<JsonElement[]> Status(HttpClient admin)
    {
        var answer = await admin.GetAsync(Settings);
        Assert.True(answer.IsSuccessStatusCode, answer.StatusCode.ToString());
        return JsonDocument.Parse(await answer.Content.ReadAsStringAsync()).RootElement.EnumerateArray().ToArray();
    }

    private static async Task<JsonElement> Load(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        Assert.True(response.IsSuccessStatusCode, url + ": " + response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> Submit(HttpClient client, HttpMethod method, string url, JsonElement form, object values, Dictionary<string, object> formData, string? submissionKey = null)
        => Json(client, method, url, form, new { values, definitionVersion = form.GetProperty("definitionVersion").GetString(), submissionKey = submissionKey ?? Guid.NewGuid().ToString(), formData });

    private static Task<HttpResponseMessage> Json(HttpClient client, HttpMethod method, string url, JsonElement form, object body)
        => Raw(client, method, url, form, JsonSerializer.Serialize(body));

    private static Task<HttpResponseMessage> Raw(HttpClient client, HttpMethod method, string url, JsonElement form, string body)
    {
        var request = new HttpRequestMessage(method, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("RequestVerificationToken", form.GetProperty("antiforgeryToken").GetString());
        return client.SendAsync(request);
    }

    private static async Task<string> RefusedAsync(HttpClient client, string url, JsonElement form, Dictionary<string, object> formData)
    {
        using var refused = await Submit(client, HttpMethod.Post, url, form, new { name = "Pat" }, formData);
        var text = await refused.Content.ReadAsStringAsync();
        Assert.True(refused.StatusCode == HttpStatusCode.UnprocessableEntity, refused.StatusCode + ": " + text);
        return Message(text);
    }

    private static string Message(string answer) => JsonDocument.Parse(answer).RootElement.GetProperty("errors").GetProperty("")[0].GetString()!;

    /// <summary>A body with no length up front, sent in chunks.</summary>
    private sealed class PushStreamContent : HttpContent
    {
        private readonly byte[] bytes;
        public PushStreamContent(byte[] bytes) { this.bytes = bytes; Headers.ContentType = new MediaTypeHeaderValue("application/json"); }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            for (var at = 0; at < bytes.Length; at += 64 * 1024) await stream.WriteAsync(bytes.AsMemory(at, Math.Min(64 * 1024, bytes.Length - at)));
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}

/// <summary>Serves the site for the browser test in tests/ui, which starts this and says when to stop. Never part of an ordinary run.</summary>
[Collection("Standalone Umbraco")]
public sealed class AntiSpamBrowserHost
{
    [Fact(Explicit = true)]
    public async Task Serve_for_the_browser_test()
    {
        var control = Environment.GetEnvironmentVariable("APPBLOCKS_ANTISPAM_HOST") ?? throw new InvalidOperationException("Set APPBLOCKS_ANTISPAM_HOST to a file path.");
        await using var site = await AntiSpamSite.StartAsync(realKeys: Environment.GetEnvironmentVariable("APPBLOCKS_ANTISPAM_REAL") == "1");
        await File.WriteAllTextAsync(control, site.Address.ToString(), TestContext.Current.CancellationToken);
        while (File.Exists(control)) await Task.Delay(200, TestContext.Current.CancellationToken);
    }
}
