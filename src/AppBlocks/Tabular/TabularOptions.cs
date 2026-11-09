namespace AppBlocks.Tabular;

/// <summary>The <c>AppBlocks:Tabular</c> configuration section: the limits every Tabular form block stays within.</summary>
public sealed class TabularOptions
{
    public const string Section = "AppBlocks:Tabular";

    /// <summary>The most rows any block lists for one record; a block's own row limit is cut to it. 1000 unless configured.</summary>
    public int MaxRows { get; set; } = TabularBlock.MaxRowsLimit;
}
