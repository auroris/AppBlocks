using AppBlocks.Forms;
using AppBlocks.Blocks;
using AppBlocks.Connections;
using System.Text.Json.Nodes;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;

// Test website content, copied into the disposable host only after package installation has been verified.
// The host retains the official template's Program.cs and loads AppBlocks exclusively from NuGet packages.
public sealed class CleanInstallComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.Services.AddScoped<IFormResource, DemoTasks>();
        builder.AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, DemoSeed>();
        builder.AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, CleanDocumentSeed>();
        builder.AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, CleanAttachmentSeed>();
        builder.AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, CleanDatabaseDriverProbe>();
    }
}

// Nothing listens on this port. An error from each driver shows that AppBlocks loaded the site's installed package;
// the settings screen hides connection errors, so verify.mjs reads the original exception here.
public sealed class CleanDatabaseDriverProbe(ConnectionCatalog connections, IWebHostEnvironment environment)
    : INotificationAsyncHandler<UmbracoApplicationStartedNotification>
{
    public async Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken ct)
    {
        var results = new Dictionary<string, string>();
        foreach (var provider in new[] { "postgres", "oracle", "oracle11g" })
        {
            var connectionString = provider == "postgres"
                ? "Host=127.0.0.1;Port=1;Database=probe;Username=probe;Password=probe;Timeout=1"
                : "Data Source=127.0.0.1:1/probe;User Id=probe;Password=probe;Connection Timeout=1";
            try
            {
                await connections.TestAsync(new SaveConnection("driver-probe", provider, connectionString), ct);
                results[provider] = "opened";
            }
            catch (Exception error) { results[provider] = error.GetType().FullName + ": " + error.Message; }
        }
        var path = Path.Combine(environment.ContentRootPath, "driver-probe.json");
        await System.IO.File.WriteAllTextAsync(path + ".tmp", System.Text.Json.JsonSerializer.Serialize(results), ct);
        System.IO.File.Move(path + ".tmp", path, overwrite: true);
    }
}

public sealed class CleanDocumentSeed(IContentService contents, IContentTypeService types, ITemplateService templates, IWebHostEnvironment environment)
    : INotificationAsyncHandler<UmbracoApplicationStartedNotification>
{
    public async Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken ct)
    {
        if (types.Get("paperReport") is null || contents.GetById(DemoSeed.Key("document")) is { Published: true }) return;
        var view = await System.IO.File.ReadAllTextAsync(Path.Combine(environment.ContentRootPath, "Views", "CleanDocument.cshtml"), ct);
        if (await templates.GetAsync("CleanDocument") is null) {
            var template = await templates.CreateAsync("Clean document", "CleanDocument", view, Constants.Security.SuperUserKey);
            if (!template.Success) throw new InvalidOperationException("Could not create clean document template.");
        }
#pragma warning disable CS0618
        var sources = contents.GetById(DemoSeed.Key("sources")) ?? contents.Create("Clean data sources", -1, "dataSources");
        sources.Key = DemoSeed.Key("sources");
        Save(sources);
        var source = contents.GetById(DemoSeed.Key("source")) ?? contents.Create("Clean tasks", sources.Id, "sqlDataSource");
        source.Key = DemoSeed.Key("source");
        source.SetValue("alias", "clean.document.tasks");
        source.SetValue("connection", "AppBlocksDb");
        source.SetValue("source", "AppBlocksDemoTasks");
        source.SetValue("keyColumns", "id");
        Save(source);
        var document = contents.GetById(DemoSeed.Key("document")) ?? contents.Create("Clean document", -1, "paperReport");
        document.Key = DemoSeed.Key("document");
        document.TemplateId = (await templates.GetAsync("CleanDocument"))!.Id;
        document.SetValue("dataSource", source.Key.ToString());
        Save(document);
        void Save(IContent content)
        {
            if (!contents.Save(content).Success) throw new InvalidOperationException("Could not save " + content.Name);
            var result = contents.Publish(content, ["*"]);
            if (!result.Success) throw new InvalidOperationException("Could not publish " + content.Name + ": " +
                string.Join("; ", result.EventMessages?.GetAll().Select(message => message.Message) ?? []));
        }
#pragma warning restore CS0618
    }
}

public sealed class CleanAttachmentSeed(IContentService contents) : INotificationAsyncHandler<UmbracoApplicationStartedNotification>
{
    public Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken ct)
    {
        var page = contents.GetById(DemoSeed.Key("page")) ?? throw new InvalidOperationException("Missing seeded page.");
        var body = JsonNode.Parse(page.GetValue<string>("mainContent")!)!;
        var container = body["contentData"]!.AsArray().Single(block => block!["key"]!.GetValue<string>() == DemoSeed.Key("main-container").ToString())!;
        var property = container["values"]!.AsArray().Single(value => value!["alias"]!.GetValue<string>() == "forms")!;
        var forms = JsonNode.Parse(property["value"]!.GetValue<string>())!;
        var key = DemoSeed.Key("files").ToString();
        if (forms["contentData"]!.AsArray().Any(block => block!["key"]!.GetValue<string>() == key)) return Task.CompletedTask;
        forms["contentData"]!.AsArray().Add(new JsonObject {
            ["key"] = key, ["contentTypeKey"] = AppBlockSchema.Key("appAttachments").ToString(),
            ["values"] = new JsonArray(new JsonObject { ["alias"] = "heading", ["value"] = "Task attachments", ["culture"] = null, ["segment"] = null }),
        });
        forms["expose"]!.AsArray().Add(new JsonObject { ["contentKey"] = key, ["culture"] = null, ["segment"] = null });
        forms["layout"]!["Umbraco.BlockGrid"]![0]!["areas"]![0]!["items"]!.AsArray().Add(new JsonObject {
            ["contentKey"] = key, ["settingsKey"] = null, ["columnSpan"] = 12, ["rowSpan"] = 1, ["areas"] = new JsonArray(),
        });
        property["value"] = forms.ToJsonString();
        page.SetValue("mainContent", body.ToJsonString());
#pragma warning disable CS0618
        if (!contents.Save(page).Success || !contents.Publish(page, ["*"]).Success) throw new InvalidOperationException("Could not publish attachments fixture.");
#pragma warning restore CS0618
        return Task.CompletedTask;
    }
}
