using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using AppBlocks.Blocks;
using AppBlocks.Setup;

namespace AppBlocks.Tests;

public sealed partial class BlockPreviewTests
{
    [GeneratedRegex("\"alias\":\\s*\"(?<alias>[^\"]+)\",\\s*\"key\":\\s*\"(?<key>[^\"]+)\"")]
    private static partial Regex BlockType();

    private static string Shipped(string file, [CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../src/AppBlocks/wwwroot/App_Plugins/ApplicationBlocks", file));

    [Fact]
    public void The_preview_block_table_lists_every_owned_element_type_under_its_schema_key()
    {
        // The previews resolve a nested grid's content type keys synchronously from a static table, so it must not drift from the schema.
        var schema = new AppBlocksSchema(typeof(AppBlocksExtensions).Assembly);
        var settings = schema.Definitions("DataTypes")
            .Select(type => type.Element("Config")?.Value)
            .Where(config => !string.IsNullOrWhiteSpace(config))
            .SelectMany(config => JsonDocument.Parse(config!).RootElement.TryGetProperty("blocks", out var blocks) ? blocks.EnumerateArray().ToArray() : [])
            .Where(block => block.TryGetProperty("settingsElementTypeKey", out var key) && key.ValueKind == JsonValueKind.String)
            .Select(block => block.GetProperty("settingsElementTypeKey").GetGuid())
            .ToHashSet();
        var owned = schema.Definitions("ContentTypes")
            .Where(type => type.Element("Info")?.Element("IsElement")?.Value == "true" && !settings.Contains(Guid.Parse(type.Attribute("Key")!.Value)))
            .Select(type => type.Attribute("Alias")!.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var shipped = BlockType().Matches(File.ReadAllText(Shipped("block-types.js")))
            .ToDictionary(match => match.Groups["alias"].Value, match => Guid.Parse(match.Groups["key"].Value));

        Assert.NotEmpty(owned);
        Assert.Equal(owned, shipped.Keys.Order(StringComparer.Ordinal).ToArray());
        foreach (var (alias, key) in shipped) Assert.True(AppBlockSchema.Key(alias) == key, $"{alias}: block-types.js has {key}, the schema key is {AppBlockSchema.Key(alias)}");
    }
}
