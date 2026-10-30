using System.Text.Json;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Core.Actions;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Security.Authorization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Web.Common.Authorization;

namespace UmbracoAppHost.DataSources;

public sealed record DataSourceTestRequest(Guid? Key, Guid? ParentKey, Dictionary<string, JsonElement> Values,
    Dictionary<string, JsonElement>? Samples = null);

/// <summary>The editor's Test action uses the same node permissions as editing the definition.</summary>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("data-sources")]
[ApiExplorerSettings(GroupName = "Application blocks")]
public sealed class DataSourceAuthoringController(IContentService content, IAuthorizationService authorization,
    IBackOfficeSecurityAccessor security, IMemberGroupService groups, DataSourcePreview preview) : ManagementApiControllerBase
{
    [HttpPost("test")]
    [Authorize(Policy = AuthorizationPolicies.SectionAccessContent)]
    public async Task<IActionResult> Test(DataSourceTestRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var node = request.Key is { } key ? content.GetById(key) : null;
        if (node is not null && node.ContentType.Alias != DataSourceSchema.SqlAlias) return BadRequest();
        if (node is null && (request.ParentKey is not { } parent || content.GetById(parent) is null)) return NotFound();
        var permission = ContentPermissionResource.WithKeys(node is null ? ActionNew.ActionLetter : ActionUpdate.ActionLetter, node?.Key ?? request.ParentKey);
        if (!(await authorization.AuthorizeResourceAsync(User, permission, AuthorizationPolicies.ContentPermissionByResource)).Succeeded) return Forbid();
        if (security.BackOfficeSecurity?.CurrentUser is not { } user) return Forbid();
        var errors = new List<string>();
        var definition = DataSourceDefinition.Read(node?.Key ?? request.Key ?? Guid.NewGuid(), node?.Name ?? "New data source",
            alias => request.Values.TryGetValue(alias, out var value) ? PropertyValue(value) : null,
            new MemberGroupNames(groups).Resolve, errors);
        if (definition is null) return Ok(new DataSourcePreviewResult([], null, errors));
        var editor = new DataSourceUser("backoffice:" + user.Key, null, user.Username, user.Email,
            user.Groups.Select(g => g.Name ?? g.Alias).ToArray(), user.IsAdmin());
        return Ok(await preview.TestAsync(definition, editor, request.Samples ?? [], ct));
    }

    private static object? PropertyValue(JsonElement value) => value.ValueKind switch {
        JsonValueKind.Null or JsonValueKind.Undefined => null, JsonValueKind.True => true, JsonValueKind.False => false,
        JsonValueKind.String => value.GetString(), _ => value.GetRawText(),
    };
}
