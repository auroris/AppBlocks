namespace AppBlocks.Publishing;

/// <summary>Which website pages AppBlocks keeps out of caches. API responses have their own cache headers.</summary>
public sealed class PageCacheOptions
{
    public const string Section = "AppBlocks";
    public const string AllPages = "All pages";
    public const string AppBlocksPages = "App Blocks Pages";
    public const string Off = "Off";

    public string NoStorePages { get; set; } = AppBlocksPages;

    internal bool Is(string mode) => string.Equals(NoStorePages, mode, StringComparison.OrdinalIgnoreCase);
    internal bool IsValid => Is(AllPages) || Is(AppBlocksPages) || Is(Off);
}
