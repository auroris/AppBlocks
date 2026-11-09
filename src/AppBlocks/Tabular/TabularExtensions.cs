using Microsoft.Extensions.DependencyInjection.Extensions;
using AppBlocks.Blocks;

namespace AppBlocks.Tabular;

public static class TabularExtensions
{
    /// <summary>The Tabular form block's services: its limits, the shared owner resolution and the block's access rules. The endpoints are mapped by <see cref="TabularEndpoints.MapTabular"/>.</summary>
    public static IUmbracoBuilder AddTabular(this IUmbracoBuilder builder)
    {
        builder.Services.AddOptions<TabularOptions>().Bind(builder.Config.GetSection(TabularOptions.Section));
        // Shared with Attachments, so either feature works without the other.
        builder.Services.TryAddScoped<BlockOwnerAccess>();
        builder.Services.TryAddScoped<TabularAccess>();
        return builder;
    }
}
