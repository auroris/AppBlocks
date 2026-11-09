using System.Text.Json;

namespace AppBlocks.Documents.Tests;

public sealed class ConfigurationCollectionTests
{
    [Fact]
    public async Task Configurations_have_stable_ids_independent_outputs_and_encrypted_keys()
    {
        using var site = new DocumentSettingsTests.SettingsSite();
        await site.Store.DeleteAsync(DocumentSettingsStore.ConfiguredId);
        var pdf = await site.Store.SaveConfigurationAsync(new(null, "Browser PDF", "https://pdf.example/"));
        var printer = await site.Store.SaveConfigurationAsync(new(null, "Reception", "https://print.example/", "printer:reception", "print-secret"));
        var loaded = site.CreateStore();
        Assert.Equal(2, loaded.List().Configurations.Count);
        Assert.False(loaded.Resolve(pdf.Id).ApiKey is { Length: > 0 });
        Assert.Equal("reception", loaded.Resolve(printer.Id).Printer);
        Assert.Equal("print-secret", loaded.Resolve(printer.Id).ApiKey);
        var changed = await loaded.SaveConfigurationAsync(new(printer.Id, "Front desk", printer.Url, printer.Output));
        Assert.Equal(printer.Id, changed.Id);
        Assert.Equal("print-secret", site.Store.Resolve(printer.Id).ApiKey);
        Assert.DoesNotContain("print-secret", JsonSerializer.Serialize(loaded.List()));
        Assert.DoesNotContain("print-secret", await File.ReadAllTextAsync(Path.Combine(site.Root, "App_Data/AppBlocks/documents.json")));
        await loaded.DeleteAsync(pdf.Id);
        Assert.Single(site.Store.List().Configurations);
        await loaded.DeleteAsync(printer.Id);
        Assert.Empty(site.CreateStore().List().Configurations);
        Assert.True(site.Store.List().Saved); // Appsettings defaults must not reappear.
        Assert.Throws<KeyNotFoundException>(() => site.Store.Resolve(printer.Id));
    }

    [Fact]
    public async Task Concurrent_additions_are_not_lost_and_names_are_unique()
    {
        using var site = new DocumentSettingsTests.SettingsSite();
        await Task.WhenAll(Enumerable.Range(1, 6).Select(i => site.Store.SaveConfigurationAsync(new(null, "PDF " + i, "https://pdf.example/"))));
        Assert.Equal(7, site.Store.List().Configurations.Count);
        await Assert.ThrowsAsync<ArgumentException>(() => site.Store.SaveConfigurationAsync(new(null, "pdf 1", "https://pdf.example/")));
        Assert.Equal(7, site.Store.List().Configurations.Count);
    }

    [Fact]
    public async Task Saved_secrets_are_not_forwarded_to_a_changed_server_and_printers_require_a_key()
    {
        using var site = new DocumentSettingsTests.SettingsSite();
        var id = DocumentSettingsStore.ConfiguredId;
        await Assert.ThrowsAsync<ArgumentException>(() => site.Store.SaveConfigurationAsync(new(id, "Moved", "https://another.example/")));
        await Assert.ThrowsAsync<ArgumentException>(() => site.Store.SaveConfigurationAsync(new(null, "Printer", "https://print.example/", "printer:office")));
        await Assert.ThrowsAsync<ArgumentException>(() => site.Store.SaveConfigurationAsync(new(id, "Printer", "https://configured.example/", "printer:office", ClearApiKey: true)));
        await site.Store.SaveConfigurationAsync(new(id, "Moved PDF", "https://another.example/", ClearApiKey: true));
        Assert.Null(site.Store.Resolve(id).ApiKey);
        Assert.Equal("https://another.example/", site.Store.Resolve(id).Url);
    }

    [Theory]
    [InlineData("printer:")]
    [InlineData("printer:../../other")]
    [InlineData("html")]
    [InlineData(null)]
    public async Task Invalid_outputs_cannot_be_saved(string? output)
    {
        using var site = new DocumentSettingsTests.SettingsSite();
        await Assert.ThrowsAsync<ArgumentException>(() => site.Store.SaveConfigurationAsync(new(null, "Invalid", "https://example/", output!, "secret")));
        Assert.Single(site.Store.List().Configurations);
    }
}
