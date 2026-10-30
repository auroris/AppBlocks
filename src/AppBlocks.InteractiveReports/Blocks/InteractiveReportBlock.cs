using System.Text.Json;
using InteractiveReport.AspNetCore;
using InteractiveReport.Core.Model;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Services;
using UmbracoAppHost.Blocks;
using UmbracoAppHost.DataSources;
using UmbracoAppHost.PaperReports;

namespace AppBlocks.InteractiveReports;

public sealed class InteractiveReportBlock(IOptionsMonitor<InteractiveReportOptions> reports, ReportDefinitionCompiler compiler,
    PublishedReportCatalog definitions, IContentService contents, Connections.ConnectionCatalog managed) : IAppBlockPlugin
{
    public string Alias => "appReport";
    public string ViewPath => "~/Views/Partials/ApplicationBlocks/InteractiveReport.cshtml";
    public bool RequiresUser => true;
    public void Validate(JsonElement block, List<string> errors)
    {
        var sourceName = Text(block, "reportName");
        ReportDefinition source;
        if (Text(block, "dataSource") is { Length: > 0 }) {
            var authored = definitions?.ReadBlockReport(block.GetProperty("key").GetGuid(), property => Text(block, property), errors);
            if (authored is null) { if (errors.Count == 0) errors.Add("Select a published data source."); return; }
            source = authored.Definition; sourceName = authored.Alias;
        } else if (!(definitions?.Reports ?? reports.CurrentValue.Reports).TryGetValue(sourceName, out source!)) { errors.Add("Select an available report source."); return; }
        var definition = JsonSerializer.Deserialize<ReportDefinition>(JsonSerializer.Serialize(source, IrJson.Options), IrJson.Options)!;
        if (Text(block, "pageSize") is { Length: > 0 } pageSize && pageSize != "0") {
            if (!int.TryParse(pageSize, out var size) || size < 1 || size > source.MaxPageSize) errors.Add($"Report page size must be between 1 and {source.MaxPageSize}.");
            else definition.DefaultPageSize = size;
        }
        // Control names are Interactive Reports' feature tokens, as for a report definition node.
        if (Text(block, "controls").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Any(c => ReportFeatures.Canonical(c) is null)) errors.Add("The report contains an unknown control name.");
        if (Text(block, "formPage").Length > 0 && Text(block, "keyColumn").Length == 0) errors.Add("Select the record key column for the form link.");
        if (Text(block, "paperReport") is { Length: > 0 } paper) {
            PaperReport(paper, errors);
            // Row print actions read the record key from this column, so a column the report lacks prints nothing.
            if (Text(block, "keyColumn") is { Length: > 0 } printKey && definitions?.ReportColumns(sourceName) is { } columns
                && !columns.Contains(printKey, StringComparer.OrdinalIgnoreCase)) errors.Add($"The record key column {printKey} is not a column of the report {sourceName}.");
        }
        if (Text(block, "initialState") is { Length: > 0 } state) {
            definition.DefaultState = JsonSerializer.Deserialize<ReportState>(state, IrJson.Options) ?? throw new JsonException("Starting view must be a report document.");
            if (definition.DefaultState.Tables?.Values.Any(t => t.Schema is { Count: > 0 }) == true) errors.Add("Capture the starting view without its transient schema cache.");
        }
        AppBlocks.Connections.ManagedReportCompiler.CompileManaged(compiler, sourceName, definition, managed);
    }


    /// <summary>Print opens /api/paper-reports/{key}, which only a Paper report node answers. A node not yet imported is not judged.</summary>
    private void PaperReport(string picked, List<string> errors)
    {
        if (contents is null || PaperReportLinks.PickedKey(picked) is not { } key || contents.GetById(key) is not { } node) return;
        if (node.ContentType.Alias != "paperReport") errors.Add($"Select a Paper report node for Paper report; {node.Name} is not one.");
    }


    private static string Text(JsonElement block, string alias) {
        var properties = block.GetProperty("values").EnumerateArray().Where(p => p.GetProperty("alias").GetString() == alias).ToArray();
        if (properties.Length == 0) return "";
        var value = properties[0].GetProperty("value");
        return value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? "" : value.ToString();
    }

}
