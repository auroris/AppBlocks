using System.Text.Json;

namespace AppBlocks.Blocks;

/// <summary>An optional block supplied by a companion package.</summary>
public interface IAppBlockPlugin
{
    string Alias { get; }
    string ViewPath { get; }

    /// <summary>
    /// Whether the block acts for the signed-in person, as a report does. Publishing refuses such a block inside a form, and a
    /// page that has one is rendered for each request and kept out of caches.
    /// </summary>
    bool RequiresUser { get; }

    /// <summary>
    /// Whether the block's output can change without a publish, such as a public list read from a database. A page that has one
    /// is rendered for each request and kept out of caches, so visitors see current data. Return true from a public block for
    /// that, and leave <see cref="RequiresUser"/> false. A block that requires the user is kept out of caches whatever this
    /// answers. The default answers <see cref="RequiresUser"/>, so a plugin written before this member keeps its behaviour.
    /// </summary>
    bool VariesByData => RequiresUser;

    void Validate(JsonElement block, List<string> errors);
}
