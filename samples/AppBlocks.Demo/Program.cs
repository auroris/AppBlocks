var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Development.local.json", optional: true);
builder.Services.AddScoped<AppBlocks.Forms.IFormResource, DemoTasks>();
builder.CreateUmbracoBuilder().AddBackOffice().AddWebsite().AddComposers()
    .AddNotificationAsyncHandler<Umbraco.Cms.Core.Notifications.UmbracoApplicationStartedNotification, DemoSeed>().Build();
await using var app = builder.Build();
await app.BootUmbracoAsync();
app.UseUmbraco().WithMiddleware(u => { u.UseBackOffice(); u.UseWebsite(); })
    .WithEndpoints(u => { u.UseBackOfficeEndpoints(); u.UseWebsiteEndpoints(); });
await app.RunAsync();
