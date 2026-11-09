using System.Security.Claims;
using System.Text.Json;
using AppBlocks.Authentication;
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
using AppBlocks.DataSources;

namespace AppBlocks.Api;

/// <param name="Route">Sample values of the route's parameters, by name.</param>
/// <param name="Query">Sample query string parameters, by name.</param>
public sealed record ApiEndpointTestRequest(Guid? Key, Guid? ParentKey, Dictionary<string, JsonElement> Values,
    Dictionary<string, string>? Route = null, Dictionary<string, string>? Query = null);

/// <summary>What the endpoint would answer: the definition's problems, or the status and body a request would get.</summary>
public sealed record ApiEndpointTestResult(IReadOnlyList<string> Errors, int? Status, object? Body, string? Path);

/// <summary>
/// The endpoint editor's Test button and full URL. Testing reads the current edits, unsaved and unpublished, and answers as the
/// endpoint would answer the signed-in backoffice User: through the same runner, so the source's access, restriction and policies apply.
/// </summary>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("api-endpoints")]
[ApiExplorerSettings(GroupName = "Application blocks")]
public sealed class ApiEndpointAuthoringController(IContentService content, IAuthorizationService authorization, IBackOfficeSecurityAccessor security,
    IMemberGroupService groups, ApiEndpointCatalog catalog, ApiEndpointRunner runner, IConfiguration configuration) : ManagementApiControllerBase
{
    [HttpGet("settings")]
    [Authorize(Policy = AuthorizationPolicies.SectionAccessContent)]
    public IActionResult Settings() => Ok(new { basePath = ApiEndpointRoutes.BasePath(configuration) });

    [HttpPost("test")]
    [Authorize(Policy = AuthorizationPolicies.SectionAccessContent)]
    [Authorize(Policy = AppBlocksAuthorization.AdministratorPolicy)]
    public async Task<IActionResult> Test(ApiEndpointTestRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var node = request.Key is { } key ? content.GetById(key) : null;
        if (node is not null && node.ContentType.Alias != ApiEndpointCatalog.Alias) return BadRequest();
        if (node is null && (request.ParentKey is not { } parent || content.GetById(parent) is null)) return NotFound();
        var permission = ContentPermissionResource.WithKeys(node is null ? ActionNew.ActionLetter : ActionUpdate.ActionLetter, node?.Key ?? request.ParentKey);
        if (!(await authorization.AuthorizeResourceAsync(User, permission, AuthorizationPolicies.ContentPermissionByResource)).Succeeded) return Forbid();
        if (security.BackOfficeSecurity?.CurrentUser is not { } user) return Forbid();
        await catalog.LoadAsync();
        var errors = new List<string>();
        var endpoint = catalog.Read(node?.Key ?? request.Key ?? Guid.NewGuid(), node?.Name ?? "New API endpoint",
            alias => request.Values.TryGetValue(alias, out var value) ? PropertyValue(value) : null,
            new MemberGroupNames((await groups.GetAllAsync()).ToArray()).Resolve, errors);
        if (endpoint is null) return Ok(new ApiEndpointTestResult(errors, null, null, null));
        var route = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in endpoint.Route.Parameters)
            if (request.Route?.FirstOrDefault(pair => pair.Key.Equals(parameter, StringComparison.OrdinalIgnoreCase)).Value is { Length: > 0 } value) route[parameter] = value;
            else errors.Add($"Enter a sample value for :{parameter}.");
        if (errors.Count > 0) return Ok(new ApiEndpointTestResult(errors, null, null, null));
        var query = request.Query?.Where(pair => SqlTemplate.IsIdentifier(pair.Key) && pair.Value.Length > 0)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase) ?? [];
        // The User as the site sees a backoffice ticket; the resolver reads their current standing and groups from the store.
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Key.ToString())], Umbraco.Cms.Core.Constants.Security.BackOfficeAuthenticationType));
        var result = await runner.RunAsync(endpoint, principal, route, query, ct);
        var path = ApiEndpointRoutes.BasePath(configuration) + "/" + string.Join('/', endpoint.Route.Segments.Select(s => Uri.EscapeDataString(s.IsParameter ? route[s.Value] : s.Value)))
            + (query.Count == 0 ? "" : "?" + string.Join('&', query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));
        return Ok(new ApiEndpointTestResult([], result.Status, result.Body, path));
    }

    private static object? PropertyValue(JsonElement value) => value.ValueKind switch {
        JsonValueKind.Null or JsonValueKind.Undefined => null, JsonValueKind.True => true, JsonValueKind.False => false,
        JsonValueKind.String => value.GetString(), _ => value.GetRawText(),
    };
}
