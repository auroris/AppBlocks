using AppBlocks.Connections;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UmbracoAppHost.Blocks;
using UmbracoAppHost.Databases;

namespace UmbracoAppHost.Attachments;

public static class AttachmentsExtensions
{
    /// <summary>The record attachments feature: its settings, store and access rules. The endpoints are mapped by <see cref="AttachmentEndpoints.MapAttachments"/>.</summary>
    public static IUmbracoBuilder AddAttachments(this IUmbracoBuilder builder, IConfiguration configuration)
    {
        builder.Services.AddOptions<AttachmentSettings>().Bind(configuration.GetSection(AttachmentSettings.Section));
        builder.Services.AddScoped<AttachmentStore>();
        // Shared with the Tabular form block, so either feature works without the other.
        builder.Services.TryAddScoped<BlockOwnerAccess>();
        builder.Services.AddScoped<AttachmentAccess>();
        return builder;
    }

    /// <summary>
    /// The default store: the <c>AttachmentsDb</c> connection, a SQLite file in the instance data folder, with the
    /// two attachment tables created on first use. When <c>Attachments:Connection</c> names a connection of the host's
    /// own, nothing is registered here and the tables are created in that database when the store first opens it.
    /// </summary>
    public static AppBlocksBuilder AddAttachmentsDatabase(this AppBlocksBuilder connections, IConfiguration configuration)
    {
        var name = configuration[$"{AttachmentSettings.Section}:Connection"];
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == AttachmentSettings.DefaultConnection)
            connections.AddApplicationDatabase(AttachmentSettings.DefaultConnection, AttachmentStore.Schema, AttachmentSettings.DefaultDatabaseFile);
        return connections;
    }
}
