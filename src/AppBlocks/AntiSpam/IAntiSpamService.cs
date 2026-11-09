namespace AppBlocks.AntiSpam;

public enum AntiSpamOutcome
{
    /// <summary>The service vouches for the token.</summary>
    Passed,
    /// <summary>The service answered and does not vouch for the token.</summary>
    Failed,
    /// <summary>The service could not give an answer, or refused to because of how the site is set up. The visitor is not to blame.</summary>
    Unavailable,
}

/// <param name="Problem">For <see cref="AntiSpamOutcome.Unavailable"/>, what an administrator needs to know. It is logged and shown on the settings screen, so it never quotes a secret key or a token.</param>
public sealed record AntiSpamAnswer(AntiSpamOutcome Outcome, string? Problem = null)
{
    public static readonly AntiSpamAnswer Passed = new(AntiSpamOutcome.Passed);
    public static readonly AntiSpamAnswer Failed = new(AntiSpamOutcome.Failed);
    public static AntiSpamAnswer Unavailable(string problem) => new(AntiSpamOutcome.Unavailable, problem);
}

/// <summary>What an editor chose on the block, for the service to turn into its widget.</summary>
/// <param name="Theme">auto, light or dark, or null. A service offers what it can of these.</param>
/// <param name="Size">normal or compact, or null.</param>
public sealed record AntiSpamOptions(string SiteKey, string? Theme = null, string? Size = null);

/// <summary>One token to check. The secret key comes from the server's configuration or settings, never from content or the browser.</summary>
/// <param name="Hostname">
/// The host name the form was submitted to, which a service compares with the host its widget was solved on, so a token solved on
/// another site is refused. Null when the site sets <c>AppBlocks:AntiSpam:VerifyHostname</c> to false.
/// </param>
public sealed record AntiSpamVerification(string Token, string SecretKey, string SiteKey, decimal ScoreThreshold, string? Hostname = null)
{
    /// <summary>Without the token or the secret key, so an adapter that logs this gives neither away.</summary>
    public override string ToString() => $"{nameof(AntiSpamVerification)} {{ {nameof(SiteKey)} = {SiteKey} }}";
}

/// <summary>
/// What the block renders for a service. The runtime waits for the global <paramref name="BrowserApi"/> and calls its
/// <c>render(element, { sitekey, ...RenderOptions })</c>, then its <c>reset(id)</c> after each submission, because a token
/// works once. With <paramref name="ExecuteAction"/> the service shows no widget: the runtime asks for a token as the form
/// is submitted, with <c>execute(siteKey, { action })</c>.
/// </summary>
/// <param name="ScriptUrl">The service's own script, written by the adapter and not by content. A page loads each address once.</param>
public sealed record AntiSpamWidget(string ScriptUrl, string BrowserApi, IReadOnlyDictionary<string, string>? RenderOptions = null, string? ExecuteAction = null);

/// <summary>
/// One external anti-spam service. Register more with <c>AddSingleton&lt;IAntiSpamService, YourService&gt;()</c>: the block's
/// service picker offers it, and a later registration with the same alias replaces an earlier one. Derive from
/// <see cref="SiteVerifyService"/> when the service follows the usual <c>siteverify</c> shape. The built-in honeypot is not one
/// of these, because it has no service behind it.
/// </summary>
public interface IAntiSpamService
{
    /// <summary>What the block stores, and the section of <c>AppBlocks:AntiSpam</c> holding the service's keys. Letters and digits.</summary>
    string Alias { get; }

    /// <summary>What editors and administrators see.</summary>
    string Name { get; }

    /// <summary>The name of the form control holding the token, which the widget adds inside the form.</summary>
    string TokenField { get; }

    AntiSpamWidget Widget(AntiSpamOptions options);

    /// <summary>
    /// Asks the service about a token, within the time <paramref name="ct"/> allows. Post only to the service's own fixed
    /// address, and answer <see cref="AntiSpamOutcome.Unavailable"/> rather than throw when it cannot be asked.
    /// </summary>
    Task<AntiSpamAnswer> VerifyAsync(AntiSpamVerification request, CancellationToken ct);
}
