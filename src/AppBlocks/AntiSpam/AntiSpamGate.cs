using System.Globalization;
using Umbraco.Cms.Core.Models.Blocks;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services;
using AppBlocks.Blocks;
using AppBlocks.Forms;

namespace AppBlocks.AntiSpam;

/// <summary>What an editor set on one Anti-spam block. It comes from the published page, so no request can change or remove it.</summary>
public sealed record AntiSpamBlock(Guid Key, string Service, string? SiteKey, decimal ScoreThreshold, string? Theme, string? Size)
{
    /// <summary>The built-in check, which has no service behind it, no keys and no third-party script.</summary>
    public const string Honeypot = "honeypot";
    public const decimal DefaultScoreThreshold = 0.5m;

    public bool IsHoneypot => Service.Equals(Honeypot, StringComparison.OrdinalIgnoreCase);

    /// <summary>The honeypot control: a dull name no browser fills in by itself, different for each block.</summary>
    public string HoneypotField => "reference-" + Key.ToString("N")[..12];

    public static AntiSpamBlock Read(BlockGridItem item) => new(item.ContentKey, item.Content.Value<string>("service")?.Trim() ?? "",
        item.Content.Value<string>("siteKey")?.Trim() is { Length: > 0 } siteKey ? siteKey : null,
        Threshold(item.Content.Value<string>("scoreThreshold")) ?? DefaultScoreThreshold,
        item.Content.Value<string>("theme")?.Trim(), item.Content.Value<string>("size")?.Trim());

    /// <summary>A threshold from 0 to 1, or null for anything else. Publishing refuses anything else, and a value that got past it means the default.</summary>
    public static decimal? Threshold(string? value) => decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold) && threshold is >= 0 and <= 1 ? threshold : null;

    public static bool IsSiteKey(string value) => value.Length <= 256 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');

    /// <summary>The honeypot and reCAPTCHA v3 show nothing, so a grid cell for them would only leave a gap.</summary>
    public static bool ShowsNothing(IPublishedElement block) => block.ContentType.Alias == AppBlockSchema.AntiSpamAlias
        && (block.Value<string>("service")?.Trim() is { } service && (service.Equals(Honeypot, StringComparison.OrdinalIgnoreCase) || service.Equals("recaptchaV3", StringComparison.OrdinalIgnoreCase)));
}

/// <param name="Refusal">What to answer the visitor, or null to go on to the class.</param>
/// <param name="FormData">The form's other controls without the check's own, which is what the class receives.</param>
public sealed record AntiSpamResult(FormResult? Refusal, IReadOnlyDictionary<string, string> FormData);

/// <summary>
/// Checks a submission against a form's Anti-spam blocks on the server, whatever the browser did or left out. It fails closed:
/// a missing token, an unknown service, a missing key or a service that cannot answer all refuse, and the class never runs.
/// </summary>
public sealed class AntiSpamGate(IEnumerable<IAntiSpamService> services, AntiSpamSettings settings, AntiSpamHealth health, IPreviewService previews,
    IHttpContextAccessor? requests = null)
{
    /// <summary>What a visitor sees when a check fails: the same for a token that fails, no token and a filled-in honeypot, so a bot learns nothing about which.</summary>
    public const string RefusedMessage = "We could not confirm this came from a person, so it was not sent. Please try again.";

    /// <summary>What a visitor sees when the check itself cannot be made, which is not their doing.</summary>
    public const string UnavailableMessage = "We cannot check submissions just now, so this was not sent. Please try again in a few minutes.";

    /// <summary>How long a visitor waits for a service.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>The registered services. A later registration with the same alias replaces an earlier one.</summary>
    public IReadOnlyList<IAntiSpamService> Services => [.. services.Reverse().DistinctBy(service => service.Alias, StringComparer.OrdinalIgnoreCase).Reverse()];

    public IAntiSpamService? Find(string alias) => Services.FirstOrDefault(service => service.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase));

    /// <summary>The block's own site key, or else the site's for that service.</summary>
    public string? SiteKey(AntiSpamBlock block) => block.SiteKey ?? settings.SiteKey(block.Service);

    public AntiSpamSecretState SecretState(string service) => settings.SecretState(service);

    /// <summary>Whether this is one of the site's own secret keys, so it can be kept out of a block where someone pasted it by mistake.</summary>
    public bool IsSecretKey(string value) => Services.Any(service => settings.SecretKey(service.Alias) == value);

    /// <summary>
    /// An editor looking at a preview. Anyone can send the preview cookie, so this asks Umbraco whether it holds a token Umbraco
    /// itself issued to a backoffice User, which still works when the backoffice is on another host and its sign-in cookie does
    /// not come along. Preview shows a note in place of a widget, so its submissions have no token to check.
    /// </summary>
    public bool IsPreview => previews.IsInPreview();

    /// <summary>What an editor reads in a preview, including anything that would turn visitors away.</summary>
    public string PreviewNote(AntiSpamBlock block)
    {
        if (block.IsHoneypot) return "Anti-spam: a honeypot protects this form. Visitors see nothing here.";
        if (Find(block.Service) is not { } service) return $"Anti-spam: '{block.Service}' is not a service this site offers, so visitors' submissions are refused.";
        if (SiteKey(block) is null) return $"Anti-spam: {service.Name} has no site key, so visitors' submissions are refused. Set one on this block or in Settings, AppBlocks, Anti-spam.";
        if (settings.SecretKey(service.Alias) is null) return $"Anti-spam: {service.Name} has no secret key yet, so visitors' submissions are refused. An administrator adds it in Settings, AppBlocks, Anti-spam.";
        return $"Anti-spam: visitors complete {service.Name} here. It is not loaded in preview, and a submission from preview skips it.";
    }

    /// <summary>The form's other controls without the ones its Anti-spam blocks use, so a token or honeypot never reaches the class.</summary>
    public IReadOnlyDictionary<string, string> WithoutChecks(FormDefinition form, IReadOnlyDictionary<string, string> formData)
    {
        var data = new Dictionary<string, string>(formData, StringComparer.OrdinalIgnoreCase);
        foreach (var block in form.AntiSpam.Select(AntiSpamBlock.Read))
            data.Remove(block.IsHoneypot ? block.HoneypotField : Find(block.Service)?.TokenField ?? "");
        return data;
    }

    public async Task<AntiSpamResult> CheckAsync(FormDefinition form, IReadOnlyDictionary<string, string> formData, CancellationToken ct)
    {
        var remaining = WithoutChecks(form, formData);
        if (form.AntiSpam.Count == 0 || IsPreview) return new(null, remaining);
        foreach (var block in form.AntiSpam.Select(AntiSpamBlock.Read))
        {
            var refusal = block.IsHoneypot ? Honeypot(block, formData) : await ServiceAsync(block, formData, ct);
            if (refusal is not null) return new(refusal, remaining);
        }
        return new(null, remaining);
    }

    private static FormResult Refused() => FormResult.Invalid("", RefusedMessage);

    /// <summary>A person's browser sends the control empty, so one that is filled in or missing is not a person's browser.</summary>
    private static FormResult? Honeypot(AntiSpamBlock block, IReadOnlyDictionary<string, string> formData)
        => formData.TryGetValue(block.HoneypotField, out var value) && value.Length == 0 ? null : Refused();

    private async Task<FormResult?> ServiceAsync(AntiSpamBlock block, IReadOnlyDictionary<string, string> formData, CancellationToken ct)
    {
        if (Find(block.Service) is not { } service) return Unavailable(block.Service, $"A form's Anti-spam block names '{block.Service}', which this site does not offer.");
        if (SiteKey(block) is not { } siteKey) return Unavailable(service.Alias, "No site key is set, on the form's block or for the site.");
        if (settings.SecretKey(service.Alias) is not { } secret)
            return Unavailable(service.Alias, settings.SecretState(service.Alias) == AntiSpamSecretState.Unreadable
                ? "The saved secret key cannot be read with this installation's Data Protection keys. Save it again." : "No secret key is set.");
        // No token, nothing to ask about: a bot that leaves it out costs the service no call.
        if (!formData.TryGetValue(service.TokenField, out var token) || string.IsNullOrWhiteSpace(token)) return Refused();
        var hostname = settings.VerifyHostname ? requests?.HttpContext?.Request.Host.Host : null;
        var answer = await AskAsync(service, new(token, secret, siteKey, block.ScoreThreshold, hostname), ct);
        if (answer.Outcome == AntiSpamOutcome.Unavailable)
            return Unavailable(service.Alias, (answer.Problem ?? $"{service.Name} gave no answer.").Replace(secret, "[secret key]").Replace(token, "[token]"));
        health.Clear(service.Alias);
        return answer.Outcome == AntiSpamOutcome.Passed ? null : Refused();
    }

    private FormResult Unavailable(string service, string problem)
    {
        health.Report(service, problem);
        return FormResult.Invalid("", UnavailableMessage);
    }

    private static async Task<AntiSpamAnswer> AskAsync(IAntiSpamService service, AntiSpamVerification request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try { return await service.VerifyAsync(request, timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return AntiSpamAnswer.Unavailable($"{service.Name} did not answer within {Timeout.TotalSeconds:0} seconds."); }
        // Only the kind of exception: its message could quote whatever a site's own adapter was doing.
        catch (Exception error) when (error is not OperationCanceledException) { return AntiSpamAnswer.Unavailable($"{service.Name} could not be asked ({error.GetType().Name})."); }
    }
}

/// <summary>The services the block's picker offers. Whether a secret key is set is all it says about one.</summary>
public sealed class AntiSpamCatalogue(AntiSpamGate gate) : IAppBlocksCatalogueContributor
{
    public Task PopulateAsync(IDictionary<string, object> catalogue, System.Security.Claims.ClaimsPrincipal user)
    {
        catalogue["antiSpamServices"] = new object[] { new { alias = AntiSpamBlock.Honeypot, name = "Honeypot (no account needed)", external = false, ready = true } }
            .Concat(gate.Services.Select(service => (object)new { alias = service.Alias, name = service.Name, external = true, ready = gate.SecretState(service.Alias) is AntiSpamSecretState.Configuration or AntiSpamSecretState.Saved }))
            .ToArray();
        return Task.CompletedTask;
    }
}

public static class AntiSpamExtensions
{
    public static IUmbracoBuilder AddAntiSpam(this IUmbracoBuilder builder)
    {
        var services = builder.Services;
        services.AddSingleton<AntiSpamSettings>();
        services.AddSingleton<AntiSpamHealth>();
        services.AddScoped<AntiSpamGate>();
        services.AddScoped<IAppBlocksCatalogueContributor, AntiSpamCatalogue>();
        // No redirects, so the secret goes only to the service's own address. An answer is a few hundred bytes.
        services.AddHttpClient(SiteVerifyService.ClientName, client => { client.Timeout = AntiSpamGate.Timeout; client.MaxResponseContentBufferSize = 64 * 1024; })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddSingleton<IAntiSpamService, TurnstileService>();
        services.AddSingleton<IAntiSpamService, RecaptchaV2Service>();
        services.AddSingleton<IAntiSpamService, RecaptchaV3Service>();
        services.AddSingleton<IAntiSpamService, HCaptchaService>();
        return builder;
    }
}
