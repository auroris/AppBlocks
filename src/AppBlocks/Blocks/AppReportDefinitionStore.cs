using System.Text.Json;
using AppBlocks.Connections;
using InteractiveReport.AspNetCore;
using InteractiveReport.Core.Definitions;
using InteractiveReport.Core.Model;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Extensions;
using UmbracoAppHost.DataSources;
using UmbracoAppHost.Reports;

namespace UmbracoAppHost.Blocks;

/// <summary>Retains normal configured reports and resolves published block instances on demand.</summary>
public sealed class ConfiguredReportStore(IReportDefinitionStore value) { public IReportDefinitionStore Value { get; } = value; }

public sealed class AppReportDefinitionStore(
    ConfiguredReportStore configured, AppBlockContent content,
    ReportDefinitionCompiler compiler,
    PublishedDataCatalog catalog, DataSourceReportBindings bindings, ConnectionCatalog? managed = null)
    : IReportDefinitionStore, IReportDefinitionAuthorizationStore
{
    public async ValueTask<ReportDefinitionAuthorization?> FindAuthorization(string name, CancellationToken ct = default)
    {
        if (name.StartsWith(PublishedDataCatalog.SourceReportPrefix, StringComparison.Ordinal))
            return catalog.SourceForReport(name) is null ? null : new(name, new ReportAuthorization { Policy = ReportAdministratorRequirement.Policy });
        if (!AppBlockContent.ParseReportName(name, out _, out _))
        {
            if (catalog.IsUnavailableReport(name)) return null;
            if (catalog.ReportNodes.TryGetValue(name, out var node)) return new(node.Alias, node.Definition.Authorization);
            return configured.Value is IReportDefinitionAuthorizationStore authorization
                ? await authorization.FindAuthorization(name, ct)
                : await configured.Value.Find(name, ct) is { } normal ? new(normal.Name, normal.Authorization) : null;
        }
        if (content.Report(name) is not { } instance) return null;
        if (instance.Block.Content.Value<string>("dataSource") is { Length: > 0 } selection)
            return catalog.ResolveSource(selection) is null ? null : new(name, new ReportAuthorization { Policy = AppPagePolicyProvider.Name(instance.Page.Key, null) });
        if (!catalog.Reports.TryGetValue(instance.Block.Content.Value<string>("reportName") ?? "", out var source)) return null;
        return new(name, new ReportAuthorization { Policy = AppPagePolicyProvider.Name(instance.Page.Key, source.Authorization?.Policy) });
    }

    public async ValueTask<ReportDefinition?> Find(string name, CancellationToken ct = default)
    {
        if (name.StartsWith(PublishedDataCatalog.SourceReportPrefix, StringComparison.Ordinal)) {
            if (catalog.SourceForReport(name) is not { } previewSource) return null;
            var preview = ReadBlock(previewSource.Key, property => property == "dataSource" ? previewSource.Key.ToString() : null);
            if (preview is null) return null;
            await bindings.BindAsync(preview.Definition, previewSource, ct);
            preview.Definition.SourceName = name;
            preview.Definition.Authorization = new ReportAuthorization { Policy = ReportAdministratorRequirement.Policy };
            return compiler.CompileManaged(name, preview.Definition, managed);
        }
        if (!AppBlockContent.ParseReportName(name, out _, out _))
        {
            if (catalog.IsUnavailableReport(name)) return null;
            if (!catalog.ReportNodes.TryGetValue(name, out var node)) return await configured.Value.Find(name, ct);
            var definition = Copy(node.Definition);
            if (node.DataSource is { } nodeSource) await bindings.BindAsync(definition, nodeSource, ct);
            return compiler.CompileManaged(node.Alias, definition, managed);
        }
        if (content.Report(name) is not { } instance) return null;
        var block = instance.Block.Content;
        string sourceName; ReportDefinition source; DataSourceDefinition? dataSource;
        if (block.Value<string>("dataSource") is { Length: > 0 }) {
            var authored = ReadBlock(block.Key, property => block.GetProperty(property)?.GetValue(instance.Culture));
            if (authored?.DataSource is null) return null;
            source = authored.Definition; dataSource = authored.DataSource;
            sourceName = PublishedDataCatalog.SourceReportName(dataSource);
        } else {
            sourceName = block.Value<string>("reportName") ?? "";
            if (!catalog.Reports.TryGetValue(sourceName, out source!)) return null;
            dataSource = catalog.ReportNodes.GetValueOrDefault(sourceName)?.DataSource;
        }
        var result = Copy(source);
        if (dataSource is not null) await bindings.BindAsync(result, dataSource, ct);
        result.SourceName = sourceName;
        result.Authorization = new ReportAuthorization { Policy = AppPagePolicyProvider.Name(instance.Page.Key, source.Authorization?.Policy) };
        result.DocumentFiles = null; // Configured files belong to their original saved-view family.
        if (block.Value<string>("heading") is { Length: > 0 } title) result.Title = title;
        if (block.Value<string>("initialState") is { Length: > 0 } state) result.DefaultState = JsonSerializer.Deserialize<ReportState>(state, IrJson.Options);
        if (block.Value<int?>("pageSize") is > 0 and var size) {
            result.DefaultPageSize = size;
            if (result.DefaultState is not null) result.DefaultState.Page = new PageRequest { Index = 1, Size = size };
        }
        if (block.Value<IPublishedContent>("formPage") is { } formPage) {
            var url = formPage.Url(culture: instance.Culture);
            var column = block.Value<string>("keyColumn");
            var parameter = block.Value<string>("keyParameter") is { Length: > 0 } key ? key : "id";
            if (string.IsNullOrWhiteSpace(column)) throw new InvalidOperationException("A report with a form page requires its record key column.");
            result.EditLink = new ReportEditLink { UrlTemplate = url + "?" + Uri.EscapeDataString(parameter) + "={" + column + "}", Label = block.Value<string>("editLabel") };
            result.CreateLink = new ReportCreateLink { Url = url, Label = block.Value<string>("createLabel") };
        }
        if (block.Value<IPublishedContent>("paperReport") is not null && block.Value<string>("keyColumn") is { Length: > 0 } printKey)
            PaperReports.PaperReportLinks.AddPrintAction(result, printKey);
        return compiler.CompileManaged(name, result, managed);
    }
    private PublishedReport? ReadBlock(Guid key, Func<string, object?> value) => catalog.ReadBlockReport(key, value, new List<string>());
    private static ReportDefinition Copy(ReportDefinition source) => JsonSerializer.Deserialize<ReportDefinition>(JsonSerializer.Serialize(source, IrJson.Options), IrJson.Options)!;
}
