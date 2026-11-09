using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Infrastructure.Migrations;
using AppBlocks.InteractiveReports.Blocks;
using AppBlocks.Setup;

namespace AppBlocks.InteractiveReports.Setup;

/// <summary>
/// Gives an existing site's Interactive Report block its Create button and Open link settings, each with its backoffice and member
/// group pickers, on the Configuration tab, and the data types they need when the site lacks them, all as the package's schemas
/// declare them. Does nothing when the package's <c>appReport</c> type is not there, as on a fresh site, which the installer gives the
/// current definitions, or for a property the type already has. Blocks keep no value for them, which means Form page decides, except
/// where a block still holds the earlier Record links choice: that choice becomes Hidden on the link it left out, in every stored
/// version, drafts and published values alike.
/// </summary>
public sealed class AddReportLinkVisibility(IMigrationContext context, IContentTypeService contentTypes, IDataTypeService dataTypes,
    IDataTypeContainerService dataFolders, PropertyEditorCollection editors, IConfigurationEditorJsonSerializer serializer, IShortStringHelper strings,
    IOptions<PackageMigrationSettings> settings, IKeyValueService keyValues, ICoreScopeProvider scopes)
    : AsyncMigrationBase(context)
{
    internal static readonly string[] Properties = [.. new[] { ReportLinkRule.Create, ReportLinkRule.Open }
        .SelectMany(link => new[] { link + "Visibility", link + "UserGroups", link + "MemberGroups" })];

    /// <summary>The report block's earlier setting: Open and create, Open only, Create only or None.</summary>
    internal const string RecordLinks = "recordLinks";
    private static readonly JsonSerializerOptions Unescaped = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    protected override async Task MigrateAsync()
    {
        using var scope = scopes.CreateCoreScope();
        // The migration records its state even when no schema change is needed; lock before the first read on SQLite.
        scope.EagerWriteLock(Constants.Locks.KeyValues);
        await UpdateAsync();
        scope.Complete();
    }

    private async Task UpdateAsync()
    {
        if (!settings.Value.RunSchemaAndContentMigrations || keyValues.GetValue(SchemaInstaller.UninstalledKey) == "true") return;
        var schema = new AppBlocksSchema(typeof(AddReportLinkVisibility).Assembly);
        var definition = schema.Definitions("ContentTypes").Single(d => d.Attribute("Alias")!.Value == "appReport");
        // The package's type, by key: a site's own type that took the alias is not the package's to change.
        if (contentTypes.Get(Guid.Parse(definition.Attribute("Key")!.Value)) is not { } type) return;
        // The member group pickers use the base package's data type.
        await new CompanionSchemaProperties(contentTypes, dataTypes, dataFolders, editors, serializer, strings).AddMissingAsync(type, definition, Properties);

        // Block values live inside the pages' block editor values; only those that mention the earlier property can hold its choice.
        var mapped = false;
        foreach (var stored in Database.Fetch<StoredText>("SELECT id, textValue FROM umbracoPropertyData WHERE textValue LIKE @0", "%\"" + RecordLinks + "\"%"))
        {
            if (stored.TextValue is null || MapRecordLinks(stored.TextValue, type.Key) is not { } value) continue;
            Database.Execute("UPDATE umbracoPropertyData SET textValue = @0 WHERE id = @1", value, stored.Id);
            mapped = true;
        }
        if (mapped) RebuildCache = true;
    }

    /// <summary>
    /// Gives each report block in a block editor value the visibility its Record links choice meant, on a link whose visibility the
    /// block does not already hold: Open only hides the Create button, Create only hides the Open link, and None hides both. Open and
    /// create meant what an empty visibility means. Report blocks nested in other blocks' values are visited too.
    /// </summary>
    /// <returns>The changed value, or null when nothing in it changed or it is not JSON.</returns>
    internal static string? MapRecordLinks(string value, Guid reportTypeKey)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(value); }
        catch (JsonException) { return null; }
        // Rich text in a block keeps its characters as written rather than escaped.
        return root is not null && Visit(root) ? root.ToJsonString(Unescaped) : null;

        bool Visit(JsonNode node)
        {
            var changed = false;
            switch (node)
            {
                case JsonObject item:
                    if (Text(item, "contentTypeKey") is { } type && Guid.TryParse(type, out var key) && key == reportTypeKey && item["values"] is JsonArray values)
                        changed |= MapBlock(values);
                    foreach (var (_, child) in item.ToArray())
                        if (child is not null) changed |= Visit(child);
                    break;
                case JsonArray array:
                    foreach (var child in array.ToArray())
                        if (child is not null) changed |= Visit(child);
                    break;
                // A nested block editor's value stored as text.
                case JsonValue nested when nested.TryGetValue<string>(out var inner) && inner.Contains("\"" + RecordLinks + "\"", StringComparison.Ordinal)
                    && MapRecordLinks(inner, reportTypeKey) is { } replaced:
                    nested.ReplaceWith(JsonValue.Create(replaced));
                    changed = true;
                    break;
            }
            return changed;
        }
    }

    private static bool MapBlock(JsonArray values)
    {
        var changed = false;
        foreach (var choice in values.OfType<JsonObject>().Where(v => Text(v, "alias") == RecordLinks).ToArray())
        {
            string[] hidden = Text(choice, "value")?.Trim() switch
            {
                "Open only" => [ReportLinkRule.Create],
                "Create only" => [ReportLinkRule.Open],
                "None" => [ReportLinkRule.Create, ReportLinkRule.Open],
                _ => [],
            };
            foreach (var link in hidden)
            {
                var alias = link + "Visibility";
                var existing = values.OfType<JsonObject>().FirstOrDefault(v => Text(v, "alias") == alias
                    && Text(v, "culture") == Text(choice, "culture") && Text(v, "segment") == Text(choice, "segment"));
                if (existing is not null && !string.IsNullOrWhiteSpace(Text(existing, "value"))) continue;
                if (existing is null)
                {
                    existing = new JsonObject();
                    if (choice.ContainsKey("editorAlias")) existing["editorAlias"] = "Umbraco.RadioButtonList";
                    existing["culture"] = choice["culture"]?.DeepClone();
                    existing["segment"] = choice["segment"]?.DeepClone();
                    existing["alias"] = alias;
                    values.Add(existing);
                }
                existing["value"] = nameof(ReportLinkVisibility.Hidden);
                changed = true;
            }
        }
        return changed;
    }

    private static string? Text(JsonObject item, string name) => item[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private sealed class StoredText
    {
        public int Id { get; set; }
        public string? TextValue { get; set; }
    }
}
