namespace AppBlocks.DataSources;

/// <summary>
/// A data source field as code and authoring tools see it: its alias, the column behind it and its kind. A backing class written
/// from a table alone has no aliases, and its fields leave <paramref name="Field"/> empty.
/// </summary>
public sealed record DataSourceFieldColumn(string Field, string Column, string Kind);
