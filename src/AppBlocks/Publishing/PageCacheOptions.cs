namespace AppBlocks.Publishing;

/// <summary>Which website pages AppBlocks sends <c>no-store</c> and keeps out of the output cache.</summary>
public enum NoStorePages
{
    /// <summary>Pages with a block whose output depends on who asks, including their Public Access login and denied responses.</summary>
    AppBlocksPages,
    /// <summary>Every page Umbraco routes.</summary>
    AllPages,
    /// <summary>None: Umbraco and the host decide.</summary>
    Off,
}

/// <summary>The page cache setting of the <c>AppBlocks</c> configuration section. API responses have their own cache headers.</summary>
public sealed class PageCacheOptions
{
    public const string Section = "AppBlocks";

    public NoStorePages NoStorePages { get; set; } = NoStorePages.AppBlocksPages;
}
