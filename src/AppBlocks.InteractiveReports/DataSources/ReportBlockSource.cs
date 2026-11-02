using System.Text.Json;
using AppBlocks.DataSources;

namespace AppBlocks.InteractiveReports.DataSources;

/// <summary>A report's own row source. Only a connection name is stored in content.</summary>
internal static class ReportBlockSource
{
    public static DataSourceDefinition? Read(Guid key, string selection, Func<string, bool> isConnection, ICollection<string> errors)
    {
        var start = errors.Count;
        try
        {
            using var document = JsonDocument.Parse(selection);
            var root = document.RootElement;
            string Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()!.Trim() : "";
            var kind = Text("kind");
            var connection = Text("connection");
            var source = Text("source");
            if (root.EnumerateObject().Any(p => p.Name is not ("kind" or "connection" or "source"))
                || root.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() > 1))
                errors.Add("The report source contains unsupported or repeated settings. Select its table or SQL query again.");
            if (!isConnection(connection))
                errors.Add("Select a data source configured in Settings → AppBlocks → Connections.");
            if (source.Length is 0 or > DataSourceDefinition.MaxSourceLength)
                errors.Add($"Enter a table or SQL query of at most {DataSourceDefinition.MaxSourceLength} characters.");
            else if (kind == "table")
            {
                if (TableName.Parse(source) is null) errors.Add("Select a table or view from the data source.");
            }
            else if (kind == "sql")
            {
                SqlTemplate.Parse(source, SqlUse.RowSource, "The report SQL query", errors);
            }
            else errors.Add("Choose Table or SQL query for the report source.");
            return errors.Count == start ? new DataSourceDefinition {
                Key = key, Name = "Report " + key.ToString("N"), Alias = "appblocks-block-" + key.ToString("N"),
                // Block-owned sources follow the page's access, like the default for named SQL data sources.
                Connection = connection, Source = source, Access = DataSourceAccess.Public,
            } : null;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            errors.Add("The report source is invalid. Select its table or SQL query again.");
            return null;
        }
    }
}
