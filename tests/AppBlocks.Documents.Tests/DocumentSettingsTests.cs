using System.Text.Json;
using AppBlocks.Documents.Management;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Umbraco.Cms.Core.Security;

namespace AppBlocks.Documents.Tests;

public sealed class DocumentSettingsTests
{
    [Fact]
    public async Task The_configured_pressroom_is_a_pdf_configuration_until_settings_are_saved()
    {
        using var site = new SettingsSite();
        var configured = Assert.Single(site.Store.List().Configurations);
        Assert.Equal((DocumentSettingsStore.ConfiguredId, "https://configured.example/", "pdf", true), (configured.Id, configured.Url, configured.Output, configured.HasApiKey));
        Assert.Equal("configured-secret", site.Store.Resolve(configured.Id).ApiKey);
        Assert.False(site.Store.List().Saved);
        await site.Store.SaveConfigurationAsync(new(configured.Id, "Saved PDF", "https://saved.example/pdf", ApiKey: "test-secret", TimeoutSeconds: 12));
        var reloaded = site.CreateStore();
        var saved = Assert.Single(reloaded.List().Configurations);
        Assert.Equal((configured.Id, "https://saved.example/pdf", 12d), (saved.Id, saved.Url, saved.TimeoutSeconds));
        Assert.Equal("test-secret", reloaded.Resolve(saved.Id).ApiKey);
        var json = await File.ReadAllTextAsync(Path.Combine(site.Root, "App_Data", "AppBlocks", "documents.json"));
        Assert.DoesNotContain("test-secret", json);
        Assert.DoesNotContain("test-secret", JsonSerializer.Serialize(reloaded.List()));
        // The unprefixed Pressroom:* keys of earlier versions supply nothing.
        using var unprefixed = new SettingsSite(new() { ["Pressroom:Url"] = "https://configured.example/" });
        Assert.Empty(unprefixed.Store.List().Configurations);
    }

    [Fact]
    public async Task A_settings_file_of_an_earlier_version_is_refused()
    {
        using var site = new SettingsSite();
        var path = Path.Combine(site.Root, "App_Data", "AppBlocks", "documents.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, """{ "PressroomUrl": "https://legacy.example/", "TimeoutSeconds": 25 }""");
        Assert.Throws<JsonException>(() => site.Store.List());
    }

    [Theory]
    [InlineData("relative/address")]
    [InlineData("ftp://pressroom.example/")]
    [InlineData("https://user:password@pressroom.example/")]
    [InlineData("https://pressroom.example/?key=secret")]
    [InlineData("https://pressroom.example/#fragment")]
    public async Task Invalid_addresses_do_not_replace_working_settings(string address)
    {
        using var site = new SettingsSite();
        await Assert.ThrowsAsync<ArgumentException>(() => site.Store.SaveConfigurationAsync(new(DocumentSettingsStore.ConfiguredId, "PDF", address)));
        Assert.Equal("https://configured.example/", Assert.Single(site.Store.List().Configurations).Url);
        Assert.False(site.Store.List().Saved);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task Invalid_timeouts_are_rejected(double timeout)
    {
        using var site = new SettingsSite();
        await Assert.ThrowsAsync<ArgumentException>(() => site.Store.SaveConfigurationAsync(new(null, "PDF", "https://pdf.example/", TimeoutSeconds: timeout)));
        Assert.False(site.Store.List().Saved);
    }

    [Fact]
    public void Only_administrators_can_read_or_save_settings()
    {
        // The administrator policy guards every action of the controller, before any of them runs.
        var policies = typeof(DocumentsSettingsController).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Select(attribute => attribute.Policy);
        Assert.Contains(AppBlocks.Authentication.AppBlocksAuthorization.AdministratorPolicy, policies);
    }

    internal sealed class SettingsSite : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("documents-settings-").FullName;
        private readonly IDataProtectionProvider protection = new EphemeralDataProtectionProvider();
        internal IConfiguration Configuration { get; }
        public DocumentSettingsStore Store { get; }
        public SettingsSite(Dictionary<string, string?>? configuration = null)
        {
            Configuration = new ConfigurationBuilder().AddInMemoryCollection(configuration ?? new Dictionary<string, string?> {
                ["AppBlocks:Documents:Pressroom:Url"] = "https://configured.example/", ["AppBlocks:Documents:Pressroom:ApiKey"] = "configured-secret",
            }).Build();
            Store = CreateStore();
        }
        public DocumentSettingsStore CreateStore() => new(Configuration, new TestEnvironment { ContentRootPath = Root }, protection);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    internal sealed class TestEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Documents.Tests";
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
