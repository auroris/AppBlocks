using System.Net;
using System.Xml.Linq;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Entities;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;

namespace AppBlocks.Setup;

/// <summary>
/// Installs missing package definitions from the current schema for a fresh site.
/// Existing site definitions are never replaced on startup. Runs when the application has started, and a site that
/// imports definitions from elsewhere first calls <see cref="EnsureAsync"/> itself, as UmbracoAppHost does for uSync.
/// </summary>
public sealed class SchemaInstaller(IContentTypeService contentTypes, IDataTypeService dataTypes,
    IContentTypeContainerService contentFolders, IDataTypeContainerService dataFolders,
    PropertyEditorCollection editors, IConfigurationEditorJsonSerializer serializer, IShortStringHelper strings,
    ICoreScopeProvider scopes, ILogger<SchemaInstaller> logger, IEnumerable<AppBlocksSchema> schemas,
    SchemaProperties properties, IRuntimeState runtime)
    : INotificationAsyncHandler<UmbracoApplicationStartedNotification>
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken cancellationToken)
        // The installation wizard starts the application before a database exists. Direct callers still decide
        // when to call EnsureAsync themselves, including hosts installing definitions before a schema import.
        => runtime.Level == RuntimeLevel.Run ? EnsureAsync(cancellationToken) : Task.CompletedTask;

    public async Task EnsureAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            // Roll back folders and definitions together, so a retry cannot mistake a partial type for a customization.
            using var scope = scopes.CreateCoreScope();
            var installed = 0;
            foreach (var definition in Definitions("DataTypes"))
            {
                ct.ThrowIfCancellationRequested();
                var key = Guid.Parse(definition.Attribute("Key")!.Value);
                if (await dataTypes.GetAsync(key) is not null) continue;
                var info = definition.Element("Info")!;
                var editorAlias = Value(info, "EditorAlias");
                var editor = editors.FirstOrDefault(e => e.Alias == editorAlias)
                    ?? throw new InvalidOperationException($"AppBlocks data type '{Value(info, "Name")}' requires missing editor '{editorAlias}'.");
                var type = new DataType(editor, serializer, await FolderAsync(dataFolders, Value(info, "Folder")))
                {
                    Key = key,
                    Name = Value(info, "Name"),
                    EditorUiAlias = Value(info, "EditorUIAlias"),
                    // Stored as the editor stores it, as a site's own data types are; the type's default is long text whatever the editor.
                    DatabaseType = ValueTypes.ToStorageType(editor.GetValueEditor().ValueType),
                    ConfigurationData = editor.GetConfigurationEditor().FromDatabase(Value(definition, "Config"), serializer),
                };
                var result = await dataTypes.CreateAsync(type, Constants.Security.SuperUserKey);
                if (!result.Success) throw new InvalidOperationException($"AppBlocks could not install data type '{type.Name}' ({key}): {result.Status}.");
                installed++;
            }

            var created = new List<(XElement Definition, IContentType Type)>();
            foreach (var definition in Definitions("ContentTypes"))
            {
                ct.ThrowIfCancellationRequested();
                var key = Guid.Parse(definition.Attribute("Key")!.Value);
                var alias = definition.Attribute("Alias")!.Value;
                if (contentTypes.Get(key) is not null || contentTypes.Get(alias) is not null) continue;
                var type = await ContentTypeAsync(definition);
                var result = await contentTypes.CreateAsync(type, Constants.Security.SuperUserKey);
                if (!result.Success) throw new InvalidOperationException($"AppBlocks could not install content type '{alias}' ({key}): {result.Result}.");
                created.Add((definition, type));
                installed++;
            }

            // All referenced types now exist. Only types created in this transaction may be changed.
            foreach (var (definition, type) in created)
            {
                ct.ThrowIfCancellationRequested();
                var children = definition.Element("Structure")!.Elements("ContentType").ToArray();
                if (children.Length == 0) continue;
                var allowed = new List<ContentTypeSort>();
                foreach (var child in children.OrderBy(child => (int)child.Attribute("SortOrder")!))
                {
                    var key = Guid.Parse(child.Attribute("Key")!.Value);
                    var target = contentTypes.Get(key) ?? contentTypes.Get(child.Value.Trim());
                    // A fresh site can install the base package without its optional companions.
                    if (target is null && (bool?)child.Attribute("Optional") == true) continue;
                    if (target is null) throw new InvalidOperationException($"AppBlocks content type '{type.Alias}' requires missing child type '{child.Value}'.");
                    allowed.Add(new ContentTypeSort(target.Key, allowed.Count, target.Alias));
                }
                type.AllowedContentTypes = allowed;
                var result = await contentTypes.UpdateAsync(type, Constants.Security.SuperUserKey);
                if (!result.Success) throw new InvalidOperationException($"AppBlocks could not install child types for '{type.Alias}': {result.Result}.");
            }

            ct.ThrowIfCancellationRequested();
            scope.Complete();
            if (installed > 0) logger.LogInformation("Installed {Count} missing AppBlocks definitions.", installed);
        }
        finally { Gate.Release(); }
    }

    private IEnumerable<XElement> Definitions(string kind) => schemas.DistinctBy(s => s.Assembly).SelectMany(schema => schema.Definitions(kind));

    private async Task<IContentType> ContentTypeAsync(XElement definition)
    {
        var info = definition.Element("Info")!;
        // The package catalogue contains standalone types and no site templates or compositions.
        // Reject unsupported definitions instead of silently installing an incomplete type.
        if (info.Element("Compositions")!.HasElements || info.Element("AllowedTemplates")!.HasElements
            || Value(info, "DefaultTemplate").Length > 0)
            throw new InvalidOperationException($"AppBlocks schema '{definition.Attribute("Alias")!.Value}' must not declare site templates or compositions.");
        var type = new ContentType(strings, await FolderAsync(contentFolders, Value(info, "Folder")))
        {
            Key = Guid.Parse(definition.Attribute("Key")!.Value),
            Alias = definition.Attribute("Alias")!.Value,
            Name = Value(info, "Name"),
            Icon = Value(info, "Icon"),
            Thumbnail = Value(info, "Thumbnail"),
            Description = Value(info, "Description"),
            AllowedAsRoot = (bool)info.Element("AllowAtRoot")!,
            IsElement = (bool)info.Element("IsElement")!,
            AllowedInLibrary = (bool)info.Element("AllowedInLibrary")!,
            Variations = Enum.Parse<ContentVariation>(Value(info, "Variations")),
            ListView = Guid.Parse(Value(info, "ListView")),
        };
        if (info.Element("HistoryCleanup") is { } cleanup)
            type.HistoryCleanup = new Umbraco.Cms.Core.Models.ContentEditing.HistoryCleanup
            {
                PreventCleanup = (bool)cleanup.Element("PreventCleanup")!,
                KeepAllVersionsNewerThanDays = OptionalInt(cleanup, "KeepAllVersionsNewerThanDays"),
                KeepLatestVersionPerDayForDays = OptionalInt(cleanup, "KeepLatestVersionPerDayForDays"),
            };
        foreach (var tab in definition.Element("Tabs")!.Elements("Tab"))
            type.PropertyGroups.Add(SchemaProperties.Group(tab));
        foreach (var property in definition.Element("GenericProperties")!.Elements("GenericProperty"))
            SchemaProperties.Add(type, await properties.PropertyAsync(type.Alias, property), (string?)property.Element("Tab")?.Attribute("Alias"));
        return type;
    }

    private static async Task<int> FolderAsync<T>(IEntityTypeContainerService<T> folders, string path) where T : ITreeEntity
    {
        EntityContainer? parent = null;
        var level = 1;
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = WebUtility.UrlDecode(segment);
            var folder = (await folders.GetAsync(name, level++)).FirstOrDefault(f => f.ParentId == (parent?.Id ?? -1));
            if (folder is null)
            {
                var result = await folders.CreateAsync(null, name, parent?.Key, Constants.Security.SuperUserKey);
                if (!result.Success || result.Result is null)
                    throw new InvalidOperationException($"AppBlocks could not create schema folder '{path}': {result.Status}.");
                folder = result.Result;
            }
            parent = folder;
        }
        return parent?.Id ?? -1;
    }

    private static string Value(XElement element, string name) => SchemaProperties.Value(element, name);
    private static int? OptionalInt(XElement element, string name) => Value(element, name) is { Length: > 0 } value ? int.Parse(value) : null;
}
