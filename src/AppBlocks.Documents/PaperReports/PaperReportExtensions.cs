using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using UmbracoAppHost.Blocks;
using UmbracoAppHost.DataSources;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using AppBlocks.Documents;

namespace UmbracoAppHost.PaperReports;

public static class PaperReportExtensions
{
    public static IUmbracoBuilder AddPaperReports(this IUmbracoBuilder builder)
        => builder.AddAppBlocksDocuments();

    internal static void RegisterPaperReports(IUmbracoBuilder builder)
    {
        builder.Services.AddScoped<SelfContainedHtml>();
        builder.Services.AddScoped<PaperReportRenderer>();
        builder.Services.AddScoped<PaperReportDelivery>();
        // Authored assets are fetched from the server's own network position: briefly, small, and never by redirect.
        builder.Services.AddHttpClient(SelfContainedHtml.ClientName, client => {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.MaxResponseContentBufferSize = 8 * 1024 * 1024;
        }).ConfigurePrimaryHttpMessageHandler(SelfContainedHtml.CreateHandler);
        // Delivery applies one current settings snapshot to each client, so backoffice edits apply immediately.
        builder.Services.AddHttpClient("Pressroom");
        builder.AddNotificationHandler<ContentPublishingNotification, PaperReportPublishValidator>();
        // Registered after AppBlocks' schema installer so fresh sites already have the document type.
        builder.AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, PaperReportTemplates>();
        builder.AddNotificationAsyncHandler<TemplateSavedNotification, PaperReportTemplates>();
        builder.AddNotificationAsyncHandler<TemplateDeletedNotification, PaperReportTemplates>();
        // A backoffice import of the library's definition would narrow the list again until the next start.
        builder.AddNotificationAsyncHandler<ContentTypeSavedNotification, PaperReportTemplates>();
    }

    public static void MapPaperReports(this IEndpointRouteBuilder endpoints)
    {
        if (endpoints.DataSources.SelectMany(source => source.Endpoints).Any(endpoint => endpoint.Metadata.GetMetadata<DocumentEndpoint>() is not null)) return;
        endpoints.MapGet(DocumentLinks.Route, Download).WithMetadata(new DocumentEndpoint());
    }

    private sealed record DocumentEndpoint;

    private static async Task<IResult> Download(Guid key, HttpContext http, IUmbracoContextFactory contexts, AppPageAccess access,
        PaperReportRenderer renderer, PaperReportDelivery delivery, CancellationToken ct)
    {
        using var reference = contexts.EnsureUmbracoContext();
        var page = reference.UmbracoContext.Content?.GetById(false, key);
        if (page?.ContentType.Alias != PaperReportDefinition.Alias) return Results.NotFound();
        if (await access.UserAsync(page) is not { } user)
        {
            // Paper reports always render for a person, even on a public node, so say who may download one.
            return (await http.AuthenticateAsync(IdentityConstants.ApplicationScheme)).Succeeded
                ? Results.Problem("This member account does not have access to the paper report.", statusCode: 403, title: "Access denied")
                : Results.Problem("Paper reports are delivered to a signed-in member with access to the report, or to a signed-in backoffice Administrator. Sign in, then download the report again.",
                    statusCode: 403, title: "Sign in to download this paper report");
        }
        http.User = user;
        http.Response.Headers.CacheControl = "no-store";
        try { return await delivery.DeliverAsync(await renderer.RenderAsync(page, http, ct), http, ct); }
        catch (UnauthorizedAccessException) { return Results.Problem("This account cannot read the paper report's data.", statusCode: 403, title: "Access denied"); }
        catch (Exception error) when (error is DataSourceException or JsonException or CultureNotFoundException or FormatException)
        { return Results.Problem(error.Message, statusCode: 422); }
    }
}

public sealed class PaperReportPublishValidator(ITemplateService templates, ICompositeViewEngine views) : INotificationHandler<ContentPublishingNotification>
{
    public void Handle(ContentPublishingNotification notification)
    {
        foreach (var page in notification.PublishedEntities.Where(p => p.ContentType.Alias == PaperReportDefinition.Alias))
        {
            try
            {
                _ = PaperReportDefinition.Read(alias => page.GetValue(alias));
                if (page.TemplateId is not { } templateId) throw new DataSourceException("Choose a template for the paper report.");
                // Every download renders ~/Views/{alias}.cshtml, so a template registered without its view would fail them all.
                var template = templates.GetAsync(templateId).GetAwaiter().GetResult();
                if (template is null || !views.GetView(null, "~/Views/" + template.Alias + ".cshtml", isMainPage: false).Success)
                    throw new DataSourceException($"The paper report's template has no view. Add Views/{template?.Alias ?? "the template"}.cshtml, or choose another template.");
            }
            catch (Exception error) when (error is DataSourceException or JsonException)
            { notification.CancelOperation(new EventMessage("Paper report", error.Message, EventMessageType.Error)); }
        }
    }
}
