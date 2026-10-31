using System.Diagnostics;
using AppBlocks.Blocks;
using AppBlocks.DataSources;
using AppBlocks.Setup;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace AppBlocks.Tests;

/// <summary>An installation that fails part way, on a real site: nothing of it stays, and trying again works, in the same process and in a new one.</summary>
[Collection("Standalone Umbraco")]
public sealed class SchemaInstallerRetryTests
{
    private const string StageVariable = "APPBLOCKS_RETRY_STAGE";
    private const string RootVariable = "APPBLOCKS_RETRY_ROOT";

    /// <summary>What a test turns off to let the installation through; while it is on, the first content type past the data types fails its save.</summary>
    private sealed class Failure { public bool On { get; set; } = true; }

    private sealed class FailingSave(Failure failure) : INotificationHandler<ContentTypeSavingNotification>
    {
        // After the data types and some content types are written, so what the transaction wrote is what a retry must not be misled by.
        public void Handle(ContentTypeSavingNotification notification)
        {
            if (failure.On && notification.SavedEntities.Any(type => type.Alias == DataSourceSchema.SqlAlias)) throw new InvalidOperationException("Simulated schema failure.");
        }
    }

    private static void Fail(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<Failure>();
        builder.Services.AddSingleton<INotificationHandler<ContentTypeSavingNotification>, FailingSave>();
    }

    [Fact]
    public async Task A_retry_in_the_same_process_meets_the_real_error_until_it_is_fixed_and_then_installs_everything()
    {
        await using var site = await BlockSite.BootAsync(configure: Fail);
        using var scope = site.CreateScope();
        var services = scope.ServiceProvider;
        await AssertNothingInstalledAsync(services);

        // As a site that calls the installer before an import and then has the started notification call it. The second meets the same failure, not one the first left behind.
        var installer = services.GetRequiredService<SchemaInstaller>();
        Assert.Equal("Simulated schema failure.", (await Assert.ThrowsAsync<InvalidOperationException>(() => installer.EnsureAsync(default))).Message);
        await AssertNothingInstalledAsync(services);

        // With the fault gone, the same process installs all of it, and the site works.
        services.GetRequiredService<Failure>().On = false;
        await installer.EnsureAsync(default);
        await AssertInstalledAsync(services);
        await site.PublishAsync();
        await AssertBlocksWorkAsync(site);
    }

    /// <summary>
    /// A real second process over the folder the first left. Each stage is <see cref="Retry_stage_in_its_own_process"/> run by this assembly's
    /// own test runner, so they share no memory at all, which is the difference from the test above.
    /// </summary>
    [Fact]
    public async Task A_retry_in_a_new_process_installs_everything_over_what_the_failed_one_left()
    {
        var root = Directory.CreateTempSubdirectory("appblocks-retry-").FullName;
        try
        {
            await RunStageAsync("fail", root);
            await RunStageAsync("retry", root);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    [Fact]
    public async Task Retry_stage_in_its_own_process()
    {
        var stage = Environment.GetEnvironmentVariable(StageVariable);
        Assert.SkipWhen(stage is null, "Run only as a stage of A_retry_in_a_new_process_installs_everything_over_what_the_failed_one_left.");
        var root = Environment.GetEnvironmentVariable(RootVariable)!;
        if (stage == "fail")
        {
            await using var failed = await BlockSite.BootAsync(root, Fail);
            using var scope = failed.CreateScope();
            await AssertNothingInstalledAsync(scope.ServiceProvider);
            return;
        }
        await using var site = await BlockSite.BootAsync(root);
        using (var scope = site.CreateScope())
        {
            // The started notification does it on this boot, like any other start.
            var types = scope.ServiceProvider.GetRequiredService<IContentTypeService>();
            for (var i = 0; i < 100 && types.Get(DataSourceSchema.SqlAlias) is null; i++) await Task.Delay(100);
            await AssertInstalledAsync(scope.ServiceProvider);
        }
        await site.PublishAsync();
        await AssertBlocksWorkAsync(site);
    }

    private static async Task RunStageAsync(string stage, string root)
    {
        var runner = Path.ChangeExtension(typeof(SchemaInstallerRetryTests).Assembly.Location, OperatingSystem.IsWindows() ? ".exe" : null);
        var start = new ProcessStartInfo(runner) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add("*" + nameof(Retry_stage_in_its_own_process));
        start.ArgumentList.Add("-noLogo");
        start.Environment[StageVariable] = stage;
        start.Environment[RootVariable] = root;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await process.WaitForExitAsync(timeout.Token);
        var text = await output + await errors;
        Assert.True(process.ExitCode == 0 && text.Contains("Total: 1, Errors: 0, Failed: 0, Skipped: 0"), $"The {stage} stage: {text}");
    }

    private static IEnumerable<AppBlocksSchema> Schemas(IServiceProvider services) => services.GetServices<AppBlocksSchema>().DistinctBy(schema => schema.Assembly);

    private static async Task AssertNothingInstalledAsync(IServiceProvider services)
    {
        var types = services.GetRequiredService<IContentTypeService>();
        var dataTypes = services.GetRequiredService<IDataTypeService>();
        foreach (var schema in Schemas(services))
        {
            foreach (var definition in schema.Definitions("ContentTypes")) Assert.Null(types.Get(Guid.Parse(definition.Attribute("Key")!.Value)));
            foreach (var definition in schema.Definitions("DataTypes")) Assert.Null(await dataTypes.GetAsync(Guid.Parse(definition.Attribute("Key")!.Value)));
        }
        // Not in what the caches say, and not in the database, whose rows the caches could hide.
        var entities = services.GetRequiredService<IEntityService>();
        foreach (var schema in Schemas(services))
            foreach (var definition in schema.Definitions("ContentTypes"))
                Assert.False(entities.Exists(Guid.Parse(definition.Attribute("Key")!.Value)), definition.Attribute("Alias")!.Value);
    }

    private static async Task AssertInstalledAsync(IServiceProvider services)
    {
        var types = services.GetRequiredService<IContentTypeService>();
        var dataTypes = services.GetRequiredService<IDataTypeService>();
        foreach (var schema in Schemas(services))
        {
            foreach (var definition in schema.Definitions("DataTypes")) Assert.NotNull(await dataTypes.GetAsync(Guid.Parse(definition.Attribute("Key")!.Value)));
            foreach (var definition in schema.Definitions("ContentTypes"))
            {
                var type = types.Get(Guid.Parse(definition.Attribute("Key")!.Value));
                Assert.NotNull(type);
                Assert.All(definition.Element("GenericProperties")!.Elements("GenericProperty"), property => Assert.True(type.PropertyTypeExists(property.Element("Alias")!.Value), property.Element("Alias")!.Value));
            }
        }
        Assert.Contains(types.Get(DataSourceSchema.RootAlias)!.AllowedContentTypes!, child => child.Alias == DataSourceSchema.SqlAlias);
    }

    /// <summary>What was installed takes real use: a member lists a form's files and lines through their endpoints.</summary>
    private static async Task AssertBlocksWorkAsync(BlockSite site)
    {
        var member = site.CreateMember("retry-" + Guid.NewGuid().ToString("N")[..8], Guid.NewGuid().ToString("N") + "@example.invalid", "Retry member");
        using var client = await site.SignInMemberAsync(member);
        var files = await site.Attachments(client, BlockSite.FormPageKey, site.AttachmentsBlock).ListAsync("id=4");
        Assert.True(files.IsSuccess, files.Problem);
        var lines = await site.Tabular(client, BlockSite.FormPageKey, BlockPages.LinesBlock).ListAsync("id=4");
        Assert.True(lines.IsSuccess, lines.Problem);
        Assert.Equal(2, lines.Rows.Count);
    }
}
