# Anti-spam

Editors protect a form with no code by placing an **Anti-spam** block inside it. The Forms container's grid offers it on plain sites, because unlike HTML it runs nothing an editor writes. They choose Cloudflare Turnstile, Google reCAPTCHA v2 or v3 (with a score threshold, 0.5 by default), hCaptcha, or a built-in honeypot that needs no account. The block renders the service's widget and script. AppBlocks checks every create and update on the server before the form's class is called, and a request can't leave the check out. A failed check returns a message to the visitor. If the service is unreachable or a key is missing, the submission is refused with a friendly message and the problem is logged once and shown on the settings screen.

Site keys are public and can go on the block. Secret keys stay on the server, in configuration (`AppBlocks:AntiSpam:<service>:SecretKey`) or encrypted in **Settings → AppBlocks → Anti-spam**. They are never in block content, exports, API responses or logs.

**The host is checked.** Turnstile, reCAPTCHA and hCaptcha say which host a token was solved on, and AppBlocks refuses a token solved on a host other than the one the form was sent to, so a token from another site that uses the same keys does not pass. Set `AppBlocks:AntiSpam:VerifyHostname` to `false` when the site cannot see its public host name, behind a proxy that does not forward it, or while using a service's published test keys, whose answers name the service's own test host.

**Privacy.** Turnstile, reCAPTCHA and hCaptcha load third-party scripts in the visitor's browser and may need cookie consent on some sites. Consent handling stays with the site. A site with its own Content-Security-Policy has to allow the scripts. The honeypot loads nothing.

**This isn't a rate limit.** Anyone can still call a public form as often as they like, so a site exposed to the internet should also limit the form endpoints, `/api/forms` unless `AppBlocks:ApiRoot` moves them. The Anti-spam section of the [business rules guide](business-rules.md) has a short recipe for ASP.NET Core's rate limiter, along with the service details, the order of checks, the hosts a Content-Security-Policy needs and how to register another service. Form submissions are limited to 1 MB; `AppBlocks:Forms:MaxBodySize` changes that.

The tests stand in for the services and need no network. To try the real ones with the test keys each service publishes:

```powershell
dotnet run --project tests/AppBlocks.Tests -- -noLogo -explicit only -class AppBlocks.Tests.AntiSpamRealServiceTests
$env:APPBLOCKS_ANTISPAM_REAL = "1"; node --test tests/ui/anti-spam.browser.test.mjs
```
