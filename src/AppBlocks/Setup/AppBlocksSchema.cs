using System.Reflection;
using System.Xml.Linq;
using Microsoft.Extensions.FileProviders;

namespace AppBlocks.Setup;

/// <summary>Embedded schema contributed by AppBlocks or an installed companion.</summary>
public sealed record AppBlocksSchema(Assembly Assembly)
{
    /// <summary>The definitions of one kind, <c>ContentTypes</c> or <c>DataTypes</c>, in file name order.</summary>
    public IEnumerable<XElement> Definitions(string kind)
    {
        foreach (var file in new ManifestEmbeddedFileProvider(Assembly).GetDirectoryContents("Schema/" + kind)
            .Where(f => !f.IsDirectory).OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            using var stream = file.CreateReadStream();
            yield return XElement.Load(stream);
        }
    }
}

public interface IAppBlocksSchemaMigration
{
    Task EnsureAsync(CancellationToken cancellationToken);
}
