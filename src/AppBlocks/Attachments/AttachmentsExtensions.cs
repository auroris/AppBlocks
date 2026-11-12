using AppBlocks.Connections;
using AppBlocks.Databases;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AppBlocks.Blocks;

namespace AppBlocks.Attachments;

public static class AttachmentsExtensions
{
    /// <summary>The record attachments feature: its settings, store and access rules. The endpoints are mapped by <see cref="AttachmentEndpoints.MapAttachments"/>.</summary>
    public static IUmbracoBuilder AddAttachments(this IUmbracoBuilder builder, IConfiguration configuration)
    {
        builder.Services.AddOptions<AttachmentSettings>().Bind(configuration.GetSection(AttachmentSettings.Section));
        builder.Services.AddScoped<AttachmentStore>();
        builder.Services.AddRecurringBackgroundJob<AttachmentCleanupJob>();
        // Shared with the Tabular form block, so either feature works without the other.
        builder.Services.TryAddScoped<BlockOwnerAccess>();
        builder.Services.AddScoped<AttachmentAccess>();
        // The cleanup job looks at the tables through the setup screen's own check before it touches the store.
        builder.Services.AddSingleton<AttachmentDatabaseSetup>();
        builder.Services.AddSingleton<IDatabaseSetupFeature>(services => services.GetRequiredService<AttachmentDatabaseSetup>());
        return builder;
    }

    /// <summary>
    /// Registers the optional <c>AttachmentsDb</c> store. Uploads use <c>AppBlocksDb</c> by default;
    /// <c>Attachments:Connection</c> can select this separate file or another named connection.
    /// The attachment tables are created in the selected database on first use, when its account may; see the README's database setup.
    /// </summary>
    public static AppBlocksBuilder AddAttachmentsDatabase(this AppBlocksBuilder connections, IConfiguration configuration)
    {
        return connections.AddConnection(AttachmentSettings.SeparateConnection,
            provider => provider.GetRequiredService<ConnectionCatalog>().Openable(AttachmentSettings.SeparateConnection));
    }
}
