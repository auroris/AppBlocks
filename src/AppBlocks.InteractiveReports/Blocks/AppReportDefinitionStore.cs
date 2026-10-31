using System.Text.Json;
using AppBlocks.InteractiveReports.Connections;
using AppBlocks.Connections;
using AppBlocks.InteractiveReports.Reports;
using InteractiveReport.AspNetCore;
using InteractiveReport.Core.Definitions;
using InteractiveReport.Core.Model;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Extensions;
using AppBlocks.InteractiveReports.DataSources;
using AppBlocks.Blocks;
using AppBlocks.DataSources;

namespace AppBlocks.InteractiveReports.Blocks;

/// <summary>Retains normal configured reports and resolves published block instances on demand.</summary>
public sealed class ConfiguredReportStore(IReportDefinitionStore value) { public IReportDefinitionStore Value { get; } = value; }

public sealed class AppReportDefinitionStore(
    ConfiguredReportStore configured, AppBlockContent content,
    ReportDefinitionCompiler compiler,
    PublishedReportCatalog catalog, DataSourceReportBindings bindings, ConnectionCatalog? managed = null,
    AppBlocks.DocumentOutput.IAppDocumentLinks? documents = null,
    ReportSourcePreviews? previews = null, IHttpContextAccessor? http = null, SavedViewStore? savedViews = null, AppPageAccess? pages = null)
    : IReportDefinitionStore, IReportDefinitionAuthorizationStore
{
    private bool CallerIsAnonymous => http?.HttpContext is { } context && context.User.Identity?.IsAuthenticated != true;

    /// <summary>
    /// A block's report-level gate. It keeps its page's Public Access for anyone signed in. An anonymous visitor passes only on
    /// a page with none, and only when reading the source binds nothing about the caller; otherwise the engine asks them to sign in.
    /// </summary>
    private ReportAuthorization BlockAuthorization(IPublishedContent page, DataSourceDefinition source)
        => CallerIsAnonymous && pages?.IsOpen(page) == true && !catalog.RequiresSignedInReader(source)
            ? new ReportAuthorization { AllowAnonymous = true }
            : new ReportAuthorization { Policy = AppPagePolicyProvider.Name(page.Key, null) };

    public async ValueTask<ReportDefinitionAuthorization?> FindAuthorization(string name, CancellationToken ct = default)
    {
        if (name.StartsWith(ReportSourcePreviews.Prefix, StringComparison.Ordinal))
            return previews?.Find(name, http?.HttpContext?.User) is null ? null
                : new(name, new ReportAuthorization { Policy = ReportAdministratorRequirement.Policy });
        if (!AppBlockContent.ParseReportName(name, out _, out _))
        {
            if (catalog.IsUnavailableReport(name)) return null;
            if (catalog.ReportNodes.TryGetValue(name, out var node)) return new(node.Alias, node.Definition.Authorization);
            return configured.Value is IReportDefinitionAuthorizationStore authorization
                ? await authorization.FindAuthorization(name, ct)
                : await configured.Value.Find(name, ct) is { } normal ? new(normal.Name, normal.Authorization) : null;
        }
        if (content.Report(name) is not { } instance) return null;
        if (instance.Block.Content.Value<string>("dataSource", culture: instance.Culture) is { Length: > 0 } selection)
            return catalog.ResolveBlockSource(instance.Block.Content.Key, selection, new List<string>()) is { } source
                ? new(name, BlockAuthorization(instance.Page, source)) : null;
        return null;
    }

    /// <summary>
    /// A report's definition, without saving among its features while the saved-view table cannot be used, or for an anonymous
    /// visitor to a block, whose saved views would have no owner. The engine's own administration listing keeps its answer.
    /// </summary>
    public async ValueTask<ReportDefinition?> Find(string name, CancellationToken ct = default)
    {
        var definition = await FindDefinition(name, ct);
        if (definition is null) return null;
        if (CallerIsAnonymous && AppBlockContent.ParseReportName(name, out _, out _)
            || savedViews is not null && !name.StartsWith("__", StringComparison.Ordinal) && !await savedViews.ReadyAsync(ct))
            definition.Features = ReportFeatures.Resolve(definition).Where(feature => feature != ReportFeatures.SavedReports).ToList();
        return definition;
    }

    private async ValueTask<ReportDefinition?> FindDefinition(string name, CancellationToken ct)
    {
        if (name.StartsWith(ReportSourcePreviews.Prefix, StringComparison.Ordinal))
        {
            if (previews?.Find(name, http?.HttpContext?.User) is not { DataSource: { } previewSource } preview) return null;
            var definition = Copy(preview.Definition);
            await bindings.BindAsync(definition, previewSource, ct);
            definition.SourceName = name;
            definition.Authorization = new ReportAuthorization { Policy = ReportAdministratorRequirement.Policy };
            return compiler.CompileManaged(name, definition, managed);
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
        var authored = ReadBlock(block.Key, property => block.GetProperty(property)?.GetValue(instance.Culture));
        if (authored?.DataSource is null) return null;
        var result = Copy(authored.Definition);
        await bindings.BindAsync(result, authored.DataSource, ct);
        result.SourceName = name;
        result.Authorization = BlockAuthorization(instance.Page, authored.DataSource);
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
        // A paper report always renders for a person, so it is no action to offer someone anonymous.
        if (documents is not null && !CallerIsAnonymous && block.Value<IPublishedContent>("paperReport") is not null && block.Value<string>("keyColumn") is { Length: > 0 } printKey)
            AppBlocks.InteractiveReports.InteractiveReportPrintLinks.AddPrintAction(result, printKey);
        return compiler.CompileManaged(name, result, managed);
    }
    private PublishedReport? ReadBlock(Guid key, Func<string, object?> value) => catalog.ReadBlockReport(key, value, new List<string>());
    private static ReportDefinition Copy(ReportDefinition source) => JsonSerializer.Deserialize<ReportDefinition>(JsonSerializer.Serialize(source, IrJson.Options), IrJson.Options)!;
}
