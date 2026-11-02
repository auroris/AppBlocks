using System.Net;
using System.Reflection;
using System.Xml.Linq;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Entities;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Packaging;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.OperationStatus;
using Umbraco.Cms.Core.Strings;

namespace AppBlocks.Setup;

/// <summary>
/// Installs missing package definitions from the current schema for a fresh site, and on request resets every definition to
/// the schema or removes them all. Existing site definitions are never replaced on startup: Umbraco's package migration plan of
/// each package (<see cref="AppBlocksPackagePlan"/>) carries changes to existing sites, and <see cref="ReinstallAsync"/> is the
/// reset for when no step does. Runs when the application has started, and a site that imports definitions from elsewhere first
/// calls <see cref="EnsureAsync"/> itself, as UmbracoAppHost does for uSync.
/// </summary>
public sealed class SchemaInstaller(IContentTypeService contentTypes, IDataTypeService dataTypes,
    IContentTypeContainerService contentFolders, IDataTypeContainerService dataFolders,
    PropertyEditorCollection editors, IConfigurationEditorJsonSerializer serializer, IShortStringHelper strings,
    ICoreScopeProvider scopes, ILogger<SchemaInstaller> logger, IEnumerable<AppBlocksSchema> schemas,
    SchemaProperties properties, IRuntimeState runtime, AppCaches caches, IIdKeyMap keys, IKeyValueService keyValues,
    PackageMigrationPlanCollection plans, PendingPackageMigrations pending, IMediaTypeService mediaTypes, IMemberTypeService memberTypes)
    : INotificationAsyncHandler<UmbracoApplicationStartedNotification>
{
    /// <summary>The key-value entry that <see cref="UninstallAsync"/> sets, so that no start installs the definitions again until <see cref="ReinstallAsync"/>.</summary>
    public const string UninstalledKey = "AppBlocks.Definitions.Uninstalled";

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken cancellationToken)
        // The installation wizard starts the application before a database exists. Direct callers still decide
        // when to call EnsureAsync themselves, including hosts installing definitions before a schema import.
        => runtime.Level == RuntimeLevel.Run ? EnsureAsync(cancellationToken) : Task.CompletedTask;

    /// <summary>Whether <see cref="UninstallAsync"/> removed the definitions and nothing has installed them since.</summary>
    public bool Uninstalled => keyValues.GetValue(UninstalledKey) == "true";

    /// <summary>Installs what is missing. After an uninstall it does nothing, and says so, until a reinstall.</summary>
    public async Task EnsureAsync(CancellationToken ct)
    {
        if (Uninstalled)
        {
            logger.LogInformation("The AppBlocks definitions were uninstalled from this site. Nothing installs until they are reinstalled from Settings > AppBlocks > Definitions.");
            return;
        }
        await RunAsync(() => InstallAsync(force: false, ct), ct);
    }

    /// <summary>
    /// Resets every definition to the schema, in place and by key, and installs what is missing: names, properties, allowed children,
    /// data type settings and folders. Content of these types keeps its values, except those of a property the schema no longer has,
    /// and a site's own type that took one of the aliases is left alone. All of it in one transaction, like an install.
    /// </summary>
    public async Task<SchemaChange> ReinstallAsync(CancellationToken ct)
    {
        var change = await RunAsync(() => InstallAsync(force: true, ct), ct);
        logger.LogInformation("Reinstalled the AppBlocks definitions: {Created} created, {Updated} reset. {Notes}", change.Created, change.Updated, string.Join(" ", change.Notes));
        return change;
    }

    /// <summary>
    /// Removes every definition of the schema from Umbraco, and the AppBlocks folders they leave empty, then keeps startup from
    /// installing them again. It refuses, changing nothing, while content of these types exists or a type of the site's own uses one
    /// of these data types, because deleting those would delete that content: the <see cref="SchemaInUseException"/> names each one.
    /// </summary>
    public async Task<SchemaChange> UninstallAsync(CancellationToken ct)
    {
        var change = await RunAsync(() => RemoveAsync(ct), ct);
        logger.LogWarning("Uninstalled the AppBlocks definitions: {Deleted} deleted. Nothing installs on startup until they are reinstalled.", change.Deleted);
        return change;
    }

    /// <summary>Each package's definitions and migration plan as they stand. Looking changes nothing.</summary>
    public async Task<DefinitionsStatus> StatusAsync(CancellationToken ct)
    {
        var states = keyValues.FindByKeyPrefix(Constants.Conventions.Migrations.KeyValuePrefix);
        var pendingPlans = pending.GetPendingPackageMigrations(states);
        var packages = new List<DefinitionsPackage>();
        foreach (var schema in schemas.DistinctBy(s => s.Assembly))
        {
            ct.ThrowIfCancellationRequested();
            var plan = plans.OfType<AppBlocksPackagePlan>().FirstOrDefault(p => p.GetType().Assembly == schema.Assembly);
            var name = schema.Assembly.GetName().Name!;
            var declared = 0;
            var installed = 0;
            foreach (var definition in schema.Definitions("ContentTypes")) { declared++; if (contentTypes.Get(Key(definition)) is not null) installed++; }
            foreach (var definition in schema.Definitions("DataTypes")) { declared++; if (await dataTypes.GetAsync(Key(definition)) is not null) installed++; }
            packages.Add(new DefinitionsPackage(plan?.PackageId ?? name, plan?.PackageName ?? name, Version(schema.Assembly), declared, installed,
                plan is null ? null : states.GetValueOrDefault(Constants.Conventions.Migrations.KeyValuePrefix + plan.Name), plan?.FinalState,
                plan is not null && pendingPlans.Contains(plan.Name)));
        }
        return new DefinitionsStatus(Uninstalled, packages);
    }

    private async Task<SchemaChange> RunAsync(Func<Task<SchemaChange>> action, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try { return await action(); }
        catch
        {
            // A rollback undoes the database, not Umbraco's caches, which took in each definition as it was written. Left there, a retry
            // in this process sees data types that do not exist, skips them, and fails on a foreign key instead of installing them.
            caches.IsolatedCaches.ClearAllCaches();
            caches.RuntimeCache.Clear();
            keys.ClearCache();
            throw;
        }
        finally { Gate.Release(); }
    }

    private async Task<SchemaChange> InstallAsync(bool force, CancellationToken ct)
    {
        // Roll back folders and definitions together, so a retry cannot mistake a partial type for a customization.
        using var scope = scopes.CreateCoreScope();
        var change = new Change();
        foreach (var definition in Definitions("DataTypes"))
        {
            ct.ThrowIfCancellationRequested();
            var key = Key(definition);
            var existing = await dataTypes.GetAsync(key);
            if (existing is not null && !force) continue;
            if (existing is null)
            {
                await CreateDataTypeAsync(definition, dataTypes, dataFolders, editors, serializer);
                change.Created++;
            }
            else
            {
                var (info, editor, storage, configuration) = Editor(definition, editors, serializer);
                var folder = await FolderAsync(dataFolders, Value(info, "Folder"));
                existing.Name = Value(info, "Name");
                existing.Editor = editor;
                existing.EditorUiAlias = Value(info, "EditorUIAlias");
                existing.DatabaseType = storage;
                existing.ConfigurationData = configuration;
                if (existing.ParentId != folder) existing.ParentId = folder;
                var result = await dataTypes.UpdateAsync(existing, Constants.Security.SuperUserKey);
                if (!result.Success) throw new InvalidOperationException($"AppBlocks could not reset data type '{existing.Name}' ({key}): {result.Status}.");
                change.Updated++;
            }
        }

        var touched = new List<(XElement Definition, IContentType Type, bool Reset)>();
        foreach (var definition in Definitions("ContentTypes"))
        {
            ct.ThrowIfCancellationRequested();
            var key = Key(definition);
            var alias = definition.Attribute("Alias")!.Value;
            var existing = contentTypes.Get(key);
            if (existing is null)
            {
                if (contentTypes.Get(alias) is { } taken)
                {
                    // The site's, by its key, and left alone whatever the schema says, so the package's type goes uninstalled.
                    if (force) change.Notes.Add($"Left the site's own '{taken.Name}' ({alias}) alone, so the package's {alias} is not installed.");
                    continue;
                }
                var type = await ContentTypeAsync(definition);
                var result = await contentTypes.CreateAsync(type, Constants.Security.SuperUserKey);
                if (!result.Success) throw new InvalidOperationException($"AppBlocks could not install content type '{alias}' ({key}): {result.Result}.");
                touched.Add((definition, type, false));
                change.Created++;
                continue;
            }
            if (!force) continue;
            await ResetAsync(existing, definition, change);
            var reset = await contentTypes.UpdateAsync(existing, Constants.Security.SuperUserKey);
            if (!reset.Success) throw new InvalidOperationException($"AppBlocks could not reset content type '{alias}' ({key}): {reset.Result}.");
            touched.Add((definition, existing, true));
            change.Updated++;
        }

        // All referenced types now exist. Only types created or reset in this transaction may be changed.
        foreach (var (definition, type, reset) in touched)
        {
            ct.ThrowIfCancellationRequested();
            var children = definition.Element("Structure")!.Elements("ContentType").ToArray();
            if (children.Length == 0 && !reset) continue;
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
        if (force) keyValues.SetValue(UninstalledKey, "false");
        scope.Complete();
        if (change.Created > 0 && !force) logger.LogInformation("Installed {Count} missing AppBlocks definitions.", change.Created);
        return change.Result();
    }

    /// <summary>Makes an existing type the schema's, keeping each property the site's content already has values for, by alias.</summary>
    private async Task ResetAsync(IContentType type, XElement definition, Change change)
    {
        var info = Info(definition);
        type.Name = Value(info, "Name");
        type.Icon = Value(info, "Icon");
        type.Thumbnail = Value(info, "Thumbnail");
        type.Description = Value(info, "Description");
        type.AllowedAsRoot = (bool)info.Element("AllowAtRoot")!;
        type.IsElement = (bool)info.Element("IsElement")!;
        type.AllowedInLibrary = (bool)info.Element("AllowedInLibrary")!;
        type.Variations = Enum.Parse<ContentVariation>(Value(info, "Variations"));
        type.ListView = Guid.Parse(Value(info, "ListView"));
        type.HistoryCleanup = Cleanup(info);
        var folder = await FolderAsync(contentFolders, Value(info, "Folder"));
        if (type.ParentId != folder) type.ParentId = folder;

        var tabs = definition.Element("Tabs")!.Elements("Tab").ToArray();
        foreach (var tab in tabs)
        {
            var wanted = SchemaProperties.Group(tab);
            if (type.PropertyGroups.FirstOrDefault(g => g.Alias == wanted.Alias) is { } group)
            {
                group.Key = wanted.Key;
                group.Name = wanted.Name;
                group.Type = wanted.Type;
                group.SortOrder = wanted.SortOrder;
            }
            else type.PropertyGroups.Add(wanted);
        }

        var aliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in definition.Element("GenericProperties")!.Elements("GenericProperty"))
        {
            var wanted = await properties.PropertyAsync(type.Alias, property);
            aliases.Add(wanted.Alias);
            var tab = (string?)property.Element("Tab")?.Attribute("Alias");
            if (type.PropertyTypes.FirstOrDefault(p => p.Alias == wanted.Alias) is not { } current)
            {
                SchemaProperties.Add(type, wanted, tab);
                continue;
            }
            // The same property, so its values stay with it: only what the schema says about it changes.
            current.Key = wanted.Key;
            current.Name = wanted.Name;
            current.Description = wanted.Description;
            current.SortOrder = wanted.SortOrder;
            current.Mandatory = wanted.Mandatory;
            current.MandatoryMessage = wanted.MandatoryMessage;
            current.ValidationRegExp = wanted.ValidationRegExp;
            current.ValidationRegExpMessage = wanted.ValidationRegExpMessage;
            current.LabelOnTop = wanted.LabelOnTop;
            current.Variations = wanted.Variations;
            current.DataTypeId = wanted.DataTypeId;
            current.DataTypeKey = wanted.DataTypeKey;
            current.PropertyEditorAlias = wanted.PropertyEditorAlias;
            current.ValueStorageType = wanted.ValueStorageType;
            if (!string.IsNullOrEmpty(tab) && type.PropertyGroups.FirstOrDefault(g => g.PropertyTypes?.Contains(wanted.Alias) == true)?.Alias != tab)
                type.MovePropertyType(wanted.Alias, tab);
        }
        foreach (var extra in type.PropertyTypes.Where(p => !aliases.Contains(p.Alias)).Select(p => p.Alias).ToArray())
        {
            type.RemovePropertyType(extra);
            change.Notes.Add($"Removed the property '{extra}' from {type.Alias}, which the package does not have; its values are gone.");
        }
        foreach (var group in type.PropertyGroups.Where(g => !tabs.Any(tab => Value(tab, "Alias") == g.Alias)).Select(g => g.Alias).ToArray())
            type.RemovePropertyGroup(group);
    }

    private async Task<SchemaChange> RemoveAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateCoreScope();
        var change = new Change();
        var declaredTypes = Definitions("ContentTypes").Select(Key).ToHashSet();
        var declaredData = Definitions("DataTypes").Select(Key).ToHashSet();
        var installedTypes = declaredTypes.Select(contentTypes.Get).OfType<IContentType>().ToArray();
        var installedData = new List<IDataType>();
        foreach (var key in declaredData) if (await dataTypes.GetAsync(key) is { } dataType) installedData.Add(dataType);

        // Deleting a type deletes its content, and deleting a data type deletes every property using it along with its values.
        var problems = new List<string>();
        foreach (var type in installedTypes)
            if (contentTypes.HasContentNodes(type.Id)) problems.Add($"Content of the type '{type.Name}' ({type.Alias}) exists. Delete it first, from the recycle bin too.");
        var others = contentTypes.GetAll().Where(t => !declaredTypes.Contains(t.Key)).Cast<IContentTypeComposition>()
            .Concat(mediaTypes.GetAll()).Concat(memberTypes.GetAll());
        foreach (var other in others)
            foreach (var property in other.PropertyTypes.Where(p => declaredData.Contains(p.DataTypeKey)))
                problems.Add($"The site's own type '{other.Name}' ({other.Alias}) uses the data type '{installedData.FirstOrDefault(d => d.Key == property.DataTypeKey)?.Name}' on its property '{property.Alias}'. Remove that property first.");
        if (problems.Count > 0) throw new SchemaInUseException(problems);

        foreach (var type in installedTypes)
        {
            ct.ThrowIfCancellationRequested();
            var status = await contentTypes.DeleteAsync(type.Key, Constants.Security.SuperUserKey);
            if (status != ContentTypeOperationStatus.Success) throw new InvalidOperationException($"AppBlocks could not delete content type '{type.Alias}': {status}.");
            change.Deleted++;
        }
        foreach (var dataType in installedData)
        {
            ct.ThrowIfCancellationRequested();
            var result = await dataTypes.DeleteAsync(dataType.Key, Constants.Security.SuperUserKey);
            if (!result.Success) throw new InvalidOperationException($"AppBlocks could not delete data type '{dataType.Name}': {result.Status}.");
            change.Deleted++;
        }
        // The folders the schema names, deepest first, when nothing is left in them; one a site filled stays.
        var remainingData = (await dataTypes.GetAllAsync()).Select(t => t.ParentId).ToHashSet();
        await RemoveFoldersAsync(contentFolders, Definitions("ContentTypes"), id => contentTypes.GetAll().Any(t => t.ParentId == id), change);
        await RemoveFoldersAsync(dataFolders, Definitions("DataTypes"), remainingData.Contains, change);
        keyValues.SetValue(UninstalledKey, "true");
        scope.Complete();
        return change.Result();
    }

    private static async Task RemoveFoldersAsync<T>(IEntityTypeContainerService<T> folders, IEnumerable<XElement> definitions, Func<int, bool> hasTypes, Change change) where T : ITreeEntity
    {
        var paths = definitions.Select(d => Value(d.Element("Info")!, "Folder")).Distinct().OrderByDescending(path => path.Count(c => c == '/')).ToArray();
        foreach (var path in paths)
        {
            var folder = await FindFolderAsync(folders, path);
            if (folder is null || hasTypes(folder.Id) || (await folders.GetAllAsync()).Any(f => f.ParentId == folder.Id)) continue;
            var result = await folders.DeleteAsync(folder.Key, Constants.Security.SuperUserKey);
            if (result.Success) change.Deleted++;
        }
    }

    private IEnumerable<XElement> Definitions(string kind) => schemas.DistinctBy(s => s.Assembly).SelectMany(schema => schema.Definitions(kind));

    /// <summary>Creates the data type a schema definition declares, in its folder, inside the caller's scope; a migration step that adds one uses it too.</summary>
    internal static async Task CreateDataTypeAsync(XElement definition, IDataTypeService dataTypes, IDataTypeContainerService folders,
        PropertyEditorCollection editors, IConfigurationEditorJsonSerializer serializer)
    {
        var (info, editor, storage, configuration) = Editor(definition, editors, serializer);
        var type = new DataType(editor, serializer, await FolderAsync(folders, Value(info, "Folder")))
        {
            Key = Key(definition), Name = Value(info, "Name"), EditorUiAlias = Value(info, "EditorUIAlias"), DatabaseType = storage, ConfigurationData = configuration,
        };
        var result = await dataTypes.CreateAsync(type, Constants.Security.SuperUserKey);
        if (!result.Success) throw new InvalidOperationException($"AppBlocks could not install data type '{type.Name}' ({type.Key}): {result.Status}.");
    }

    /// <summary>A data type definition's editor, and its storage and settings as that editor keeps them.</summary>
    private static (XElement Info, IDataEditor Editor, ValueStorageType Storage, IDictionary<string, object> Configuration) Editor(XElement definition,
        PropertyEditorCollection editors, IConfigurationEditorJsonSerializer serializer)
    {
        var info = definition.Element("Info")!;
        var editorAlias = Value(info, "EditorAlias");
        var editor = editors.FirstOrDefault(e => e.Alias == editorAlias)
            ?? throw new InvalidOperationException($"AppBlocks data type '{Value(info, "Name")}' requires missing editor '{editorAlias}'.");
        // Stored as the editor stores it, as a site's own data types are; the type's default is long text whatever the editor.
        return (info, editor, ValueTypes.ToStorageType(editor.GetValueEditor().ValueType), editor.GetConfigurationEditor().FromDatabase(Value(definition, "Config"), serializer));
    }

    private async Task<IContentType> ContentTypeAsync(XElement definition)
    {
        var info = Info(definition);
        var type = new ContentType(strings, await FolderAsync(contentFolders, Value(info, "Folder")))
        {
            Key = Key(definition),
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
            HistoryCleanup = Cleanup(info),
        };
        foreach (var tab in definition.Element("Tabs")!.Elements("Tab"))
            type.PropertyGroups.Add(SchemaProperties.Group(tab));
        foreach (var property in definition.Element("GenericProperties")!.Elements("GenericProperty"))
            SchemaProperties.Add(type, await properties.PropertyAsync(type.Alias, property), (string?)property.Element("Tab")?.Attribute("Alias"));
        return type;
    }

    /// <summary>The definition's Info, checked: the package catalogue contains standalone types and no site templates or compositions.</summary>
    private static XElement Info(XElement definition)
    {
        var info = definition.Element("Info")!;
        // Reject unsupported definitions instead of silently installing an incomplete type.
        if (info.Element("Compositions")!.HasElements || info.Element("AllowedTemplates")!.HasElements || Value(info, "DefaultTemplate").Length > 0)
            throw new InvalidOperationException($"AppBlocks schema '{definition.Attribute("Alias")!.Value}' must not declare site templates or compositions.");
        return info;
    }

    private static Umbraco.Cms.Core.Models.ContentEditing.HistoryCleanup Cleanup(XElement info)
        => info.Element("HistoryCleanup") is { } cleanup
            ? new Umbraco.Cms.Core.Models.ContentEditing.HistoryCleanup
            {
                PreventCleanup = (bool)cleanup.Element("PreventCleanup")!,
                KeepAllVersionsNewerThanDays = OptionalInt(cleanup, "KeepAllVersionsNewerThanDays"),
                KeepLatestVersionPerDayForDays = OptionalInt(cleanup, "KeepLatestVersionPerDayForDays"),
            }
            : new Umbraco.Cms.Core.Models.ContentEditing.HistoryCleanup();

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

    private static async Task<EntityContainer?> FindFolderAsync<T>(IEntityTypeContainerService<T> folders, string path) where T : ITreeEntity
    {
        EntityContainer? parent = null;
        var level = 1;
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = WebUtility.UrlDecode(segment);
            parent = (await folders.GetAsync(name, level++)).FirstOrDefault(f => f.ParentId == (parent?.Id ?? -1));
            if (parent is null) return null;
        }
        return parent;
    }

    private static string Version(Assembly assembly)
        => (assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "").Split('+')[0];

    private static Guid Key(XElement definition) => Guid.Parse(definition.Attribute("Key")!.Value);
    private static string Value(XElement element, string name) => SchemaProperties.Value(element, name);
    private static int? OptionalInt(XElement element, string name) => Value(element, name) is { Length: > 0 } value ? int.Parse(value) : null;

    private sealed class Change
    {
        public int Created;
        public int Updated;
        public int Deleted;
        public List<string> Notes { get; } = [];
        public SchemaChange Result() => new(Created, Updated, Deleted, Notes);
    }
}
