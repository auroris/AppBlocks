using System.Security.Cryptography;
using System.Text;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;

namespace AppBlocks.Blocks;

/// <summary>Stable identities shared by the embedded schema catalogue and block rendering.</summary>
public static class AppBlockSchema
{
    public const string PageAlias = "appBlockPage";
    public const string BodyAlias = "applicationBlocks";
    public const string FormsContainerAlias = "appFormsContainer";
    public static bool IsBlockEditor(string? alias) => alias is "Umbraco.BlockGrid" or "Umbraco.BlockList";

    /// <summary>The shared block property is a capability of a page, including composed/custom document types.</summary>
    public static bool HasBlocks(IPublishedContentType type) => type.PropertyTypes.Any(p => IsBlockEditor(p.EditorAlias));
    public static bool HasBlocks(IContentBase page) => page.Properties.Any(p => IsBlockEditor(p.PropertyType.PropertyEditorAlias));
    public static readonly string[] FieldAliases = ["appText", "appTextArea", "appNumber", "appDate", "appTime", "appDateTime", "appCheckbox", "appSelect", "appDisplay"];

    /// <summary>
    /// The column layouts. The grid data type never lets one into another's areas, so a page's columns
    /// nest one level deep and every theme can style them without guessing at depth.
    /// </summary>
    public static readonly string[] ColumnLayoutAliases = ["appFullWidth", "appColumns", "appThreeColumns"];

    /// <summary>APEX's region: an optional title bar over one area. It is content to a column layout, not a layout.</summary>
    public const string RegionAlias = "appRegion";

    /// <summary>Blocks that only arrange other blocks, so a form or a validator can pass over them to their contents.</summary>
    public static readonly string[] LayoutAliases = [.. ColumnLayoutAliases, RegionAlias];

    public const string RichTextAlias = "appRichText";
    public const string HtmlAlias = "appHtml";
    public const string CssAlias = "appCss";
    public const string ScriptAlias = "appScript";

    /// <summary>
    /// Blocks that carry content rather than data: rich text and raw HTML render where they sit, CSS and
    /// JavaScript once for the whole page, in its head and at the end of its body. None of them binds to the
    /// form's resource, so a form or a validator passes over them.
    /// </summary>
    public static readonly string[] ContentAliases = [RichTextAlias, HtmlAlias, CssAlias, ScriptAlias];

    /// <summary>
    /// The files of a record: inside a form, the form's record; on its own, the record of the resource and key the
    /// block names. It binds to no field, so a form passes over it, and it is interactive, so a page that carries one
    /// resolves the signed-in person as it does for a form.
    /// </summary>
    public const string AttachmentsAlias = "appAttachments";

    /// <summary>
    /// Resources against time: rows from one source, bars from another, in a window the page's address sets. It binds
    /// to no field and reads its sources for the signed-in person, so a page that carries one resolves them as for a form.
    /// </summary>
    public const string ScheduleAlias = "appSchedule";

    /// <summary>
    /// The rows of a record, edited several at a time: a child data source whose link field holds the record's key.
    /// Inside a form the record is the form's; on its own the block names the owner, as Attachments does. It binds to
    /// no field and reads and writes for the signed-in person, so a page that carries one resolves them as for a form.
    /// </summary>
    public const string TabularAlias = "appTabular";

    /// <summary>
    /// A form's spam check, which the form endpoints enforce on the server before its class runs. It binds to no field and
    /// runs no editor-written code, which is why the Forms container's grid can offer it on a plain site.
    /// </summary>
    public const string AntiSpamAlias = "appAntiSpam";

    // The seed is older than the namespace rename. Every stored content key derives from it, so it does not change.
    public static Guid Key(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes("UmbracoAppHost.Blocks.v1:" + value))[..16]);
}
