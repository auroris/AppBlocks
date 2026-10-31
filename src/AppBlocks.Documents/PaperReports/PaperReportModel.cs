using System.Globalization;
using Umbraco.Cms.Core.Models.PublishedContent;
using AppBlocks.DataSources;

namespace AppBlocks.Documents.PaperReports;

/// <summary>Report-specific Razor templates receive data, never a database connection or SQL.</summary>
public sealed record PaperReportModel(IPublishedContent Page, IReadOnlyList<DataSourceQueryColumn> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, IReadOnlyDictionary<string, string> Parameters,
    DataSourceUser Person, CultureInfo Culture, DateTimeOffset GeneratedAt, string Theme);
