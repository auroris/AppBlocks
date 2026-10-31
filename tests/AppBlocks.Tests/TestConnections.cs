using AppBlocks.Connections;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;

namespace AppBlocks.Tests;

/// <summary>Registers connections with AppBlocks as a site does, for tests of the engine without a site.</summary>
internal static class TestConnections
{
    public static IServiceCollection AddTestConnections(this IServiceCollection services, Action<AppBlocksBuilder> configure)
    {
        var builder = new AppBlocksBuilder();
        configure(builder);
        var environment = new TestEnvironment();
        services.TryAddSingleton<IWebHostEnvironment>(environment);
        services.AddSingleton(sp => new ConnectionCatalog(sp.GetRequiredService<IConfiguration>(), environment, new EphemeralDataProtectionProvider(), builder, sp));
        services.AddSingleton<IApplicationConnectionFactory>(sp => new ApplicationConnections(sp, builder, sp.GetRequiredService<ConnectionCatalog>()));
        return services;
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "AppBlocks.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = Directory.CreateTempSubdirectory("appblocks-site-").FullName;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
