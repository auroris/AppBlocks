using Umbraco.Cms.Core.Models.PublishedContent;

namespace AppBlocks.InteractiveReports.Blocks;

/// <summary>
/// Who sees one of a report block's links to its form page, as the block's Create button and Open link settings choose. Without a
/// form page the block offers neither.
/// </summary>
public enum ReportLinkVisibility
{
    /// <summary>Those the form page would let do it: add a record for the create button, change one for the open link.</summary>
    FormPageDecides,
    /// <summary>Everyone the report's page admits.</summary>
    Public,
    /// <summary>Those in one of the selected backoffice user groups or member groups.</summary>
    Groups,
    /// <summary>Nobody.</summary>
    Hidden,
}

/// <summary>A report block's setting for one link: who sees it, and the groups that see it when that is <see cref="ReportLinkVisibility.Groups"/>.</summary>
/// <param name="UserGroups">Backoffice user group aliases.</param>
/// <param name="MemberGroups">The member group picker's value as Umbraco stores it: group ids or keys, separated by commas.</param>
public sealed record ReportLinkRule(ReportLinkVisibility Visibility, IReadOnlyList<string> UserGroups, string MemberGroups)
{
    /// <summary>The prefixes of the two links' properties: <c>createVisibility</c>, <c>createUserGroups</c>, <c>createMemberGroups</c>, and the same for <c>open</c>.</summary>
    public const string Create = "create", Open = "open";

    /// <summary>The stored choice. An empty value, and one no choice names, are Form page decides, as a new block starts.</summary>
    public static ReportLinkVisibility Parse(string? value) => value?.Trim() switch
    {
        "Public" => ReportLinkVisibility.Public,
        "Groups" => ReportLinkVisibility.Groups,
        "Hidden" => ReportLinkVisibility.Hidden,
        _ => ReportLinkVisibility.FormPageDecides,
    };

    /// <summary>The backoffice group picker's value: aliases separated by commas.</summary>
    public static IReadOnlyList<string> UserGroupAliases(string? value)
        => (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>One link's setting from its properties, read through <paramref name="text"/>, which gives a property's value as text.</summary>
    public static ReportLinkRule Read(string link, Func<string, string?> text)
        => new(Parse(text(link + "Visibility")), UserGroupAliases(text(link + "UserGroups")), text(link + "MemberGroups")?.Trim() ?? "");

    /// <summary>One link's setting from a published block.</summary>
    public static ReportLinkRule Read(string link, IPublishedElement block)
        => Read(link, alias => block.GetProperty(alias)?.GetValue() is { } value ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) : null);

    /// <summary>Whether Groups has a group to show the link to.</summary>
    public bool HasGroups => UserGroups.Count > 0 || MemberGroups.Length > 0;
}
