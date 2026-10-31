using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace AppBlocks.AntiSpam;

/// <summary>Where a service's secret key comes from. Never the key itself.</summary>
public enum AntiSpamSecretState
{
    /// <summary>No secret key is set, so its forms refuse every submission.</summary>
    None,
    /// <summary>From <c>AppBlocks:AntiSpam:&lt;service&gt;:SecretKey</c>, which the screen cannot change.</summary>
    Configuration,
    /// <summary>Saved on the settings screen, encrypted for this installation.</summary>
    Saved,
    /// <summary>Saved, and this installation's Data Protection keys can no longer read it. Save it again.</summary>
    Unreadable,
}

public sealed record SaveAntiSpamKeys(string Service, string? SiteKey = null, string? SecretKey = null, bool RemoveSecret = false)
{
    /// <summary>Without the secret key: MVC writes an action's arguments to the log when tracing is on, and this is what it writes.</summary>
    public override string ToString() => $"{nameof(SaveAntiSpamKeys)} {{ {nameof(Service)} = {Service} }}";
}

internal sealed record StoredAntiSpamKeys(string Service, string? SiteKey, string? ProtectedSecretKey);

/// <summary>
/// A service's keys. Secret keys belong to the installation, never to content or an export, as connection strings do:
/// configuration comes first, and otherwise they are encrypted under this installation's Data Protection keys in
/// <c>App_Data/AppBlocks/anti-spam.json</c>. Nothing here hands a secret to anything but a verification.
/// </summary>
public sealed class AntiSpamSettings(IConfiguration configuration, IWebHostEnvironment environment, IDataProtectionProvider protection)
{
    public const string Section = "AppBlocks:AntiSpam";
    private readonly IDataProtector protector = protection.CreateProtector("AppBlocks.AntiSpam.v1");
    private readonly SemaphoreSlim gate = new(1, 1);
    private string FilePath => Path.Combine(configuration["AppBlocks:DataRoot"] ?? environment.ContentRootPath, "App_Data", "AppBlocks", "anti-spam.json");

    internal string? ConfiguredSecret(string service) => configuration[$"{Section}:{service}:SecretKey"] is { Length: > 0 } value ? value : null;
    internal string? ConfiguredSiteKey(string service) => configuration[$"{Section}:{service}:SiteKey"] is { Length: > 0 } value ? value : null;

    /// <summary>The secret key to verify with, or null when there is none to use.</summary>
    internal string? SecretKey(string service)
    {
        if (ConfiguredSecret(service) is { } configured) return configured;
        if (Stored(service)?.ProtectedSecretKey is not { } saved) return null;
        try { return protector.Unprotect(saved); }
        catch (CryptographicException) { return null; }
    }

    public AntiSpamSecretState SecretState(string service)
    {
        if (ConfiguredSecret(service) is not null) return AntiSpamSecretState.Configuration;
        if (Stored(service)?.ProtectedSecretKey is null) return AntiSpamSecretState.None;
        return SecretKey(service) is null ? AntiSpamSecretState.Unreadable : AntiSpamSecretState.Saved;
    }

    /// <summary>The site-wide site key, which a block's own replaces. Public.</summary>
    public string? SiteKey(string service) => ConfiguredSiteKey(service) ?? (Stored(service)?.SiteKey is { Length: > 0 } saved ? saved : null);

    public async Task SaveAsync(SaveAntiSpamKeys request, CancellationToken ct)
    {
        var secret = request.SecretKey?.Trim();
        if (!string.IsNullOrEmpty(secret) && ConfiguredSecret(request.Service) is not null) throw new ArgumentException("This secret key is set in the site's configuration, so it cannot be changed here.");
        if (secret is { Length: > 1024 }) throw new ArgumentException("A secret key is at most 1024 characters.");
        var siteKey = request.SiteKey?.Trim();
        if (siteKey is { Length: > 0 } && !AntiSpamBlock.IsSiteKey(siteKey)) throw new ArgumentException("A site key holds letters, digits, hyphens, underscores, dots and colons.");
        if (siteKey is { Length: > 0 } && siteKey == (string.IsNullOrEmpty(secret) ? SecretKey(request.Service) : secret)) throw new ArgumentException("That is the secret key. Enter the public site key instead.");
        await gate.WaitAsync(ct);
        try
        {
            var entries = Read().ToList();
            var old = entries.SingleOrDefault(entry => entry.Service.Equals(request.Service, StringComparison.OrdinalIgnoreCase));
            var saved = request.RemoveSecret ? null : string.IsNullOrEmpty(secret) ? old?.ProtectedSecretKey : protector.Protect(secret);
            entries.RemoveAll(entry => entry.Service.Equals(request.Service, StringComparison.OrdinalIgnoreCase));
            entries.Add(new(request.Service, siteKey ?? old?.SiteKey, saved));
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(entries), ct);
                File.Move(temporary, FilePath, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { gate.Release(); }
    }

    private StoredAntiSpamKeys? Stored(string service) => Read().FirstOrDefault(entry => entry.Service.Equals(service, StringComparison.OrdinalIgnoreCase));
    private StoredAntiSpamKeys[] Read() => File.Exists(FilePath) ? JsonSerializer.Deserialize<StoredAntiSpamKeys[]>(File.ReadAllText(FilePath)) ?? [] : [];
}

/// <summary>What is currently wrong with each service, which the settings screen shows. Each problem is logged once, however many visitors it turns away.</summary>
public sealed class AntiSpamHealth(ILogger<AntiSpamHealth> logger)
{
    public sealed record Problem(string Message, DateTimeOffset Since);

    private readonly ConcurrentDictionary<string, Problem> current = new(StringComparer.OrdinalIgnoreCase);

    public Problem? Current(string service) => current.GetValueOrDefault(service);

    public void Report(string service, string message)
    {
        var problem = new Problem(message, DateTimeOffset.UtcNow);
        var reported = current.AddOrUpdate(service, problem, (_, old) => old.Message == message ? old : problem);
        if (ReferenceEquals(reported, problem)) logger.LogError("Anti-spam service {Service}: {Problem} Forms that use it refuse every submission until this is fixed.", service, message);
    }

    /// <summary>The service gave an answer, or its keys changed, so whatever was wrong is over.</summary>
    public void Clear(string service)
    {
        if (current.TryRemove(service, out _)) logger.LogInformation("Anti-spam service {Service} is working again.", service);
    }
}
