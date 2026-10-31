using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Pressroom.Contracts;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services;
using AppBlocks.Blocks;
using AppBlocks.DataSources;
using AppBlocks.Forms;

namespace AppBlocks.Documents.PaperReports;

public sealed record PaperReportDocument(string Html, string FileName, RenderOptions Options);

public sealed class PaperReportRenderer(DataSourceCatalog sources, DataSourceQueries queries, IDataSourceUserResolver users,
    AppBindVariableResolver variables, ITemplateService files, ICompositeViewEngine views, ITempDataProvider tempData,
    IDocumentPresentationProvider presentation, SelfContainedHtml assets)
{
    public async Task<PaperReportDocument> RenderAsync(IPublishedContent page, HttpContext http, CancellationToken ct)
    {
        var definition = PaperReportDefinition.Read(alias => page.GetProperty(alias)?.GetValue());
        var source = sources.DataSources.FirstOrDefault(s => s.Definition.Key == definition.DataSourceKey)
            ?? throw new DataSourceException("The paper report's data source is not published.");
        var person = await users.ResolveAsync(http.User, ct) ?? throw new UnauthorizedAccessException();
        var values = await variables.ResolveAsync(http.User, PaperReportDefinition.Query(http.Request.Query));
        var parameters = definition.BindParameters(values);
        var culture = definition.ResolveCulture(values);
        var data = await queries.QueryAsync(source.Definition, new FormExecutionContext(http.User, page.Key, page.Key,
            page.UpdateDate.Ticks.ToString(CultureInfo.InvariantCulture), Parameters: parameters), definition.MaximumRows, ct);
        var theme = await presentation.ResolveAsync(page, ct);
        var model = new PaperReportModel(page, data.Columns, data.Rows, parameters, person, culture, values.Now, theme.Theme);
        var template = page.TemplateId is { } templateId ? await files.GetAsync(templateId) : null;
        if (template is null) throw new DataSourceException("Choose a template for the paper report.");
        var view = views.GetView(null, "~/Views/" + template.Alias + ".cshtml", isMainPage: false);
        if (!view.Success) throw new DataSourceException("The paper report template could not be found.");
        var action = new ActionContext(http, http.GetRouteData(), new ActionDescriptor());
        var viewData = new ViewDataDictionary<PaperReportModel>(new EmptyModelMetadataProvider(), new ModelStateDictionary()) { Model = model };
        using var writer = new StringWriter(culture);
        var context = new ViewContext(action, view.View, viewData, new TempDataDictionary(http, tempData), writer, new HtmlHelperOptions());
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            await view.View.RenderAsync(context);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
        var documentUri = new Uri($"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}/");
        if (page.Url() is { Length: > 0 } pageUrl && pageUrl != "#") documentUri = new Uri(documentUri, pageUrl);
        var pageSize = definition.Options.PageSize;
        var size = pageSize?.Width is { } width ? definition.Options.Landscape ? pageSize.Height + " " + width : width + " " + pageSize.Height
            : (pageSize?.Name ?? "Letter") + (definition.Options.Landscape ? " landscape" : "");
        var margins = definition.Options.Margins ?? new PageMargins();
        var pageStyle = definition.Options.PreferCssPageSize ? null
            : "@page { size: " + size + "; margin: " + margins.Top + " " + margins.Right + " " + margins.Bottom + " " + margins.Left + "; }";
        var html = await assets.EmbedAsync(writer.ToString(), documentUri, http.Request.PathBase, theme.PrintStylesheet, ct, pageStyle);
        var options = definition.Options with {
            Locale = culture.Name,
            HeaderTemplate = definition.Options.HeaderTemplate is { } header ? await assets.EmbedAsync(header, documentUri, http.Request.PathBase, null, ct) : null,
            FooterTemplate = definition.Options.FooterTemplate is { } footer ? await assets.EmbedAsync(footer, documentUri, http.Request.PathBase, null, ct) : null,
        };
        return new(html, definition.ResolveFileName(parameters, values.Now), options);
    }
}
