using Umbraco.Cms.Core.Models.Blocks;
using System.Text.Json;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using AppBlocks.Blocks;
using AppBlocks.Forms;

namespace AppBlocks.Samples;

public sealed record DiscoveredForm(Guid PageKey, Guid FormKey, string? Culture, string PageName, string Name,
    string Resource, bool Published, IReadOnlyList<FormField> Fields);
public sealed record FormInventoryResult(IReadOnlyList<DiscoveredForm> Forms, IReadOnlyList<string> Problems);

/// <summary>Reads saved document values, including drafts and variants, without requiring publication.</summary>
public sealed class FormInventory(IContentService content, FormResourceRegistry resources, IContentTypeService? types = null)
{
    public FormInventoryResult Read(Guid? pageKey = null)
    {
        var forms = new List<DiscoveredForm>(); var problems = new List<string>();
        foreach (var page in Pages(pageKey))
        {
            if (page.Trashed) continue;
            foreach (var property in page.Properties.Where(p => AppBlockSchema.IsBlockEditor(p.PropertyType.PropertyEditorAlias)))
            foreach (var value in property.Values)
            {
                var json = Convert.ToString(value.EditedValue ?? value.PublishedValue);
                if (string.IsNullOrWhiteSpace(json)) continue;
                try { forms.AddRange(Parse(page.Key, page.Name ?? "Untitled", page.Published, value.Culture, json, resources, types)); }
                catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
                { problems.Add($"{page.Name}: the saved blocks could not be read. Open this page and check its form configuration."); }
            }
        }
        return new(forms.OrderBy(f => f.PageName).ThenBy(f => f.Name).ToArray(), problems);
    }

    private IEnumerable<IContent> Pages(Guid? key)
    {
        if (key is { } selected) { if (content.GetById(selected) is { } page) yield return page; yield break; }
        for (var pageIndex = 0; ; pageIndex++)
        {
            var batch = content.GetPagedDescendants(-1, pageIndex, 100, out var total).ToArray();
            foreach (var page in batch) yield return page;
            if ((pageIndex + 1L) * 100 >= total || batch.Length == 0) break;
        }
    }

    internal static IReadOnlyList<DiscoveredForm> Parse(Guid page, string pageName, bool published, string? culture, string json,
        FormResourceRegistry? resources = null, IContentTypeService? types = null)
    {
        var result = new List<DiscoveredForm>();
        foreach (var set in BlockValueSets.Read(json, types))
        {
        var data = set.ContentData.ToDictionary(d => d.Key);
        var layouts = BlockValueSets.Layout(set);
        foreach (var form in data.Values.Where(d => d.ContentTypeKey == AppBlockSchema.Key("appForm")))
        {
            var formKey = form.Key;
            var resourceAlias = "";
            var interfaceJson = Text(form, "formInterface");
            if (interfaceJson.Length > 0)
            {
                var first = BlockValueSets.Parse(interfaceJson).ContentData.FirstOrDefault();
                if (first is not null) resourceAlias = Text(first, "resource");
            }
            IReadOnlyList<FormField> contract;
            try { contract = resources?.RegisteredResources.GetValueOrDefault(resourceAlias)?.Fields ?? []; }
            catch (Exception error) when (error is not OperationCanceledException) { contract = []; }
            var fieldKeys = FindChildren(layouts, formKey);
            var fields = new List<FormField>();
            foreach (var key in fieldKeys)
            {
                if (!data.TryGetValue(key, out var field)) continue;
                var typeKey = field.ContentTypeKey;
                var kind = AppBlockSchema.FieldAliases.FirstOrDefault(alias => AppBlockSchema.Key(alias) == typeKey);
                if (kind is null) continue;
                var binding = Text(field, "field");
                if (binding.Length == 0) binding = "field" + (fields.Count + 1);
                var known = contract.FirstOrDefault(f => f.Key == binding);
                var label = Text(field, "label");
                var required = Text(field, "required") is "true" or "True" or "1";
                var readOnly = kind == "appDisplay" || Text(field, "readOnly") is "true" or "True" or "1";
                fields.Add(known is not null ? known with { Required = known.Required || required, ReadOnly = known.ReadOnly || readOnly }
                    : new(binding, label.Length > 0 ? label : binding, kind switch
                {
                    "appNumber" => "number", "appCheckbox" => "checkbox", "appDate" => "date", "appTime" => "time",
                    "appDateTime" => "datetime", _ => "text",
                }, Required: required, ReadOnly: readOnly));
            }
            var name = Text(form, "heading");
            result.Add(new(page, formKey, culture, pageName, name.Length > 0 ? name : "Form", resourceAlias, published,
                fields.DistinctBy(f => f.Key).ToArray()));
        }
        }
        return result;
    }

    private static IReadOnlyList<Guid> FindChildren(IEnumerable<IBlockLayoutItem> layout, Guid key)
    {
        var found = new List<Guid>();
        void Visit(IBlockLayoutItem node, bool inside)
        {
            inside |= node.ContentKey == key;
            if (inside && node.ContentKey != key) found.Add(node.ContentKey);
            foreach (var child in BlockValueSets.Children(node)) Visit(child, inside);
        }
        foreach (var item in layout) Visit(item, false);
        return found;
    }

    internal static string Text(BlockItemData block, string alias) => BlockValueSets.Text(block, alias);
}
