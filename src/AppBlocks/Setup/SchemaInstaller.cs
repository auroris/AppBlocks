using System.Net;
using System.Xml.Linq;
using Microsoft.Extensions.FileProviders;
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

/// <summary>Adds missing package definitions. Existing site definitions are never replaced on startup.</summary>
public sealed class SchemaInstaller(IContentTypeService contentTypes, IDataTypeService dataTypes,
    IContentTypeContainerService contentFolders, IDataTypeContainerService dataFolders,
    PropertyEditorCollection editors, IConfigurationEditorJsonSerializer serializer, IShortStringHelper strings,
    ICoreScopeProvider scopes, ILogger<SchemaInstaller> logger, IEnumerable<AppBlocksSchema> schemas,
    IEnumerable<IAppBlocksSchemaMigration> migrations)
    : INotificationAsyncHandler<UmbracoApplicationStartedNotification>
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken cancellationToken)
        => EnsureAsync(cancellationToken);

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
                type.AllowedContentTypes = children.Select(child =>
                {
                    var key = Guid.Parse(child.Attribute("Key")!.Value);
                    var target = contentTypes.Get(key) ?? contentTypes.Get(child.Value.Trim())
                        ?? throw new InvalidOperationException($"AppBlocks content type '{type.Alias}' requires missing child type '{child.Value}'.");
                    return new ContentTypeSort(target.Key, (int)child.Attribute("SortOrder")!, target.Alias);
                }).ToArray();
                var result = await contentTypes.UpdateAsync(type, Constants.Security.SuperUserKey);
                if (!result.Success) throw new InvalidOperationException($"AppBlocks could not install child types for '{type.Alias}': {result.Result}.");
            }

            foreach (var migration in migrations)
            {
                ct.ThrowIfCancellationRequested();
                await migration.EnsureAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            scope.Complete();
            if (installed > 0) logger.LogInformation("Installed {Count} missing AppBlocks definitions.", installed);
        }
        finally { Gate.Release(); }
    }

    private IEnumerable<XElement> Definitions(string kind)
    {
        foreach (var schema in schemas.DistinctBy(s => s.Assembly))
        foreach (var file in new ManifestEmbeddedFileProvider(schema.Assembly).GetDirectoryContents("Schema/" + kind)
            .Where(f => !f.IsDirectory).OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            using var stream = file.CreateReadStream();
            yield return XElement.Load(stream);
        }
    }

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
            type.PropertyGroups.Add(new PropertyGroup(true)
            {
                Key = Guid.Parse(Value(tab, "Key")),
                Name = Value(tab, "Caption"),
                Alias = Value(tab, "Alias"),
                Type = Enum.Parse<PropertyGroupType>(Value(tab, "Type")),
                SortOrder = (int)tab.Element("SortOrder")!,
            });
        foreach (var property in definition.Element("GenericProperties")!.Elements("GenericProperty"))
        {
            var dataTypeKey = Guid.Parse(Value(property, "Definition"));
            var dataType = await dataTypes.GetAsync(dataTypeKey)
                ?? throw new InvalidOperationException($"AppBlocks property '{type.Alias}.{Value(property, "Alias")}' requires missing data type '{dataTypeKey}'.");
            var propertyType = new PropertyType(strings, dataType)
            {
                Key = Guid.Parse(Value(property, "Key")),
                Alias = Value(property, "Alias"),
                Name = Value(property, "Name"),
                Description = Value(property, "Description"),
                SortOrder = (int)property.Element("SortOrder")!,
                Mandatory = (bool)property.Element("Mandatory")!,
                MandatoryMessage = Value(property, "MandatoryMessage"),
                ValidationRegExp = Value(property, "Validation"),
                ValidationRegExpMessage = Value(property, "ValidationRegExpMessage"),
                LabelOnTop = (bool)property.Element("LabelOnTop")!,
                Variations = Enum.Parse<ContentVariation>(Value(property, "Variations")),
            };
            var group = (string?)property.Element("Tab")?.Attribute("Alias");
            if (string.IsNullOrEmpty(group)) type.AddPropertyType(propertyType);
            else type.AddPropertyType(propertyType, group);
        }
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

    private static string Value(XElement element, string name) => element.Element(name)?.Value.Trim() ?? "";
    private static int? OptionalInt(XElement element, string name) => Value(element, name) is { Length: > 0 } value ? int.Parse(value) : null;
}
