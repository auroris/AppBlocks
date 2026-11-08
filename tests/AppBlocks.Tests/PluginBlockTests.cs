using System.Text.Json;
using AppBlocks.Blocks;

namespace AppBlocks.Tests;

/// <summary>What a companion's block plugin declares, and what AppBlocks does with each answer.</summary>
public sealed class PluginBlockTests
{
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
