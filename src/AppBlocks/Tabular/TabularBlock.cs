using System.Globalization;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Extensions;
using AppBlocks.Forms;

namespace AppBlocks.Tabular;

/// <summary>
/// What an editor sets on a Tabular form block: the child data source and the field of it that holds the parent's
/// key, which fields to show as columns, whose record the rows belong to (the enclosing form's unless an owner is
/// named, as for Attachments), what the block offers and its labels.
/// </summary>
/// <param name="Fields">The field aliases shown as columns, in order; empty shows every field but the link field.</param>
/// <param name="HideAdd">The block offers no new rows, whatever the data source allows.</param>
/// <param name="HideEdit">The block changes no row that is already saved, whatever the data source allows; a new row is still filled in.</param>
/// <param name="HideDelete">The block offers no deletions, whatever the data source allows.</param>
/// <param name="ReadOnly">The block shows rows and writes nothing.</param>
/// <param name="MaxRows">The most rows the block lists for one record.</param>
public sealed record TabularBlock(string? Heading, string? Help, string DataSource, string LinkField, IReadOnlyList<string> Fields,
    string? OwnerResource, string? OwnerKey, bool HideAdd, bool HideEdit, bool HideDelete, bool ReadOnly, int MaxRows, string? EmptyMessage, string AddLabel, string SaveLabel)
{
    public const string HeadingAlias = "heading", HelpAlias = "helpText", DataSourceAlias = "dataSource", LinkFieldAlias = "linkField", FieldsAlias = "fields",
        OwnerResourceAlias = "ownerResource", OwnerKeyAlias = "ownerKey", HideAddAlias = "hideAdd", HideEditAlias = "hideEdit", HideDeleteAlias = "hideDelete",
        ReadOnlyAlias = "readOnly", MaxRowsAlias = "maxRows", EmptyMessageAlias = "emptyMessage", AddLabelAlias = "addLabel", SaveLabelAlias = "saveLabel";
    public const int DefaultMaxRows = 200;
    /// <summary>The default of <see cref="TabularOptions.MaxRows"/>, which callers without the options pass on.</summary>
    public const int MaxRowsLimit = 1000;
    public const string DefaultAddLabel = "Add row", DefaultSaveLabel = "Save rows";

    /// <summary>The rows belong to the record of the form the block sits in.</summary>
    public bool OwnedByForm => string.IsNullOrWhiteSpace(OwnerResource);

    /// <param name="maxRowsLimit">The site's <see cref="TabularOptions.MaxRows"/>, which the block's row limit is cut to.</param>
    public static TabularBlock Read(IPublishedElement block, int maxRowsLimit = MaxRowsLimit) => Read(alias => alias switch {
        MaxRowsAlias => block.Value<int?>(alias)?.ToString(CultureInfo.InvariantCulture),
        HideAddAlias or HideEditAlias or HideDeleteAlias or ReadOnlyAlias => block.Value<bool>(alias) ? "true" : "false",
        _ => block.Value<string>(alias),
    }, maxRowsLimit);

    /// <summary>Reads the settings from their text values, as publishing sees them.</summary>
    public static TabularBlock Read(Func<string, string?> text, int maxRowsLimit = MaxRowsLimit)
    {
        string? Clean(string alias) => text(alias) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        bool Flag(string alias) => Clean(alias) is { } value && (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1");
        var limit = Math.Max(1, maxRowsLimit);
        var rows = int.TryParse(Clean(MaxRowsAlias), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? Math.Min(parsed, limit) : Math.Min(DefaultMaxRows, limit);
        return new(Clean(HeadingAlias), Clean(HelpAlias), Clean(DataSourceAlias) ?? "", Clean(LinkFieldAlias) ?? "", ParseFields(Clean(FieldsAlias)),
            Clean(OwnerResourceAlias), Clean(OwnerKeyAlias), Flag(HideAddAlias), Flag(HideEditAlias), Flag(HideDeleteAlias), Flag(ReadOnlyAlias), rows,
            Clean(EmptyMessageAlias), Clean(AddLabelAlias) ?? DefaultAddLabel, Clean(SaveLabelAlias) ?? DefaultSaveLabel);
    }

    /// <summary>Field aliases separated by commas, semicolons or whitespace, each once, in the order written.</summary>
    public static IReadOnlyList<string> ParseFields(string? text)
        => (text ?? "").Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>The columns the block shows, in order: the named fields that the data source has, or every field but the link field.</summary>
    public IReadOnlyList<FormField> Columns(IReadOnlyList<FormField> fields) => Fields.Count == 0
        ? fields.Where(f => !f.Key.Equals(LinkField, StringComparison.OrdinalIgnoreCase)).ToArray()
        : Fields.Select(name => fields.FirstOrDefault(f => f.Key.Equals(name, StringComparison.OrdinalIgnoreCase))).OfType<FormField>().ToArray();
}
