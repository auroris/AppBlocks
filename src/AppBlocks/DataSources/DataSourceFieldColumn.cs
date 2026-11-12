namespace AppBlocks.DataSources;

/// <summary>A field's mapping to its database column, for authoring tools consuming data-source metadata.</summary>
public sealed record DataSourceFieldColumn(string Field, string Column, string Kind);
