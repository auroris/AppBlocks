using System.Text.Json;

namespace UmbracoAppHost.Blocks;

/// <summary>An optional block supplied by a companion package.</summary>
public interface IAppBlockPlugin
{
    string Alias { get; }
    string ViewPath { get; }
    bool RequiresUser { get; }
    void Validate(JsonElement block, List<string> errors);
}
