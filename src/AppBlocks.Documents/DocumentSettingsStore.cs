using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Pressroom.Contracts;

namespace AppBlocks.Documents;

/// <summary>
/// Installation settings, outside content and schema definitions. Until settings are saved, configuration under
/// <c>AppBlocks:Documents:Pressroom</c> (<c>Url</c>, <c>ApiKey</c>, <c>TimeoutSeconds</c>) supplies one PDF configuration.
/// Secrets are protected with the site's Data Protection keys and never returned by the API.
/// </summary>
public sealed class DocumentSettingsStore(IConfiguration configuration, IWebHostEnvironment environment,
    IDataProtectionProvider protection)
{
    private const string ConfigurationSection = "AppBlocks:Documents:Pressroom:";
    private readonly IDataProtector protector = protection.CreateProtector("AppBlocks.Documents.Settings.v1");
    private readonly SemaphoreSlim gate = new(1, 1);
    /// <summary>The id of the PDF configuration that site configuration supplies, which keeps it once saved.</summary>
    internal static readonly Guid ConfiguredId = new("3d49f9bd-ec0d-4397-9017-689245984630");
    private string FilePath => Path.Combine(configuration["AppBlocks:DataRoot"]
        ?? environment.ContentRootPath, "App_Data", "AppBlocks", "documents.json");

    public PressroomConfigurationList List() => new(ReadConfigurations().Where(c => !string.IsNullOrWhiteSpace(c.Url)).Select(Info).ToArray(), File.Exists(FilePath));
    private static PressroomConfigurationInfo Info(StoredPressroomConfiguration item)
        => new(item.Id, item.Name, item.Url, item.ProtectedApiKey is not null, item.Output, item.TimeoutSeconds);

    internal ResolvedPressroomConfiguration? DefaultPdf()
        => ReadConfigurations().FirstOrDefault(c => c.Output == "pdf" && !string.IsNullOrWhiteSpace(c.Url)) is { } item ? Resolve(item) : null;
    internal ResolvedPressroomConfiguration Resolve(Guid id)
    {
        var item = ReadConfigurations().SingleOrDefault(c => c.Id == id) ?? throw new KeyNotFoundException("The selected document configuration no longer exists.");
        // A hand-edited file can hold any output; it is not a destination until it is PDF or a printer again.
        return ValidOutput(item.Output) ? Resolve(item)
            : throw new KeyNotFoundException($"The document configuration {item.Name} has an output that is neither PDF nor a printer. Choose its output again in Settings.");
    }

    /// <summary>"pdf", or "printer:" and a printer identifier, as a save accepts and the file should hold.</summary>
    internal static bool ValidOutput(string? output) => output == "pdf"
        || output?.StartsWith("printer:", StringComparison.Ordinal) == true && RequestValidator.IsPrinterIdentifier(output[8..]);
    private ResolvedPressroomConfiguration Resolve(StoredPressroomConfiguration item)
        => new(item.Id, item.Name, item.Url, item.ProtectedApiKey is null ? null : protector.Unprotect(item.ProtectedApiKey), item.Output, item.TimeoutSeconds);

    public async Task<PressroomConfigurationInfo> SaveConfigurationAsync(SavePressroomConfiguration request, CancellationToken ct = default)
    {
        ValidateConnection(request.Url, request.TimeoutSeconds);
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 100)
            throw new ArgumentException("Enter a configuration name of at most 100 characters.");
        if (!ValidOutput(request.Output)) throw new ArgumentException("Choose PDF or a printer returned by Pressroom.");
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
            var url = configuration[ConfigurationSection + "Url"];
            var key = configuration[ConfigurationSection + "ApiKey"];
            var timeout = double.TryParse(configuration[ConfigurationSection + "TimeoutSeconds"], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : 65;
            return string.IsNullOrWhiteSpace(url) ? [] : [new(ConfiguredId, "Pressroom PDF", url,
                string.IsNullOrWhiteSpace(key) ? null : protector.Protect(key), "pdf", timeout)];
        }
        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var json = JsonDocument.Parse(stream);
        if (!json.RootElement.TryGetProperty("Version", out var version) || version.GetInt32() != 2) throw new JsonException("Unsupported document settings version.");
        return json.RootElement.Deserialize<StoredPressroomConfigurations>()?.Configurations ?? throw new JsonException("Missing Pressroom configurations.");
    }
}
