using AppBlocks.AntiSpam;
using Microsoft.Extensions.DependencyInjection;

namespace AppBlocks.Tests;

/// <summary>
/// The adapters against the real services, with the test keys each one publishes. These need the network, so an ordinary run
/// leaves them out. Run them with:
/// <c>dotnet run --project tests/AppBlocks.Tests -- -noLogo -explicit only -class AppBlocks.Tests.AntiSpamRealServiceTests</c>
/// </summary>
public sealed class AntiSpamRealServiceTests
{
    private static IHttpClientFactory Clients()
    {
        // The client the block really uses: its handler, its timeout and its limit on an answer.
        var services = new ServiceCollection();
        services.AddHttpClient(SiteVerifyService.ClientName, client => { client.Timeout = AntiSpamGate.Timeout; client.MaxResponseContentBufferSize = 64 * 1024; })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
    }

    private static async Task<AntiSpamAnswer> Ask(IAntiSpamService service, string siteKey, string secret, string token)
    {
        using var timeout = new CancellationTokenSource(AntiSpamGate.Timeout);
        return await service.VerifyAsync(new(token, secret, siteKey, AntiSpamBlock.DefaultScoreThreshold), timeout.Token);
    }

    [Fact(Explicit = true)]
    public async Task Turnstile_test_keys()
    {
        var service = new TurnstileService(Clients());
        var (siteKey, secret, token) = AntiSpamRealKeys.Passing["turnstile"];
        Assert.Equal(AntiSpamAnswer.Passed, await Ask(service, siteKey, secret, token));
        // Cloudflare's published always-fails and token-already-spent secrets.
        Assert.Equal(AntiSpamAnswer.Failed, await Ask(service, siteKey, "2x0000000000000000000000000000000AA", token));
        Assert.Equal(AntiSpamAnswer.Failed, await Ask(service, siteKey, "3x0000000000000000000000000000000AA", token));
        // A secret that is not one is the site's problem, not the visitor's.
        var wrong = await Ask(service, siteKey, "not-a-real-secret", token);
        Assert.Equal(AntiSpamOutcome.Unavailable, wrong.Outcome);
        Assert.Contains("invalid-input-secret", wrong.Problem);
    }

    [Fact(Explicit = true)]
    public async Task ReCAPTCHA_test_keys()
    {
        var clients = Clients();
        var (siteKey, secret, token) = AntiSpamRealKeys.Passing["recaptchaV2"];
        Assert.Equal(AntiSpamAnswer.Passed, await Ask(new RecaptchaV2Service(clients), siteKey, secret, token));
        // With a made-up token as well, Google blames the token rather than the secret, so this only shows a wrong secret never passes.
        Assert.NotEqual(AntiSpamOutcome.Passed, (await Ask(new RecaptchaV2Service(clients), siteKey, "not-a-real-secret", token)).Outcome);
        // Google publishes no v3 test keys. Its v2 test key gives no score, which v3 reports as the wrong keys rather than letting through.
        var three = await Ask(new RecaptchaV3Service(clients), siteKey, secret, token);
        Assert.Equal(AntiSpamOutcome.Unavailable, three.Outcome);
        Assert.Contains("no score", three.Problem);
    }

    [Fact(Explicit = true)]
    public async Task HCaptcha_test_keys()
    {
        var service = new HCaptchaService(Clients());
        var (siteKey, secret, token) = AntiSpamRealKeys.Passing["hcaptcha"];
        Assert.Equal(AntiSpamAnswer.Passed, await Ask(service, siteKey, secret, token));
        Assert.Equal(AntiSpamAnswer.Failed, await Ask(service, siteKey, secret, "not-a-real-token"));
        Assert.Equal(AntiSpamOutcome.Unavailable, (await Ask(service, siteKey, "not-a-real-secret", token)).Outcome);
    }
}
