using System.Text.Json;

namespace AppBlocks.AntiSpam;

/// <summary>
/// A service that follows the shape Turnstile, reCAPTCHA and hCaptcha share: the secret and the token posted as a form to one
/// fixed address, and a JSON answer with <c>success</c>, <c>error-codes</c> and <c>hostname</c>. The address belongs to the class, so
/// content can never choose where the secret goes, and the client follows no redirects for the same reason. A successful answer whose
/// <c>hostname</c> differs from <see cref="AntiSpamVerification.Hostname"/> fails, as the services advise for a key that is not limited
/// to the site's domains.
/// </summary>
public abstract class SiteVerifyService(IHttpClientFactory clients) : IAntiSpamService
{
    /// <summary>The <see cref="IHttpClientFactory"/> client these use.</summary>
    public const string ClientName = "AppBlocks.AntiSpam";

    /// <summary>What a service says about the token, as opposed to about the site's keys or itself.</summary>
    private static readonly HashSet<string> TokenCodes = ["missing-input-response", "invalid-input-response", "timeout-or-duplicate",
        "expired-input-response", "already-seen-response", "invalid-or-already-seen-response"];

    public abstract string Alias { get; }
    public abstract string Name { get; }
    public abstract string TokenField { get; }
    protected abstract Uri VerifyUrl { get; }
    public abstract AntiSpamWidget Widget(AntiSpamOptions options);

    /// <summary>The form posted to <see cref="VerifyUrl"/>. The visitor's address is left out: the service does not need it to answer.</summary>
    protected virtual IEnumerable<KeyValuePair<string, string>> Fields(AntiSpamVerification request) =>
        [new("secret", request.SecretKey), new("response", request.Token)];

    /// <summary>The answer to a <c>success</c> reply, for a service with more to say than that.</summary>
    protected virtual AntiSpamAnswer Judge(JsonElement reply, AntiSpamVerification request) => AntiSpamAnswer.Passed;

    public async Task<AntiSpamAnswer> VerifyAsync(AntiSpamVerification request, CancellationToken ct)
    {
        try
        {
            using var client = clients.CreateClient(ClientName);
            using var content = new FormUrlEncodedContent(Fields(request));
            using var response = await client.PostAsync(VerifyUrl, content, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                // Turnstile answers a request it refuses with a 400 and its usual error codes, which say more than the status does. Nothing but a 2xx ever passes.
                var refused = Codes(text);
                return refused.Length == 0 ? AntiSpamAnswer.Unavailable($"{Name} answered with HTTP status {(int)response.StatusCode}.") : Refused(refused);
            }
            using var reply = JsonDocument.Parse(text);
            if (reply.RootElement.ValueKind != JsonValueKind.Object || !reply.RootElement.TryGetProperty("success", out var success) || success.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return Unreadable();
            if (!success.GetBoolean()) return Refused(Codes(text));
            return SolvedElsewhere(reply.RootElement, request) ? AntiSpamAnswer.Failed : Judge(reply.RootElement, request);
        }
        catch (HttpRequestException) { return AntiSpamAnswer.Unavailable($"{Name} could not be reached."); }
        catch (JsonException) { return Unreadable(); }
    }

    /// <summary>Whether the answer names a host other than the one the form was sent to. An answer with no host name, as for an app rather than a page, has nothing to compare.</summary>
    private static bool SolvedElsewhere(JsonElement reply, AntiSpamVerification request)
        => request.Hostname is { Length: > 0 } expected && reply.TryGetProperty("hostname", out var hostname) && hostname.ValueKind == JsonValueKind.String
            && hostname.GetString() is { Length: > 0 } solved && !solved.TrimEnd('.').Equals(expected.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    private AntiSpamAnswer Refused(string[] codes) => codes.All(TokenCodes.Contains) ? AntiSpamAnswer.Failed : AntiSpamAnswer.Unavailable(Explain(codes));

    private static string[] Codes(string reply)
    {
        try
        {
            using var document = JsonDocument.Parse(reply);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error-codes", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(code => code.ValueKind == JsonValueKind.String).Select(code => code.GetString()!).ToArray() : [];
        }
        catch (JsonException) { return []; }
    }

    private AntiSpamAnswer Unreadable() => AntiSpamAnswer.Unavailable($"{Name} sent an answer AppBlocks could not read.");

    private string Explain(string[] codes)
    {
        // Only what looks like one of their codes is repeated, since it ends up in a log and on a screen.
        var named = string.Join(", ", codes.Where(code => code.Length <= 64 && code.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')));
        return codes.Any(code => code.Contains("secret", StringComparison.OrdinalIgnoreCase) || code.Contains("sitekey", StringComparison.OrdinalIgnoreCase))
            ? $"{Name} refused the site's keys ({named}). Check the secret key and that it belongs to the same site as the site key."
            : $"{Name} refused the request ({named}).";
    }

    protected static string? Allowed(string? value, params string[] allowed) => allowed.Contains(value) ? value : null;

    /// <summary>The options that have a value, for <see cref="AntiSpamWidget.RenderOptions"/>.</summary>
    protected static IReadOnlyDictionary<string, string> Options(params (string Name, string? Value)[] options) =>
        options.Where(option => option.Value is not null).ToDictionary(option => option.Name, option => option.Value!);
}

/// <summary>Cloudflare Turnstile.</summary>
public sealed class TurnstileService(IHttpClientFactory clients) : SiteVerifyService(clients)
{
    public override string Alias => "turnstile";
    public override string Name => "Cloudflare Turnstile";
    public override string TokenField => "cf-turnstile-response";
    protected override Uri VerifyUrl { get; } = new("https://challenges.cloudflare.com/turnstile/v0/siteverify");
    public override AntiSpamWidget Widget(AntiSpamOptions options) => new("https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit", "turnstile",
        Options(("theme", Allowed(options.Theme, "auto", "light", "dark")), ("size", Allowed(options.Size, "normal", "compact"))));
}

/// <summary>Google reCAPTCHA v2, the "I'm not a robot" checkbox.</summary>
public sealed class RecaptchaV2Service(IHttpClientFactory clients) : SiteVerifyService(clients)
{
    public override string Alias => "recaptchaV2";
    public override string Name => "Google reCAPTCHA v2";
    public override string TokenField => "g-recaptcha-response";
    protected override Uri VerifyUrl { get; } = new("https://www.google.com/recaptcha/api/siteverify");
    public override AntiSpamWidget Widget(AntiSpamOptions options) => new("https://www.google.com/recaptcha/api.js?render=explicit", "grecaptcha",
        Options(("theme", Allowed(options.Theme, "light", "dark")), ("size", Allowed(options.Size, "normal", "compact"))));
}

/// <summary>Google reCAPTCHA v3: no widget, and a score against the block's threshold.</summary>
public sealed class RecaptchaV3Service(IHttpClientFactory clients) : SiteVerifyService(clients)
{
    /// <summary>The action the runtime asks for and the answer has to repeat, so a token from another page's action is not enough.</summary>
    public const string Action = "appblocks_form";

    public override string Alias => "recaptchaV3";
    public override string Name => "Google reCAPTCHA v3";
    public override string TokenField => "g-recaptcha-response";
    protected override Uri VerifyUrl { get; } = new("https://www.google.com/recaptcha/api/siteverify");
    public override AntiSpamWidget Widget(AntiSpamOptions options) => new("https://www.google.com/recaptcha/api.js?render=" + Uri.EscapeDataString(options.SiteKey), "grecaptcha", ExecuteAction: Action);

    protected override AntiSpamAnswer Judge(JsonElement reply, AntiSpamVerification request)
    {
        // A v2 key answers success with no score, which says nothing about this visitor and means the block has the wrong keys.
        if (!reply.TryGetProperty("score", out var score) || !score.TryGetDecimal(out var value))
            return AntiSpamAnswer.Unavailable($"{Name} sent no score. Check that the keys are reCAPTCHA v3 keys.");
        if (reply.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.String && action.GetString() != Action) return AntiSpamAnswer.Failed;
        return value >= request.ScoreThreshold ? AntiSpamAnswer.Passed : AntiSpamAnswer.Failed;
    }
}

/// <summary>hCaptcha. Its reCAPTCHA compatibility is off, so it adds one token control and no <c>grecaptcha</c> global.</summary>
public sealed class HCaptchaService(IHttpClientFactory clients) : SiteVerifyService(clients)
{
    public override string Alias => "hcaptcha";
    public override string Name => "hCaptcha";
    public override string TokenField => "h-captcha-response";
    protected override Uri VerifyUrl { get; } = new("https://api.hcaptcha.com/siteverify");
    public override AntiSpamWidget Widget(AntiSpamOptions options) => new("https://js.hcaptcha.com/1/api.js?render=explicit&recaptchacompat=off", "hcaptcha",
        Options(("theme", Allowed(options.Theme, "light", "dark")), ("size", Allowed(options.Size, "normal", "compact"))));
    protected override IEnumerable<KeyValuePair<string, string>> Fields(AntiSpamVerification request) => [.. base.Fields(request), new("sitekey", request.SiteKey)];
}
