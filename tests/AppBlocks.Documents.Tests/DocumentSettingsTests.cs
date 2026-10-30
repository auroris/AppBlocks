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
    public async Task Saved_settings_survive_a_new_store_and_blank_url_disables_configured_pressroom()
    {
        using var site = new SettingsSite();
        Assert.Equal("https://configured.example/", site.Store.Get().PressroomUrl);
        await site.Store.SaveAsync(new("https://saved.example/pdf", "test-secret", TimeoutSeconds: 12));
        var reloaded = site.CreateStore();
        Assert.Equal("https://saved.example/pdf", reloaded.Get().PressroomUrl);
        Assert.Equal("test-secret", reloaded.Resolve().ApiKey);
        Assert.Equal(12, reloaded.Resolve().TimeoutSeconds);
        var json = await File.ReadAllTextAsync(Path.Combine(site.Root, "App_Data", "AppBlocks", "documents.json"));
        Assert.DoesNotContain("test-secret", json);
        Assert.DoesNotContain("test-secret", JsonSerializer.Serialize(reloaded.Get()));
        await reloaded.SaveAsync(new(null));
        Assert.Null(site.Store.Resolve().Url);
        Assert.Equal("test-secret", site.Store.Resolve().ApiKey);
        await reloaded.SaveAsync(new(null, ClearApiKey: true));
        Assert.False(site.Store.Get().HasApiKey);
        Assert.Null(site.Store.Resolve().ApiKey);
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
        await Assert.ThrowsAsync<ArgumentException>(() => site.Store.SaveAsync(new(address)));
        Assert.Equal("https://configured.example/", site.Store.Get().PressroomUrl);
        Assert.False(site.Store.Get().Saved);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task Invalid_timeouts_are_rejected(double timeout)
    {
        using var site = new SettingsSite();
        await Assert.ThrowsAsync<ArgumentException>(() => site.Store.SaveAsync(new(null, TimeoutSeconds: timeout)));
        Assert.False(site.Store.Get().Saved);
    }

    [Fact]
    public async Task Non_administrators_cannot_read_or_save_settings()
    {
        using var site = new SettingsSite();
        var accessor = System.Reflection.DispatchProxy.Create<IBackOfficeSecurityAccessor, EmptySecurity>();
        var controller = new DocumentsSettingsController(site.Store, accessor);
        Assert.IsType<ForbidResult>(controller.Get());
        Assert.IsType<ForbidResult>(await controller.Save(new("https://other.example/"), default));
        Assert.False(site.Store.Get().Saved);
    }

    public class EmptySecurity : System.Reflection.DispatchProxy
    {
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args) => null;
    }

    internal sealed class SettingsSite : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("documents-settings-").FullName;
        private readonly IDataProtectionProvider protection = new EphemeralDataProtectionProvider();
        internal IConfiguration Configuration { get; } = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Pressroom:Url"] = "https://configured.example/", ["Pressroom:ApiKey"] = "configured-secret",
        }).Build();
        public DocumentSettingsStore Store { get; }
        public SettingsSite() => Store = CreateStore();
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
