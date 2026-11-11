using System.Text.Json;
using AppBlocks.Blocks;
using AppBlocks.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace AppBlocks.Tests;

/// <summary>What a companion's block plugin declares, and what AppBlocks does with each answer.</summary>
public sealed class PluginBlockTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Forms_allow_companion_controls_but_refuse_blocks_that_require_the_user(bool requiresUser)
    {
        var services = new ServiceCollection();
        services.AddScoped<IFormResource, SampleOrdersResource>(); services.AddScoped<FormResourceRegistry>();
        services.AddSingleton<IAppBlockPlugin>(new Plugin(requiresUser)); services.AddScoped<AppBlocksValidator>();
        using var provider = services.BuildServiceProvider(); using var scope = provider.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<AppBlocksValidator>();
        await validator.LoadAsync();
        var page = BlockPages.ContentBlocksForm();
        var block = page["contentData"]!.AsArray().First(item => item!["contentTypeKey"]!.GetValue<string>() == AppBlockSchema.Key(AppBlockSchema.HtmlAlias).ToString())!;
        block["contentTypeKey"] = AppBlockSchema.Key("testPlugin").ToString();
        var errors = validator.Validate(page.ToJsonString());
        if (requiresUser) Assert.Contains(errors, error => error.Contains("cannot be nested"));
        else Assert.Empty(errors);
    }

    private sealed class Plugin(bool requiresUser, bool? variesByData = null) : IAppBlockPlugin
    {
        public string Alias => "testPlugin";
        public string ViewPath => "test.cshtml";
        public bool RequiresUser => requiresUser;
        public void Validate(JsonElement block, List<string> errors) { }
        // Only a plugin that answers keeps an answer of its own; the others take the interface's default.
        public bool VariesByData => variesByData ?? requiresUser;
    }

    private sealed class OlderPlugin(bool requiresUser) : IAppBlockPlugin
    {
        public string Alias => "olderPlugin";
        public string ViewPath => "older.cshtml";
        public bool RequiresUser => requiresUser;
        public void Validate(JsonElement block, List<string> errors) { }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void A_plugin_written_before_VariesByData_keeps_its_cache_behaviour(bool requiresUser, bool varies)
    {
        IAppBlockPlugin plugin = new OlderPlugin(requiresUser);
        Assert.Equal(requiresUser, plugin.VariesByData);
        Assert.Equal(varies, AppBlockContent.Varies(plugin));
    }

    [Fact]
    public void A_public_block_that_reads_data_leaves_caches_without_requiring_the_user()
    {
        IAppBlockPlugin plugin = new Plugin(requiresUser: false, variesByData: true);
        Assert.False(plugin.RequiresUser);
        Assert.True(AppBlockContent.Varies(plugin));
    }

    [Fact]
    public void A_block_that_requires_the_user_stays_out_of_caches_whatever_it_says_about_data()
        => Assert.True(AppBlockContent.Varies(new Plugin(requiresUser: true, variesByData: false)));
}
