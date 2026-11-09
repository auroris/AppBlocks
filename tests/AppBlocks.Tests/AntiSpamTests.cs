using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AppBlocks.AntiSpam;
using AppBlocks.Blocks;
using AppBlocks.Forms;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace AppBlocks.Tests;

/// <summary>The adapters, keys and rules of the Anti-spam block, apart from a site. <see cref="AntiSpamSiteTests"/> proves them end to end.</summary>
public sealed class AntiSpamTests
{
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public string? Asked { get; private set; }
        public HttpClient CreateClient(string name) { Asked = name; return new(handler, disposeHandler: false); }
    }

    private static (T Service, RecordingVerifier Verifier, Factory Factory) With<T>(Func<IHttpClientFactory, T> create, string reply = """{"success":true}""", HttpStatusCode status = HttpStatusCode.OK)
    {
        var verifier = new RecordingVerifier { Reply = () => new(status) { Content = new StringContent(reply) } };
        var factory = new Factory(verifier);
        return (create(factory), verifier, factory);
    }

    private static AntiSpamVerification Request(decimal threshold = 0.5m) => new("the-token", "the-secret", "the-site-key", threshold);

    public static TheoryData<string, string, string> Addresses => new() {
        { "turnstile", "https://challenges.cloudflare.com/turnstile/v0/siteverify", "secret=the-secret&response=the-token" },
        { "recaptchaV2", "https://www.google.com/recaptcha/api/siteverify", "secret=the-secret&response=the-token" },
        { "hcaptcha", "https://api.hcaptcha.com/siteverify", "secret=the-secret&response=the-token&sitekey=the-site-key" },
    };

    private static IAntiSpamService Adapter(string alias, IHttpClientFactory factory) => alias switch {
        "turnstile" => new TurnstileService(factory), "recaptchaV2" => new RecaptchaV2Service(factory), "recaptchaV3" => new RecaptchaV3Service(factory), "hcaptcha" => new HCaptchaService(factory),
        _ => throw new ArgumentException(alias),
    };

    [Theory]
    [MemberData(nameof(Addresses))]
    public async Task An_adapter_posts_a_form_to_its_own_address_and_nothing_goes_in_the_url(string alias, string url, string body)
    {
        var (service, verifier, factory) = With(f => Adapter(alias, f));
        Assert.Equal(AntiSpamAnswer.Passed, await service.VerifyAsync(Request(), default));
        var sent = Assert.Single(verifier.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal(url, sent.Url.ToString());
        Assert.Equal(body, sent.Body);
        Assert.Equal(SiteVerifyService.ClientName, factory.Asked);
    }

    [Theory]
    [InlineData("""{"success":false}""", AntiSpamOutcome.Failed, null)]
    [InlineData("""{"success":false,"error-codes":["invalid-input-response"]}""", AntiSpamOutcome.Failed, null)]
    [InlineData("""{"success":false,"error-codes":["timeout-or-duplicate"]}""", AntiSpamOutcome.Failed, null)]
    [InlineData("""{"success":false,"error-codes":["missing-input-response"]}""", AntiSpamOutcome.Failed, null)]
    [InlineData("""{"success":false,"error-codes":["already-seen-response"]}""", AntiSpamOutcome.Failed, null)]
    [InlineData("""{"success":false,"error-codes":["invalid-input-secret"]}""", AntiSpamOutcome.Unavailable, "refused the site's keys (invalid-input-secret)")]
    [InlineData("""{"success":false,"error-codes":["sitekey-secret-mismatch"]}""", AntiSpamOutcome.Unavailable, "refused the site's keys")]
    [InlineData("""{"success":false,"error-codes":["internal-error"]}""", AntiSpamOutcome.Unavailable, "refused the request (internal-error)")]
    [InlineData("""{"success":false,"error-codes":["timeout-or-duplicate","internal-error"]}""", AntiSpamOutcome.Unavailable, "internal-error")]
    [InlineData("""{"success":false,"error-codes":["<script>alert(1)</script>"]}""", AntiSpamOutcome.Unavailable, "refused the request ()")]
    [InlineData("""{"success":"true"}""", AntiSpamOutcome.Unavailable, "could not read")]
    [InlineData("""[]""", AntiSpamOutcome.Unavailable, "could not read")]
    [InlineData("""<html>""", AntiSpamOutcome.Unavailable, "could not read")]
    public async Task What_a_service_says_decides_the_outcome(string reply, AntiSpamOutcome outcome, string? problem)
    {
        var (service, _, _) = With(f => new TurnstileService(f), reply);
        var answer = await service.VerifyAsync(Request(), default);
        Assert.Equal(outcome, answer.Outcome);
        if (problem is not null) Assert.Contains(problem, answer.Problem);
        Assert.DoesNotContain("the-secret", answer.Problem ?? "");
        Assert.DoesNotContain("the-token", answer.Problem ?? "");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task An_answer_that_is_not_a_success_is_an_outage(HttpStatusCode status)
    {
        var (service, _, _) = With(f => new HCaptchaService(f), """{"success":true}""", status);
        var answer = await service.VerifyAsync(Request(), default);
        Assert.Equal(AntiSpamOutcome.Unavailable, answer.Outcome);
        Assert.Contains(((int)status).ToString(), answer.Problem);
    }

    [Theory]
    // Turnstile refuses a bad secret with a 400 and its usual codes.
    [InlineData("""{"success":false,"error-codes":["invalid-input-secret"]}""", AntiSpamOutcome.Unavailable, "invalid-input-secret")]
    [InlineData("""{"success":false,"error-codes":["invalid-input-response"]}""", AntiSpamOutcome.Failed, null)]
    // Nothing but a 2xx ever passes, whatever its body says.
    [InlineData("""{"success":true}""", AntiSpamOutcome.Unavailable, "400")]
    [InlineData("""not json""", AntiSpamOutcome.Unavailable, "400")]
    public async Task A_refusal_with_error_codes_says_what_they_say_whatever_its_status(string reply, AntiSpamOutcome outcome, string? problem)
    {
        var (service, _, _) = With(f => new TurnstileService(f), reply, HttpStatusCode.BadRequest);
        var answer = await service.VerifyAsync(Request(), default);
        Assert.Equal(outcome, answer.Outcome);
        if (problem is not null) Assert.Contains(problem, answer.Problem);
    }

    [Fact]
    public async Task A_network_failure_is_an_outage_that_says_nothing_of_the_request()
    {
        var verifier = new RecordingVerifier { Reply = () => throw new HttpRequestException("the-secret was in this somehow") };
        var answer = await new RecaptchaV2Service(new Factory(verifier)).VerifyAsync(Request(), default);
        Assert.Equal(AntiSpamOutcome.Unavailable, answer.Outcome);
        Assert.Equal("Google reCAPTCHA v2 could not be reached.", answer.Problem);
    }

    [Theory]
    [InlineData("""{"success":true,"score":0.9,"action":"appblocks_form"}""", 0.5, AntiSpamOutcome.Passed)]
    [InlineData("""{"success":true,"score":0.5,"action":"appblocks_form"}""", 0.5, AntiSpamOutcome.Passed)]
    [InlineData("""{"success":true,"score":0.4,"action":"appblocks_form"}""", 0.5, AntiSpamOutcome.Failed)]
    [InlineData("""{"success":true,"score":0.6,"action":"appblocks_form"}""", 0.7, AntiSpamOutcome.Failed)]
    [InlineData("""{"success":true,"score":0.9,"action":"another_page"}""", 0.5, AntiSpamOutcome.Failed)]
    [InlineData("""{"success":false,"score":0.9,"error-codes":["timeout-or-duplicate"]}""", 0.5, AntiSpamOutcome.Failed)]
    // A v2 key answers success with no score. That says nothing about the visitor, and an administrator needs to hear of it.
    [InlineData("""{"success":true}""", 0.5, AntiSpamOutcome.Unavailable)]
    public async Task ReCAPTCHA_v3_holds_the_score_to_the_blocks_threshold(string reply, double threshold, AntiSpamOutcome outcome)
    {
        var (service, _, _) = With(f => new RecaptchaV3Service(f), reply);
        Assert.Equal(outcome, (await service.VerifyAsync(Request((decimal)threshold), default)).Outcome);
    }

    [Theory]
    [InlineData("""{"success":true,"hostname":"www.example.org"}""", "www.example.org", AntiSpamOutcome.Passed)]
    [InlineData("""{"success":true,"hostname":"WWW.Example.org"}""", "www.example.org", AntiSpamOutcome.Passed)]
    [InlineData("""{"success":true,"hostname":"elsewhere.example"}""", "www.example.org", AntiSpamOutcome.Failed)]
    // Nothing to compare: no host name in the answer, or the site turned the check off.
    [InlineData("""{"success":true}""", "www.example.org", AntiSpamOutcome.Passed)]
    [InlineData("""{"success":true,"hostname":"elsewhere.example"}""", null, AntiSpamOutcome.Passed)]
    public async Task A_token_solved_on_another_host_fails(string reply, string? hostname, AntiSpamOutcome outcome)
    {
        foreach (var alias in new[] { "turnstile", "recaptchaV2", "hcaptcha" })
        {
            var (service, _, _) = With(f => Adapter(alias, f), reply);
            Assert.Equal(outcome, (await service.VerifyAsync(Request() with { Hostname = hostname }, default)).Outcome);
        }
        var (three, _, _) = With(f => new RecaptchaV3Service(f), reply.Replace("\"success\":true", "\"success\":true,\"score\":0.9,\"action\":\"appblocks_form\""));
        Assert.Equal(outcome, (await three.VerifyAsync(Request() with { Hostname = hostname }, default)).Outcome);
    }

    [Fact]
    public void Each_adapter_describes_its_own_widget_and_offers_only_what_it_can()
    {
        var factory = new Factory(new RecordingVerifier());
        var turnstile = new TurnstileService(factory).Widget(new("key", "auto", "compact"));
        Assert.Equal("https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit", turnstile.ScriptUrl);
        Assert.Equal("turnstile", turnstile.BrowserApi);
        Assert.Equal(new Dictionary<string, string> { ["theme"] = "auto", ["size"] = "compact" }, turnstile.RenderOptions);
        Assert.Null(turnstile.ExecuteAction);

        // Auto is Turnstile's alone, and anything else an export might hold is dropped rather than passed on.
        Assert.Empty(new RecaptchaV2Service(factory).Widget(new("key", "auto", "enormous")).RenderOptions!);
        Assert.Equal("dark", new RecaptchaV2Service(factory).Widget(new("key", "dark")).RenderOptions!["theme"]);
        Assert.Equal("https://js.hcaptcha.com/1/api.js?render=explicit&recaptchacompat=off", new HCaptchaService(factory).Widget(new("key")).ScriptUrl);
        Assert.Equal("h-captcha-response", new HCaptchaService(factory).TokenField);
        Assert.Equal("g-recaptcha-response", new RecaptchaV2Service(factory).TokenField);
        Assert.Equal("cf-turnstile-response", new TurnstileService(factory).TokenField);

        // v3 shows no widget and takes its site key in the address, where content cannot steer the script elsewhere.
        var three = new RecaptchaV3Service(factory).Widget(new("a key&evil=1"));
        Assert.Equal("https://www.google.com/recaptcha/api.js?render=a%20key%26evil%3D1", three.ScriptUrl);
        Assert.Equal(RecaptchaV3Service.Action, three.ExecuteAction);
    }

    private sealed class Environment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "AppBlocks.Tests";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.Combine(root, "wwwroot");
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static AntiSpamSettings Settings(string root, IDataProtectionProvider? protection = null, params (string Key, string Value)[] configuration)
        => new(new ConfigurationBuilder().AddInMemoryCollection(configuration.Select(item => KeyValuePair.Create(item.Key, (string?)item.Value))).Build(),
            new Environment(root), protection ?? new EphemeralDataProtectionProvider());

    [Fact]
    public async Task A_saved_secret_is_encrypted_at_rest_and_only_ever_read_for_a_verification()
    {
        var root = Directory.CreateTempSubdirectory("appblocks-antispam-").FullName;
        var protection = new EphemeralDataProtectionProvider();
        var settings = Settings(root, protection);
        Assert.Equal(AntiSpamSecretState.None, settings.SecretState("turnstile"));
        Assert.Null(settings.SecretKey("turnstile"));

        const string secret = "a-secret-that-must-not-be-exported";
        await settings.SaveAsync(new("turnstile", "public-site-key", secret), default);
        var file = Path.Combine(root, "App_Data", "AppBlocks", "anti-spam.json");
        Assert.DoesNotContain(secret, File.ReadAllText(file));
        Assert.Equal(AntiSpamSecretState.Saved, settings.SecretState("turnstile"));
        Assert.Equal(secret, settings.SecretKey("turnstile"));
        Assert.Equal("public-site-key", settings.SiteKey("turnstile"));
        // A restart reads it back, and the public surface of the settings has no way to hand it out.
        Assert.Equal(secret, Settings(root, protection).SecretKey("turnstile"));
        Assert.DoesNotContain(typeof(AntiSpamSettings).GetMethods(BindingFlags.Public | BindingFlags.Instance), method => method.ReturnType == typeof(string) && method.Name.Contains("Secret"));
        Assert.DoesNotContain(typeof(AntiSpamGate).GetMethods(BindingFlags.Public | BindingFlags.Instance), method => method.ReturnType == typeof(string) && method.Name.Contains("Secret"));

        // Leaving the secret blank keeps it, as a connection string's does; changing only the site key touches nothing else.
        await settings.SaveAsync(new("turnstile", "another-site-key"), default);
        Assert.Equal(secret, settings.SecretKey("turnstile"));
        Assert.Equal("another-site-key", settings.SiteKey("turnstile"));
        await settings.SaveAsync(new("turnstile", RemoveSecret: true), default);
        Assert.Equal(AntiSpamSecretState.None, settings.SecretState("turnstile"));
        Assert.Equal("another-site-key", settings.SiteKey("turnstile"));
    }

    [Fact]
    public void Printing_a_request_never_prints_its_secrets()
    {
        // MVC logs an action's arguments this way when tracing is on, so this is a path to the log.
        var save = new SaveAntiSpamKeys("turnstile", "public", "a-secret").ToString();
        Assert.Contains("turnstile", save);
        Assert.DoesNotContain("a-secret", save);
        var verification = new AntiSpamVerification("a-token", "a-secret", "public", 0.5m).ToString();
        Assert.DoesNotContain("a-secret", verification);
        Assert.DoesNotContain("a-token", verification);
    }

    [Fact]
    public async Task A_secret_this_installation_cannot_read_is_treated_as_missing_and_said_so()
    {
        var root = Directory.CreateTempSubdirectory("appblocks-antispam-").FullName;
        await Settings(root).SaveAsync(new("turnstile", null, "a-secret"), default);
        var moved = Settings(root);
        Assert.Equal(AntiSpamSecretState.Unreadable, moved.SecretState("turnstile"));
        Assert.Null(moved.SecretKey("turnstile"));
    }

    [Fact]
    public async Task Configuration_outranks_the_screen_and_the_screen_cannot_change_it()
    {
        var root = Directory.CreateTempSubdirectory("appblocks-antispam-").FullName;
        var settings = Settings(root, null, ("AppBlocks:AntiSpam:turnstile:SecretKey", "from-configuration"), ("AppBlocks:AntiSpam:turnstile:SiteKey", "configured-site-key"));
        Assert.Equal(AntiSpamSecretState.Configuration, settings.SecretState("turnstile"));
        Assert.Equal("from-configuration", settings.SecretKey("turnstile"));
        Assert.Equal("configured-site-key", settings.SiteKey("turnstile"));
        await Assert.ThrowsAsync<ArgumentException>(() => settings.SaveAsync(new("turnstile", null, "another"), default));
        await Assert.ThrowsAsync<ArgumentException>(() => settings.SaveAsync(new("hcaptcha", "has spaces"), default));
        await Assert.ThrowsAsync<ArgumentException>(() => settings.SaveAsync(new("turnstile", "from-configuration"), default));
        await Assert.ThrowsAsync<ArgumentException>(() => settings.SaveAsync(new("hcaptcha", "same-value", "same-value"), default));
        Assert.False(File.Exists(Path.Combine(root, "App_Data", "AppBlocks", "anti-spam.json")));
    }

    private sealed class Logged : ILogger<AntiSpamHealth>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public void A_problem_is_logged_once_until_it_changes_or_ends()
    {
        var log = new Logged();
        var health = new AntiSpamHealth(log);
        Assert.Null(health.Current("turnstile"));
        for (var i = 0; i < 5; i++) health.Report("turnstile", "It could not be reached.");
        Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Error, log.Entries[0].Level);
        var since = health.Current("turnstile")!.Since;
        health.Report("turnstile", "It could not be reached.");
        Assert.Equal(since, health.Current("turnstile")!.Since);
        health.Report("turnstile", "It refused the keys.");
        Assert.Equal(2, log.Entries.Count);
        Assert.Equal("It refused the keys.", health.Current("turnstile")!.Message);
        // Another service's problem is its own.
        health.Report("hcaptcha", "It could not be reached.");
        Assert.Equal(3, log.Entries.Count);
        health.Clear("turnstile");
        Assert.Null(health.Current("turnstile"));
        Assert.NotNull(health.Current("hcaptcha"));
        Assert.Equal(LogLevel.Information, log.Entries[^1].Level);
        health.Report("turnstile", "It refused the keys.");
        Assert.Equal(2, log.Entries.Count(entry => entry.Level == LogLevel.Error && entry.Message.Contains("It refused the keys.")));
        health.Clear("nothing wrong with this one");
    }

    [Fact]
    public void Scores_and_site_keys_take_only_what_they_should()
    {
        Assert.Equal(0.5m, AntiSpamBlock.Threshold("0.5"));
        Assert.Equal(0m, AntiSpamBlock.Threshold("0"));
        Assert.Equal(1m, AntiSpamBlock.Threshold("1"));
        foreach (var bad in new[] { "1.1", "-0.1", "half", "", null }) Assert.Null(AntiSpamBlock.Threshold(bad));
        Assert.True(AntiSpamBlock.IsSiteKey("6LeIxAcTAAAAAJcZVRqyHh71UMIEGNQ_MXjiZKhI"));
        Assert.True(AntiSpamBlock.IsSiteKey("10000000-ffff-ffff-ffff-000000000001"));
        foreach (var bad in new[] { "a b", "\"><script>", "a&b=c", new string('k', 257) }) Assert.False(AntiSpamBlock.IsSiteKey(bad));
    }

    /// <summary>A form over <see cref="SampleOrdersResource"/> with these blocks in it, and the page around it.</summary>
    private static (JsonNode Page, string Json) FormWith(params (string Alias, string Value)[][] blocks)
    {
        var page = BlockPages.ContentBlocksForm();
        var area = BlockPages.Layout(page)[0]!["areas"]![0]!["items"]!.AsArray();
        foreach (var values in blocks) area.Add(BlockPages.Add(page, AppBlockSchema.AntiSpamAlias, values));
        return (page, page.ToJsonString());
    }

    private static AppBlocksValidator Validator() => new(new FormResourceRegistry([new SampleOrdersResource()], []));

    [Fact]
    public void Publishing_takes_the_block_inside_a_form_and_nowhere_else()
    {
        var validator = Validator();
        Assert.Empty(validator.Validate(FormWith([("service", "honeypot")]).Json));
        Assert.Empty(validator.Validate(FormWith([("service", "honeypot")]).Json));
        Assert.Empty(validator.Validate(FormWith([("service", "turnstile"), ("siteKey", "public-key"), ("scoreThreshold", "0.7"), ("theme", "dark"), ("size", "compact")]).Json));

        // Inside a fieldset inside the form is still inside the form.
        var nested = BlockPages.ContentBlocksForm();
        var fieldset = BlockPages.Wrap(nested, "appFieldset", [BlockPages.Add(nested, AppBlockSchema.AntiSpamAlias, ("service", "honeypot"))]);
        BlockPages.Layout(nested)[0]!["areas"]![0]!["items"]!.AsArray().Add(fieldset);
        Assert.Empty(validator.Validate(nested.ToJsonString()));

        // Outside one it is refused, wherever it was left.
        var page = BlockPages.Empty();
        BlockPages.Layout(page).Add(BlockPages.Add(page, AppBlockSchema.AntiSpamAlias, ("service", "honeypot")));
        Assert.Contains("Place the Anti-spam block inside the form it protects.", validator.Validate(page.ToJsonString()));
        Assert.Contains("Place the Anti-spam block inside the form it protects.", validator.Validate(page.ToJsonString()));
        Assert.Contains("Place the Anti-spam block inside the form it protects.", validator.Validate(page.ToJsonString()));
        page = BlockPages.Empty();
        BlockPages.Layout(page).Add(BlockPages.Wrap(page, AppBlockSchema.RegionAlias, [BlockPages.Add(page, AppBlockSchema.AntiSpamAlias, ("service", "honeypot"))]));
        Assert.Contains("Place the Anti-spam block inside the form it protects.", validator.Validate(page.ToJsonString()));
    }

    [Fact]
    public void Publishing_refuses_a_block_set_up_wrongly()
    {
        var validator = Validator();
        Assert.Contains("Use one Anti-spam block per form.", validator.Validate(FormWith([("service", "honeypot")], [("service", "honeypot")]).Json));
        Assert.Contains("Choose a service for the Anti-spam block.", validator.Validate(FormWith([("theme", "dark")]).Json));
        Assert.Contains(validator.Validate(FormWith([("service", "recaptchaV3"), ("scoreThreshold", "1.5")]).Json), error => error.Contains("score threshold"));
        Assert.Contains(validator.Validate(FormWith([("service", "turnstile"), ("siteKey", "not a key\"")]).Json), error => error.Contains("site key holds"));
    }

    private static string Config(string path)
    {
        using var stream = new ManifestEmbeddedFileProvider(typeof(AppBlocksExtensions).Assembly).GetFileInfo(path).CreateReadStream();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void The_block_has_its_stable_key_a_place_on_the_grid_and_nowhere_for_a_secret()
    {
        var definition = XElement.Parse(Config("Schema/ContentTypes/appantispam.config"));
        Assert.Equal(AppBlockSchema.Key(AppBlockSchema.AntiSpamAlias).ToString(), definition.Attribute("Key")!.Value);
        Assert.Equal("bb529b16-87cc-a2f5-2806-165bb5edcd8c", definition.Attribute("Key")!.Value);
        var aliases = definition.Element("GenericProperties")!.Elements("GenericProperty").Select(property => property.Element("Alias")!.Value).ToArray();
        Assert.Equal(new[] { "service", "siteKey", "scoreThreshold", "theme", "size" }, aliases);
        // The schema offers no property a secret could be typed into.
        Assert.DoesNotContain(aliases, alias => alias.Contains("secret", StringComparison.OrdinalIgnoreCase));
        // Editors are told about the third-party scripts.
        var description = definition.Element("Info")!.Element("Description")!.Value;
        Assert.Contains("third-party scripts", description);
        Assert.Contains("cookie consent", description);
        Assert.Contains("Content-Security-Policy", description);

        var grid = JsonNode.Parse(XElement.Parse(Config("Schema/DataTypes/ApplicationApplicationBlocks.config")).Element("Config")!.Value)!;
        var offered = grid["blocks"]!.AsArray().Single(block => block!["contentElementTypeKey"]!.GetValue<string>() == definition.Attribute("Key")!.Value)!;
        Assert.False(offered["allowAtRoot"]!.GetValue<bool>());
        Assert.True(offered["allowInAreas"]!.GetValue<bool>());
        // In the Content group, so a fieldset or a region in a form takes it.
        Assert.Equal(grid["blockGroups"]![1]!["key"]!.GetValue<string>(), offered["groupKey"]!.GetValue<string>());
    }

    private static string Guide()
    {
        using var stream = typeof(AppBlocksExtensions).Assembly.GetManifestResourceStream("AppBlocks.BusinessRules.md")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void The_guides_fences_are_well_formed()
    {
        var fences = Regex.Matches(Guide(), "^```.*$", RegexOptions.Multiline).Select(match => match.Value.TrimEnd()).ToArray();
        Assert.Equal(0, fences.Length % 2);
        for (var i = 0; i < fences.Length; i += 2) Assert.Equal("```", fences[i + 1]);
    }

    [Fact]
    public void The_rate_limit_recipe_in_the_guide_compiles()
    {
        var section = Regex.Match(Guide(), @"^### Limiting how often\b.*?(?=^#)", RegexOptions.Multiline | RegexOptions.Singleline).Value;
        Assert.Contains("/api/forms", section);
        var recipe = Regex.Match(section, @"^```csharp\r?\n(.*?)^```", RegexOptions.Multiline | RegexOptions.Singleline).Groups[1].Value;
        Assert.Contains("AddRateLimiter", recipe);
        Assert.Contains("UseRateLimiter", recipe);
        // As it would sit in a site's Program.cs, with the Web SDK's implicit usings and the site's own start and end around it.
        const string implicitUsings = """
            global using System;
            global using System.Collections.Generic;
            global using System.IO;
            global using System.Linq;
            global using System.Net.Http;
            global using System.Threading;
            global using System.Threading.Tasks;
            global using Microsoft.AspNetCore.Builder;
            global using Microsoft.AspNetCore.Hosting;
            global using Microsoft.AspNetCore.Http;
            global using Microsoft.AspNetCore.Routing;
            global using Microsoft.Extensions.Configuration;
            global using Microsoft.Extensions.DependencyInjection;
            global using Microsoft.Extensions.Hosting;
            global using Microsoft.Extensions.Logging;
            """;
        var program = CSharpSyntaxTree.ParseText(recipe);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Distinct().Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("RateLimitRecipe" + Guid.NewGuid().ToString("N"), [CSharpSyntaxTree.ParseText(implicitUsings), program], references,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, nullableContextOptions: NullableContextOptions.Enable));
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
    }
}
