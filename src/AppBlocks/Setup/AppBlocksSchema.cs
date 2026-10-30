using System.Reflection;

namespace AppBlocks.Setup;

/// <summary>Embedded schema contributed by AppBlocks or an installed companion.</summary>
public sealed record AppBlocksSchema(Assembly Assembly);

public interface IAppBlocksSchemaMigration
{
    Task EnsureAsync(CancellationToken cancellationToken);
}
