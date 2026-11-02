using System.Collections.Concurrent;
using AppBlocks.Setup;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Services;

namespace AppBlocks.Documents.Tests;

[Collection("Standalone Umbraco")]
public sealed class WizardSiteTests
{
    [Fact]
    public async Task First_boot_without_a_database_serves_the_installer_without_attempting_schema_installation()
    {
        var root = Directory.CreateTempSubdirectory("appblocks-wizard-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
            ApplicationName = typeof(WizardSiteTests).Assembly.GetName().Name, ContentRootPath = root, EnvironmentName = "Development",
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["ConnectionStrings:umbracoDbDSN"] = "",
            ["Umbraco:CMS:Global:Id"] = Guid.NewGuid().ToString(),
            ["Umbraco:CMS:Global:UseHttps"] = "false",
            ["Umbraco:CMS:ModelsBuilder:ModelsMode"] = "Nothing",
            ["Umbraco:CMS:Unattended:InstallUnattended"] = "false",
            ["Umbraco:CMS:TypeFinder:AdditionalAssemblyExclusionEntries:0"] = "xunit.",
        });
        var errors = new StartupErrors();
        builder.Logging.ClearProviders().AddProvider(errors);
        builder.CreateUmbracoBuilder().AddBackOffice().AddWebsite().AddComposers().Build();
        await using var app = builder.Build();
        try {
            await app.BootUmbracoAsync();
            app.UseUmbraco().WithMiddleware(u => { u.UseBackOffice(); u.UseWebsite(); })
                .WithEndpoints(u => { u.UseBackOfficeEndpoints(); u.UseWebsiteEndpoints(); });
            await app.StartAsync();
            Assert.Equal(RuntimeLevel.Install, app.Services.GetRequiredService<IRuntimeState>().Level);
            Assert.Empty(errors.Messages);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            using var response = await client.GetAsync("/umbraco/management/api/v1/install/settings");
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            Assert.Empty(errors.Messages);
            Assert.Empty(Directory.GetFiles(root, "*.db", SearchOption.AllDirectories));
        } finally { await app.StopAsync(); }
    }

    private sealed class StartupErrors : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Collector(Messages);
        public void Dispose() { }
        private sealed class Collector(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => level >= LogLevel.Error;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            { if (IsEnabled(level)) messages.Enqueue(formatter(state, exception) + exception); }
        }
    }
}
