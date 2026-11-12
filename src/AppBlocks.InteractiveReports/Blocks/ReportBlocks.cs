using AppBlocks.Blocks;
using Umbraco.Cms.Core.Models.Blocks;
using Umbraco.Cms.Core.Models.PublishedContent;

namespace AppBlocks.InteractiveReports.Blocks;

/// <summary>The report companion's naming and lookup of page-owned report blocks.</summary>
public static class ReportBlocks
{
    public const string Alias = "appReport";
    public static string Name(Guid page, Guid block) => AppBlockContent.BlockName(page, block);
    public static bool ParseName(string name, out Guid page, out Guid block) => AppBlockContent.ParseBlockName(name, out page, out block);
    public static Task<(IPublishedContent Page, BlockGridItem Block, string? Culture)?> ResolveAsync(AppBlockContent content, string name)
        => content.BlockAsync(name, Alias);
}
