using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Pressroom.Contracts;

namespace AppBlocks.Documents;

public sealed record DocumentSettingsInfo(string? PressroomUrl, bool HasApiKey, double TimeoutSeconds, bool Saved);
public sealed record SaveDocumentSettings(string? PressroomUrl, string? ApiKey = null,
    bool ClearApiKey = false, double TimeoutSeconds = 65);
internal sealed record StoredDocumentSettings(string? PressroomUrl, string? ProtectedApiKey, double TimeoutSeconds);
internal sealed record PressroomSettings(string? Url, string? ApiKey, double TimeoutSeconds)
{
    public static PressroomSettings FromConfiguration(IConfiguration configuration)
    {
        string? Value(string key) => configuration["AppBlocks:Documents:Pressroom:" + key] ?? configuration["Pressroom:" + key];
        return new(Value("Url"), Value("ApiKey"),
            double.TryParse(Value("TimeoutSeconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out var timeout) ? timeout : 65);
    }
}

/// <summary>
/// Installation settings, outside content and uSync. A saved blank URL deliberately disables configured
/// Pressroom. Secrets are protected with the site's Data Protection keys and never returned by the API.
/// </summary>
public sealed class DocumentSettingsStore(IConfiguration configuration, IWebHostEnvironment environment,
    IDataProtectionProvider protection)
{
    private readonly IDataProtector protector = protection.CreateProtector("AppBlocks.Documents.Settings.v1");
    private readonly SemaphoreSlim gate = new(1, 1);
    internal static readonly Guid LegacyId = new("3d49f9bd-ec0d-4397-9017-689245984630");
    private string FilePath => Path.Combine(configuration["AppBlocks:DataRoot"] ?? configuration["UmbracoAppHost:DataRoot"]
        ?? environment.ContentRootPath, "App_Data", "AppBlocks", "documents.json");

    public PressroomConfigurationList List() => new(ReadConfigurations().Where(c => !string.IsNullOrWhiteSpace(c.Url)).Select(Info).ToArray(), File.Exists(FilePath));
    private static PressroomConfigurationInfo Info(StoredPressroomConfiguration item)
        => new(item.Id, item.Name, item.Url, item.ProtectedApiKey is not null, item.Output, item.TimeoutSeconds);

    // Compatibility access to the original single PDF configuration. New consumers use the collection.
    public DocumentSettingsInfo Get()
    {
        var item = ReadConfigurations().FirstOrDefault(c => c.Output == "pdf");
        return new(string.IsNullOrWhiteSpace(item?.Url) ? null : item.Url, item?.ProtectedApiKey is not null, item?.TimeoutSeconds ?? 65, File.Exists(FilePath));
    }

    internal PressroomSettings Resolve()
    {
        var item = ReadConfigurations().FirstOrDefault(c => c.Output == "pdf") is { } stored ? Resolve(stored) : null;
        return new(string.IsNullOrWhiteSpace(item?.Url) ? null : item.Url, item?.ApiKey, item?.TimeoutSeconds ?? 65);
    }

    internal ResolvedPressroomConfiguration? DefaultPdf()
        => ReadConfigurations().FirstOrDefault(c => c.Output == "pdf" && !string.IsNullOrWhiteSpace(c.Url)) is { } item ? Resolve(item) : null;
    internal ResolvedPressroomConfiguration Resolve(Guid id)
        => Resolve(ReadConfigurations().SingleOrDefault(c => c.Id == id) ?? throw new KeyNotFoundException("The selected document configuration no longer exists."));
    private ResolvedPressroomConfiguration Resolve(StoredPressroomConfiguration item)
        => new(item.Id, item.Name, item.Url, item.ProtectedApiKey is null ? null : protector.Unprotect(item.ProtectedApiKey), item.Output, item.TimeoutSeconds);

    public async Task<PressroomConfigurationInfo> SaveConfigurationAsync(SavePressroomConfiguration request, CancellationToken ct = default)
    {
        ValidateConnection(request.Url, request.TimeoutSeconds);
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 100)
            throw new ArgumentException("Enter a configuration name of at most 100 characters.");
        if (request.Output != "pdf" && !(request.Output?.StartsWith("printer:", StringComparison.Ordinal) == true
            && RequestValidator.IsPrinterIdentifier(request.Output[8..])))
            throw new ArgumentException("Choose PDF or a printer returned by Pressroom.");
        await gate.WaitAsync(ct);
        try
        {
            var items = ReadConfigurations();
            var existing = Existing(items, request.Id);
            var key = ApiKey(existing, request.Url, request.ApiKey, request.ClearApiKey);
            if (request.Output != "pdf" && string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Printer output requires a Pressroom API key.");
            if (items.Any(c => c.Id != existing?.Id && c.Name.Equals(request.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("A configuration with that name already exists.");
            var item = new StoredPressroomConfiguration(existing?.Id ?? Guid.NewGuid(), request.Name.Trim(), request.Url.Trim(),
                key is null ? null : protector.Protect(key), request.Output, request.TimeoutSeconds);
            if (existing is null) items.Add(item); else items[items.IndexOf(existing)] = item;
            await WriteAsync(items, ct);
            return Info(item);
        }
        finally { gate.Release(); }
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var items = ReadConfigurations();
            if (items.RemoveAll(c => c.Id == id) == 0) throw new KeyNotFoundException("The selected document configuration no longer exists.");
            // Persist the empty collection, so deleted configuration defaults do not reappear.
            await WriteAsync(items, ct);
        }
        finally { gate.Release(); }
    }

    internal ResolvedPressroomConfiguration ForDiscovery(DiscoverPressroomPrinters request)
    {
        ValidateConnection(request.Url, request.TimeoutSeconds);
        var existing = Existing(ReadConfigurations(), request.Id);
        return new(existing?.Id ?? Guid.Empty, existing?.Name ?? "Unsaved configuration", request.Url.Trim(),
            ApiKey(existing, request.Url, request.ApiKey, request.ClearApiKey), "pdf", request.TimeoutSeconds);
    }

    private static StoredPressroomConfiguration? Existing(List<StoredPressroomConfiguration> items, Guid? id)
        => id is null ? null : items.SingleOrDefault(c => c.Id == id)
            ?? throw new KeyNotFoundException("The selected document configuration no longer exists.");

    private string? ApiKey(StoredPressroomConfiguration? existing, string url, string? supplied, bool clear)
    {
        if (clear) return null;
        if (!string.IsNullOrWhiteSpace(supplied))
        {
            var key = supplied.Trim();
            if (key.Length > 4096 || key.Any(char.IsControl)) throw new ArgumentException("Enter a single-line API key of at most 4096 characters.");
            return key;
        }
        if (existing?.ProtectedApiKey is null) return null;
        if (!existing.Url.TrimEnd('/').Equals(url.Trim().TrimEnd('/'), StringComparison.Ordinal))
            throw new ArgumentException("Enter the API key for the new URL, or choose to remove the existing key.");
        return protector.Unprotect(existing.ProtectedApiKey);
    }

    private static void ValidateConnection(string url, double timeout)
    {
        if (string.IsNullOrWhiteSpace(url) || !ValidAddress(url.Trim()))
            throw new ArgumentException("Enter an absolute HTTP or HTTPS Pressroom URL without credentials, a query or a fragment.");
        if (!ValidTimeout(timeout)) throw new ArgumentException("Enter a positive timeout of at most 2147483 seconds.");
    }

    public async Task SaveAsync(SaveDocumentSettings request, CancellationToken ct = default)
    {
        var url = request.PressroomUrl?.Trim();
        if (string.IsNullOrEmpty(url)) url = null;
        if (url is not null && !ValidAddress(url))
            throw new ArgumentException("Enter an absolute HTTP or HTTPS Pressroom URL without credentials, a query or a fragment, or leave it empty to use HTML.");
        if (!ValidTimeout(request.TimeoutSeconds)) throw new ArgumentException("Enter a positive timeout of at most 2147483 seconds.");
        await gate.WaitAsync(ct);
        try
        {
            var items = ReadConfigurations();
            var existing = items.FirstOrDefault(c => c.Id == LegacyId);
            var key = request.ClearApiKey ? null : string.IsNullOrWhiteSpace(request.ApiKey)
                ? existing?.ProtectedApiKey is { } secret ? protector.Unprotect(secret) : null : request.ApiKey.Trim();
            items.RemoveAll(c => c.Id == LegacyId);
            // Retain the old blank-URL record solely for the legacy accessor. It is not a destination.
            items.Insert(0, new(LegacyId, "Pressroom PDF", url ?? "", key is null ? null : protector.Protect(key), "pdf", request.TimeoutSeconds));
            await WriteAsync(items, ct);
        }
        finally { gate.Release(); }
    }

    internal static bool ValidAddress(string url) => Uri.TryCreate(url, UriKind.Absolute, out var address)
        && address.Scheme is "http" or "https" && address.UserInfo.Length == 0 && address.Query.Length == 0 && address.Fragment.Length == 0;
    internal static bool ValidTimeout(double seconds) => double.IsFinite(seconds) && seconds is > 0 and <= int.MaxValue / 1000d;
    private async Task WriteAsync(List<StoredPressroomConfiguration> items, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new StoredPressroomConfigurations(2, items)), ct);
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private List<StoredPressroomConfiguration> ReadConfigurations()
    {
        if (!File.Exists(FilePath))
        {
            var defaults = PressroomSettings.FromConfiguration(configuration);
            return string.IsNullOrWhiteSpace(defaults.Url) ? [] : [new(LegacyId, "Pressroom PDF", defaults.Url,
                string.IsNullOrWhiteSpace(defaults.ApiKey) ? null : protector.Protect(defaults.ApiKey), "pdf", defaults.TimeoutSeconds)];
        }
        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var json = JsonDocument.Parse(stream);
        if (json.RootElement.TryGetProperty("Version", out var version))
        {
            if (version.GetInt32() != 2) throw new JsonException("Unsupported document settings version.");
            return json.RootElement.Deserialize<StoredPressroomConfigurations>()?.Configurations ?? throw new JsonException("Missing Pressroom configurations.");
        }
        var legacy = json.RootElement.Deserialize<StoredDocumentSettings>() ?? throw new JsonException("Invalid document settings.");
        return [new(LegacyId, "Pressroom PDF", legacy.PressroomUrl ?? "", legacy.ProtectedApiKey, "pdf", legacy.TimeoutSeconds)];
    }
}
