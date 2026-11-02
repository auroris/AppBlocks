using Umbraco.Cms.Core.Models.Blocks;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;
using AppBlocks.AntiSpam;
using AppBlocks.Attachments;
using AppBlocks.DataSources;
using AppBlocks.Forms;
using AppBlocks.DocumentOutput;
using AppBlocks.Schedules;
using AppBlocks.Tabular;

namespace AppBlocks.Blocks;

/// <summary>
/// Validates authored structure without executing a report or invoking a form operation. Forms use their registered
/// C# contracts; tabular forms and record owners may use published data sources and their database schemas.
/// </summary>
public sealed class AppBlocksValidator(FormResourceRegistry resources, PublishedDataCatalog? definitions = null, IContentService? contents = null,
    IOptions<AttachmentSettings>? attachments = null, ScheduleSources? schedules = null,
    IContentTypeService? contentTypes = null, IEnumerable<IAppBlockPlugin>? plugins = null, AntiSpamGate? antiSpam = null) : INotificationAsyncHandler<ContentPublishingNotification>
{
    private static readonly string[] Aliases = ["appForm", "appFormInterface", "appFieldset", "appActions", AppBlockSchema.AttachmentsAlias, AppBlockSchema.AntiSpamAlias,
        AppBlockSchema.ScheduleAlias, AppBlockSchema.TabularAlias, ..AppBlockSchema.LayoutAliases, ..AppBlockSchema.ContentAliases, ..AppBlockSchema.FieldAliases];
    /// <summary>Loads the published reference data before synchronous authored-value validation.</summary>
    public async Task LoadAsync()
    {
        await resources.LoadAsync();
        if (definitions is not null) await definitions.LoadAsync();
        if (schedules is not null) await schedules.LoadAsync();
    }

    public async Task HandleAsync(ContentPublishingNotification notification, CancellationToken cancellationToken)
    {
        if (!notification.PublishedEntities.Any(AppBlockSchema.HasBlocks)) return;
        await LoadAsync();
        foreach (var page in notification.PublishedEntities.Where(AppBlockSchema.HasBlocks)) {
            foreach (var property in page.Properties.Where(p => AppBlockSchema.IsBlockEditor(p.PropertyType.PropertyEditorAlias))) {
            IEnumerable<string?> cultures = [null];
            if (property.PropertyType.VariesByCulture()) cultures = page.AvailableCultures.Where(culture => notification.IsPublishingCulture(page, culture));
            foreach (var culture in cultures) {
                var json = page.GetValue(property.Alias, culture)?.ToString();
                if (string.IsNullOrWhiteSpace(json)) continue;
                try {
                    var sets = BlockValueSets.Read(json, contentTypes);
                    foreach (var set in sets) {
                        var warnings = new List<string>();
                        var errors = Validate(set, website: true, embedded: true, warnings);
                        if (errors.Count > 0) notification.CancelOperation(new EventMessage("AppBlocks", page.Name + ": " + string.Join(" ", errors), EventMessageType.Error));
                        foreach (var warning in warnings.Distinct()) notification.Messages.Add(new EventMessage("AppBlocks", page.Name + ": " + warning, EventMessageType.Warning));
                    }
                } catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException) {
                    notification.CancelOperation(new EventMessage("AppBlocks", page.Name + ": Check the saved block configuration.", EventMessageType.Error));
                }
            }
            }
        }
    }

    public IReadOnlyList<string> Validate(string? json) => Validate(json, website: false);

    /// <summary>Content and layouts may stand alone; form fields and actions still belong to a form.</summary>
    public IReadOnlyList<string> ValidateWebsite(string? json) => Validate(json, website: true);
    public IReadOnlyList<string> ValidateEmbedded(string? json) => Validate(json, website: true, embedded: true);

    private IReadOnlyList<string> Validate(string? json, bool website, bool embedded = false, List<string>? warnings = null)
    {
        if (website && string.IsNullOrWhiteSpace(json)) return [];
        try { return Validate(BlockValueSets.Parse(json ?? "{}"), website, embedded, warnings); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        { return ["The block structure is invalid: " + error.Message]; }
    }

    private IReadOnlyList<string> Validate(BlockValue value, bool website, bool embedded, List<string>? warnings)
    {
        var errors = new List<string>();
        try {
            var data = value.ContentData.ToDictionary(d => d.Key);
            if (!website && data.Count > 500) return ["Use at most 500 blocks on a page."];
            var layout = BlockValueSets.Layout(value).ToArray();
            if (!website && layout.Length == 0) return ["Add a form or a report."];
            var visited = new HashSet<Guid>(); var forms = 0;
            foreach (var item in layout) {
                if (website) { VisitContent(item); continue; }
                var block = data[item.ContentKey];
                var alias = Alias(block);
                if (Plugin(alias) is { } plugin) { visited.Add(item.ContentKey); plugin.Validate(BlockValueSets.Element(block), errors); }
                else if (alias == AppBlockSchema.ScheduleAlias) { visited.Add(item.ContentKey); Schedule(block, errors); }
                // A tabular form outside a form must name its owner; Tabular says so when it does not.
                else if (alias == AppBlockSchema.TabularAlias) { visited.Add(item.ContentKey); Tabular(block, null, errors); }
                else if (alias == "appForm") { forms++; Form(item, block, data, visited, errors, warnings: warnings); }
                // Layouts, content blocks and an Attachments block that names its owner stand on the page as they do on a
                // website page, with whatever their areas hold; fields and actions still belong to a form, and VisitContent says so.
                else VisitContent(item);
            }
            // An application page's runtime shares one record key between its forms; website pages may mix independent forms.
            if (!website && forms > 1) errors.Add("Use one form per page.");
            if (visited.Count != data.Count) errors.Add("The page contains unused or duplicated blocks. Remove them before publishing.");

            void VisitContent(IBlockLayoutItem item) {
                var key = item.ContentKey;
                var block = data[key]; var alias = Alias(block);
                if (alias == "appForm") { forms++; Form(item, block, data, visited, errors, limitNesting: false, warnings: warnings); return; }
                if (!visited.Add(key)) throw new InvalidOperationException("Blocks cannot be repeated or cyclic.");
                if (Plugin(alias) is { } plugin) plugin.Validate(BlockValueSets.Element(block), errors);
                else if (alias == AppBlockSchema.AttachmentsAlias) Attachments(block, null, errors);
                else if (alias == AppBlockSchema.ScheduleAlias) Schedule(block, errors);
                else if (alias == AppBlockSchema.TabularAlias) Tabular(block, null, errors);
                else if (alias == AppBlockSchema.AntiSpamAlias) errors.Add("Place the Anti-spam block inside the form it protects.");
                else if (alias == "appActions" || AppBlockSchema.FieldAliases.Contains(alias)) errors.Add("Fields and form actions must be placed inside a form.");
                else if (!embedded && alias != "appFieldset" && !AppBlockSchema.LayoutAliases.Contains(alias) && !AppBlockSchema.ContentAliases.Contains(alias)) errors.Add("The page contains an unsupported block.");
                foreach (var nested in BlockValueSets.Children(item)) VisitContent(nested);
            }
        } catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException) {
            errors.Add("The block structure or starting view is invalid: " + error.Message);
        }
        return errors;
    }

    /// <summary>Print opens /api/paper-reports/{key}, which only a Paper report node answers. A node not yet imported is not judged.</summary>
    private void PaperReport(string picked, List<string> errors)
    {
        if (contents is null || PaperReportLinks.PickedKey(picked) is not { } key || contents.GetById(key) is not { } node) return;
        if (node.ContentType.Alias != "paperReport") errors.Add($"Select a Paper report node for Paper report; {node.Name} is not one.");
    }

    private void Form(IBlockLayoutItem layout, BlockItemData block, Dictionary<Guid, BlockItemData> data, HashSet<Guid> visited, List<string> errors, bool limitNesting = true, List<string>? warnings = null)
    {
        if (Text(block, "paperReport") is { Length: > 0 } paper) PaperReport(paper, errors);
        var definition = BlockValueSets.Parse(Text(block, "formInterface"));
        if (definition is not SingleBlockValue) { errors.Add("Recreate the Form Interface using the Single Block editor."); return; }
        var interfaces = definition.ContentData.ToArray();
        var interfaceLayout = BlockValueSets.Layout(definition).ToArray();
        if (interfaces.Length != 1 || Alias(interfaces[0]) != "appFormInterface" || interfaceLayout.Length != 1
            || interfaceLayout[0].ContentKey != interfaces[0].Key) { errors.Add("Each form needs exactly one Form Interface."); return; }
        var configuration = interfaces[0];
        var resource = resources.RegisteredResources.GetValueOrDefault(Text(configuration, "resource"));
        if (resource is null) { errors.Add("Select a registered C# form class."); return; }
        // A data source's fields come from its database; without them every binding would look wrong.
        if (resource is SqlDataSource { Problem: { } problem }) { errors.Add($"The data source {resource.Alias} is not available: {problem}"); return; }
        var fields = new Dictionary<string, BlockItemData>(StringComparer.Ordinal); var actions = 0; var checks = 0;
        void Visit(IBlockLayoutItem item, int depth) {
            if (limitNesting && depth > 16) throw new InvalidOperationException("Form nesting is too deep.");
            var key = item.ContentKey;
            if (!visited.Add(key)) throw new InvalidOperationException("Blocks cannot be repeated or cyclic.");
            var child = data[key]; var alias = Alias(child);
            if (depth > 0 && (alias == "appForm" || Plugin(alias)?.RequiresUser == true)) errors.Add("Forms and reports cannot be nested inside a form.");
            if (alias == "appActions") actions++;
            if (alias == AppBlockSchema.AntiSpamAlias) { checks++; AntiSpam(child, errors, warnings); }
            else if (alias == AppBlockSchema.AttachmentsAlias) Attachments(child, resource, errors);
            else if (alias == AppBlockSchema.ScheduleAlias) Schedule(child, errors);
            else if (alias == AppBlockSchema.TabularAlias) Tabular(child, resource, errors);
            else if (AppBlockSchema.FieldAliases.Contains(alias)) {
                var binding = Text(child, "field");
                if (!fields.TryAdd(binding, child)) errors.Add($"Field '{binding}' appears more than once.");
                var field = resource.Fields.FirstOrDefault(f => f.Key == binding);
                if (field is null) errors.Add($"'{binding}' is not a field of {resource.Alias}.");
                else if (!Compatible(alias, field, Text(child, "listOfValues").Length > 0)) errors.Add($"The block for '{binding}' does not match its {field.Kind} type.");
                if (definitions is not null && alias is "appSelect" or AppBlockSchema.ShuttleAlias && Text(child, "listOfValues") is { Length: > 0 } selected && definitions.FindList(selected) is null)
                    errors.Add($"Select a published list of values for '{binding}'.");
            }
            else if (alias is not ("appForm" or "appFieldset" or "appActions") && !AppBlockSchema.LayoutAliases.Contains(alias) && !AppBlockSchema.ContentAliases.Contains(alias)) errors.Add("The form contains an unsupported block.");
            foreach (var nested in BlockValueSets.Children(item)) Visit(nested, depth + 1);
        }
        Visit(layout, 0);
        if (actions > 1) errors.Add("Use one Form actions block per form.");
        if (checks > 1) errors.Add("Use one Anti-spam block per form.");
        if (!Flag(configuration, "viewOnly")) foreach (var field in resource.Fields.Where(f => f.Required && !f.ReadOnly && !f.HasServerDefault)) {
            if (!fields.TryGetValue(field.Key, out var item) || Flag(item, "readOnly") || Alias(item) == "appDisplay") errors.Add($"Required field '{field.Key}' must be editable, or the form must be read only.");
        }
    }

    /// <summary>
    /// An Attachments block names whose files it shows, or sits in a form whose record owns them. Its limits may narrow
    /// the site's and never widen them; a block that asks for more than the site allows is refused rather than quietly capped.
    /// </summary>
    private void Attachments(BlockItemData block, IFormResource? form, List<string> errors)
    {
        Owner(block, form, AttachmentsOwner, AttachmentBlock.OwnerResourceAlias, AttachmentBlock.OwnerKeyAlias, errors);
        var settings = attachments?.Value ?? new AttachmentSettings();
        if (Text(block, AttachmentBlock.MaxSizeAlias) is { Length: > 0 } size)
        {
            if (!long.TryParse(size, out var megabytes) || megabytes < 1) errors.Add("The largest file must be a whole number of megabytes, at least 1.");
            else if (megabytes * 1024 * 1024 > settings.MaxFileSize) errors.Add($"The largest file cannot exceed the site's limit of {settings.MaxFileSize / (1024 * 1024)} MB.");
        }
        var extensions = AttachmentLimits.Parse(Text(block, AttachmentBlock.AllowedTypesAlias));
        foreach (var extension in extensions)
        {
            var normalized = (extension.StartsWith('.') ? extension : "." + extension).ToLowerInvariant();
            if (normalized.Length < 2 || normalized.Skip(1).Any(c => !char.IsAsciiLetterOrDigit(c))) errors.Add($"'{extension}' is not a file extension; write them as .pdf .png.");
            else if (settings.AllowedExtensions.Count > 0 && !settings.AllowedExtensions.Any(allowed => (allowed.StartsWith('.') ? allowed : "." + allowed).Equals(normalized, StringComparison.OrdinalIgnoreCase)))
                errors.Add($"The site does not accept {normalized} files (Attachments:AllowedExtensions).");
        }
    }

    /// <summary>
    /// An Anti-spam block names a service this site offers and the public site key that goes with it. A secret key missing
    /// from the server is only a warning: it is an administrator's to add, and a site's content may well arrive before its keys.
    /// Without the service list, as in a test without a site, the block's own settings are all that is judged.
    /// </summary>
    private void AntiSpam(BlockItemData block, List<string> errors, List<string>? warnings)
    {
        var alias = Text(block, "service").Trim();
        var siteKey = Text(block, "siteKey").Trim();
        if (alias.Length == 0) { errors.Add("Choose a service for the Anti-spam block."); return; }
        if (Text(block, "scoreThreshold").Trim() is { Length: > 0 } threshold && AntiSpamBlock.Threshold(threshold) is null) errors.Add("The Anti-spam block's score threshold must be a number from 0 to 1, such as 0.5.");
        if (siteKey.Length > 0 && !AntiSpamBlock.IsSiteKey(siteKey)) errors.Add("A site key holds letters, digits, hyphens, underscores, dots and colons.");
        if (siteKey.Length > 0 && antiSpam?.IsSecretKey(siteKey) == true) errors.Add("The Anti-spam block's site key is one of this site's secret keys, which must never be in a block. Enter the public site key.");
        if (alias.Equals(AntiSpamBlock.Honeypot, StringComparison.OrdinalIgnoreCase) || antiSpam is null) return;
        if (antiSpam.Find(alias) is not { } service) { errors.Add($"'{alias}' is not an anti-spam service this site offers."); return; }
        if (siteKey.Length == 0 && antiSpam.SiteKey(new(Guid.Empty, service.Alias, null, AntiSpamBlock.DefaultScoreThreshold, null, null)) is null)
            errors.Add($"Enter the site key for {service.Name}, or set one in Settings, AppBlocks, Anti-spam.");
        if (antiSpam.SecretState(service.Alias) is not (AntiSpamSecretState.Configuration or AntiSpamSecretState.Saved))
            warnings?.Add($"{service.Name} has no secret key yet, so this form refuses every submission until an administrator adds it in Settings, AppBlocks, Anti-spam.");
    }

    /// <summary>The words an owner rule uses: the block's name with its article, what it holds, and what an owner owns.</summary>
    private sealed record OwnerWords(string Name, string Article, string Things, string Owned);

    private static readonly OwnerWords AttachmentsOwner = new("Attachments", "An", "files", "attachments");
    private static readonly OwnerWords TabularOwner = new("Tabular form", "A", "rows", "rows");

    /// <summary>
    /// The owner rules of a block that follows a record, such as Attachments or a tabular form: inside a form it may
    /// leave the owner to the form's record; otherwise it names a registered resource and a key the request can supply.
    /// </summary>
    private void Owner(BlockItemData block, IFormResource? form, OwnerWords words, string resourceAlias, string keyAlias, List<string> errors)
    {
        var owner = Text(block, resourceAlias);
        var key = Text(block, keyAlias);
        if (owner.Length == 0)
        {
            if (form is null) errors.Add($"Place the {words.Name} block inside a form, or select the resource that owns the {words.Things}.");
            if (key.Length > 0) errors.Add($"Leave the {words.Name} block's owner key empty when the {words.Things} belong to the form's record, or select the owner resource.");
        }
        else
        {
            if (resources.Find(owner) is null) errors.Add($"'{owner}' is not a registered form resource or a published data source, so it cannot own {words.Owned}.");
            if (key.Length == 0) errors.Add($"Give the {words.Name} block an owner key: :PARAM.name, :FIELD.alias or a literal key.");
            else if (key.StartsWith(":FIELD.", StringComparison.OrdinalIgnoreCase))
            {
                if (form is null) errors.Add($"{words.Article} {words.Name} block outside a form has no record to take :FIELD values from; use :PARAM.name or place it in a form.");
                else if (!form.Fields.Any(f => f.Key.Equals(key[7..], StringComparison.OrdinalIgnoreCase))) errors.Add($"'{key[7..]}' is not a field of {form.Alias}, so the {words.Name} block cannot take its owner key from it.");
            }
            else if (key.StartsWith(":PARAM.", StringComparison.OrdinalIgnoreCase) && key.Length == 7) errors.Add("Name the page parameter after :PARAM.");
        }
    }

    /// <summary>
    /// A tabular form edits the rows of a published data source that belong to one record, so the data source must be
    /// one the engine writes, the link field one it can set on every new row, and every column a field of it. A new
    /// row must be completable from the columns, the link and the defaults, unless the block never adds rows. Without
    /// a catalogue, as in a test without content, a data source the registry does not know is not judged.
    /// </summary>
    private void Tabular(BlockItemData block, IFormResource? form, List<string> errors)
    {
        Owner(block, form, TabularOwner, TabularBlock.OwnerResourceAlias, TabularBlock.OwnerKeyAlias, errors);
        if (Text(block, TabularBlock.MaxRowsAlias) is { Length: > 0 } rows
            && (!int.TryParse(rows, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var limit) || limit < 1 || limit > TabularBlock.MaxRowsLimit))
            errors.Add($"The tabular form's row limit must be a whole number from 1 to {TabularBlock.MaxRowsLimit}.");
        var settings = TabularBlock.Read(alias => Text(block, alias));
        if (settings.DataSource.Length == 0) { errors.Add("Select a published data source for the tabular form."); return; }
        var resource = resources.Find(settings.DataSource);
        if (resource is null && definitions is null) return;
        if (resource is null) { errors.Add($"'{settings.DataSource}' is not a published data source, so the tabular form cannot edit its rows."); return; }
        if (resource is not SqlDataSource source) { errors.Add("Select a published data source for the tabular form; the rows of a C# resource cannot be edited as a table."); return; }
        // Its fields come from its database; without them every name would look wrong.
        if (source.Problem is { } problem) { errors.Add($"The data source {source.Alias} is not available: {problem}"); return; }
        var fields = source.Fields;
        FormField? Field(string name) => fields.FirstOrDefault(f => f.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
        var link = settings.LinkField.Length == 0 ? null : Field(settings.LinkField);
        if (settings.LinkField.Length == 0) errors.Add($"Name the tabular form's link field: the field of {source.Alias} that holds the record's key.");
        else if (link is null) errors.Add($"'{settings.LinkField}' is not a field of {source.Alias}, so it cannot link rows to their record.");
        else if (link.ReadOnly) errors.Add("The link field must be a field the data source writes; keys the database assigns and read-only fields cannot link rows to their record.");
        foreach (var name in settings.Fields)
        {
            if (Field(name) is null) errors.Add($"'{name}' is not a field of {source.Alias}, so the tabular form cannot show it.");
            else if (name.Equals(settings.LinkField, StringComparison.OrdinalIgnoreCase)) errors.Add("The link field cannot be a column of the tabular form; the server sets it on every row.");
        }
        if (settings.ReadOnly || settings.HideAdd) return;
        var shown = settings.Columns(fields).Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields.Where(f => f.Required && !f.ReadOnly && !f.HasServerDefault && !shown.Contains(f.Key) && f != link))
            errors.Add($"Required field '{field.Key}' of {source.Alias} must be a column of the tabular form, or have a default.");
    }

    /// <summary>
    /// A Schedule block names two sources that must exist and carry the columns the layout reads, which the catalogue
    /// knows from the sources' schemas. Without a catalogue, as in a test without content, only the settings are checked.
    /// </summary>
    private void Schedule(BlockItemData block, List<string> errors)
    {
        var settings = ScheduleSettings.Read(alias => Text(block, alias));
        errors.AddRange(ScheduleValidation.Validate(settings, schedules is null || definitions is null ? null : schedules.Schema, Text(block, ScheduleSettings.ColumnsAlias)));
    }

    public static bool Compatible(string alias, FormField field, bool hasListOfValues = false) => alias == "appDisplay" || alias switch {
        "appText" or "appTextArea" or AppBlockSchema.ColorAlias => field.Kind == "text",
        "appSelect" => hasListOfValues ? field.Kind is "text" or "number" : field.Kind == "text" && field.Choices is { Count: > 0 },
        AppBlockSchema.ShuttleAlias => field.Kind == "text" && (hasListOfValues || field.Choices is { Count: > 0 }),
        "appNumber" => field.Kind == "number", "appDate" => field.Kind == "date", "appTime" => field.Kind == "time",
        "appDateTime" => field.Kind == "datetime", "appCheckbox" => field.Kind == "checkbox", _ => false,
    };
    private IAppBlockPlugin? Plugin(string alias) => plugins?.FirstOrDefault(p => p.Alias == alias);
    private string Alias(BlockItemData block) => Aliases.Concat(plugins?.Select(p => p.Alias) ?? []).FirstOrDefault(a => AppBlockSchema.Key(a) == block.ContentTypeKey) ?? "";
    private static string Text(BlockItemData block, string alias) => BlockValueSets.Text(block, alias);
    private static bool Flag(BlockItemData block, string alias) => Text(block, alias) is "true" or "True" or "1";
}
