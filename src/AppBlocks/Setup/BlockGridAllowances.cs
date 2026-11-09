using System.Text.Json.Nodes;
using AppBlocks.Connections;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Migrations.Notifications;

namespace AppBlocks.Setup;

/// <summary>An element type allowed in a Block Grid data type, as <see cref="AppBlocksBuilder.AllowInBlockGrid"/> registers it.</summary>
internal sealed record BlockGridAllowance(string DataType, string ElementType, int ColumnSpan, int RowSpan, string? Area);

/// <summary>
/// Adds each element type registered with <see cref="AppBlocksBuilder.AllowInBlockGrid"/> to its Block Grid data type once the definitions
/// are there: at startup when nothing waits to be upgraded, after the package migrations when something did, and when an element type it
/// names is saved, as a package's own startup may create one after this ran. Each pass reads the data type afresh and saves it only when
/// it adds something, so repeating it changes nothing.
/// </summary>
public sealed class BlockGridAllowances(AppBlocksBuilder builder, IRuntimeState runtime, IDataTypeService dataTypes, IContentTypeService contentTypes,
    IConfigurationEditorJsonSerializer serializer, ILogger<BlockGridAllowances> logger)
    : INotificationAsyncHandler<UmbracoApplicationStartedNotification>, INotificationAsyncHandler<MigrationPlansExecutedNotification>,
        INotificationAsyncHandler<ContentTypeSavedNotification>
{
    public Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken ct)
        => runtime.Level == RuntimeLevel.Run ? ApplyAsync(builder.BlockGridAllowances, ct) : Task.CompletedTask;

    public Task HandleAsync(MigrationPlansExecutedNotification notification, CancellationToken ct) => ApplyAsync(builder.BlockGridAllowances, ct);

    public Task HandleAsync(ContentTypeSavedNotification notification, CancellationToken ct)
    {
        var saved = notification.SavedEntities.Select(type => type.Alias).ToHashSet(StringComparer.Ordinal);
        return ApplyAsync(builder.BlockGridAllowances.Where(allowance => saved.Contains(allowance.ElementType)).ToArray(), ct);
    }

    /// <summary>Applies every registration; a data type or element type that is not there yet is skipped and tried at the next pass.</summary>
    internal async Task ApplyAsync(IReadOnlyList<BlockGridAllowance> allowances, CancellationToken ct)
    {
        foreach (var group in allowances.GroupBy(allowance => allowance.DataType, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            try { await ApplyAsync(group.Key, group.ToArray()); }
            // One grid that cannot be changed must not stop the site starting; the next start tries again.
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogWarning(error, "AppBlocks could not allow the registered blocks in the Block Grid data type {DataType}.", group.Key);
            }
        }
    }

    private async Task ApplyAsync(string name, IReadOnlyList<BlockGridAllowance> allowances)
    {
        if (await dataTypes.GetAsync(name) is not { EditorAlias: Constants.PropertyEditors.Aliases.BlockGrid, Editor: { } editor } grid)
        {
            logger.LogDebug("No Block Grid data type is named {DataType} yet, so its registered blocks wait.", name);
            return;
        }
        var configurationEditor = editor.GetConfigurationEditor();
        var configuration = JsonNode.Parse(configurationEditor.ToDatabase(grid.ConfigurationData, serializer))?.AsObject() ?? [];
        if (configuration["blocks"] is not JsonArray blocks) configuration["blocks"] = blocks = [];
        var changed = false;
        foreach (var allowance in allowances)
        {
            if (contentTypes.Get(allowance.ElementType) is not { IsElement: true } element)
            {
                logger.LogDebug("No element type {Alias} yet, so it waits to be allowed in {DataType}.", allowance.ElementType, name);
                continue;
            }
            var key = element.Key.ToString();
            if (!blocks.Any(block => Same(block?["contentElementTypeKey"], key)))
            {
                blocks.Add(new JsonObject {
                    ["contentElementTypeKey"] = key, ["allowAtRoot"] = allowance.Area is null, ["allowInAreas"] = true,
                    ["columnSpanOptions"] = new JsonArray(new JsonObject { ["columnSpan"] = allowance.ColumnSpan }),
                    ["rowMinSpan"] = allowance.RowSpan, ["rowMaxSpan"] = allowance.RowSpan, ["areas"] = new JsonArray(),
                });
                changed = true;
            }
            if (allowance.Area is not { } area) continue;
            var target = blocks.SelectMany(block => block?["areas"] as JsonArray ?? []).FirstOrDefault(candidate =>
                Same(candidate?["key"], area) || Same(candidate?["alias"], area));
            if (target is null) { logger.LogWarning("The Block Grid data type {DataType} has no area {Area}, so {Alias} is not allowed in one.", name, area, allowance.ElementType); continue; }
            // An area with no list of its own takes any block the grid allows in areas.
            if (target["specifiedAllowance"] is JsonArray { Count: > 0 } allowed && !allowed.Any(entry => Same(entry?["elementTypeKey"], key)))
            {
                allowed.Add(new JsonObject { ["elementTypeKey"] = key });
                changed = true;
            }
        }
        if (!changed) return;
        grid.ConfigurationData = configurationEditor.FromDatabase(configuration.ToJsonString(), serializer);
        var result = await dataTypes.UpdateAsync(grid, Constants.Security.SuperUserKey);
        if (!result.Success) throw new InvalidOperationException($"The data type could not be saved: {result.Status}.");
        logger.LogInformation("Allowed {Aliases} in the Block Grid data type {DataType}.", string.Join(", ", allowances.Select(a => a.ElementType)), name);
    }

    private static bool Same(JsonNode? node, string value) => node?.GetValueKind() == System.Text.Json.JsonValueKind.String
        && node.GetValue<string>().Equals(value, StringComparison.OrdinalIgnoreCase);
}
