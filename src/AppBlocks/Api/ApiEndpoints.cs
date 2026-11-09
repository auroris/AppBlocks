using System.Data.Common;
using System.Security.Claims;
using AppBlocks.Authentication;
using AppBlocks.Blocks;
using AppBlocks.DataSources;
using AppBlocks.Forms;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;

namespace AppBlocks.Api;

/// <summary>An endpoint's answer: its status and the JSON body, the rows or a problem of <c>status</c> and <c>title</c>.</summary>
public sealed record ApiEndpointResult(int Status, object Body);

/// <summary>
/// Runs an endpoint for one caller. There is no page, so Umbraco Public Access has nothing to say: the read goes through
/// <see cref="DataSourceQueries"/> like a list of values does, and the source's access, row restriction, policies and backing class decide.
/// </summary>
public sealed class ApiEndpointRunner(DataSourceQueries queries, ILogger<ApiEndpointRunner> logger)
{
    /// <param name="route">The route parameter values, which win over query parameters of the same name.</param>
    /// <param name="query">The query string's parameters, bounded as <see cref="FormEndpoints.Parameters"/> bounds them.</param>
    public async Task<ApiEndpointResult> RunAsync(ApiEndpointDefinition endpoint, ClaimsPrincipal principal, IReadOnlyDictionary<string, string> route,
        IReadOnlyDictionary<string, string> query, CancellationToken ct)
    {
        var parameters = new Dictionary<string, string>(query, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in route) parameters[name] = value;
        var context = new FormExecutionContext(principal, Guid.Empty, Guid.Empty, endpoint.Source.ComputeFingerprint(), Parameters: parameters);
        try
        {
            // A single item reads two rows at most and keeps the first, so a second row is no error.
            var single = endpoint.Shape == ApiEndpointShape.Single;
            var result = await queries.QueryAsync(endpoint.Source, context, single ? 2 : endpoint.MaximumRows, endpoint.Order, firstRows: single, ct);
            var items = result.Rows.Select(row => Item(result.Columns, row, endpoint.Source.DateTimes)).ToArray();
            if (!single) return new(200, new { items });
            return items.Length > 0 ? new(200, items[0]) : Problem(404);
        }
        catch (UnauthorizedAccessException) { return Problem(principal.Identities.Any(identity => identity.IsAuthenticated) ? 403 : 401); }
        catch (Exception error) when (error is DataSourceException or DbException)
        {
            logger.LogError(error, "API endpoint {Alias} ({Route}) failed.", endpoint.Alias, endpoint.Route);
            return Problem(500);
        }
    }

    /// <summary>One row by field alias, in the JSON types the forms use, with numbers as JSON numbers.</summary>
    private static Dictionary<string, object?> Item(IReadOnlyList<DataSourceQueryColumn> columns, IReadOnlyDictionary<string, object?> row, DataSourceDateTimes dateTimes)
    {
        var item = new Dictionary<string, object?>(columns.Count, StringComparer.Ordinal);
        foreach (var column in columns)
        {
            var raw = row.GetValueOrDefault(column.Name);
            // A number the database returns stays a number where the schema could not say (SQLite reports a SELECT's columns as text).
            var kind = column.Kind == FieldKinds.Text && raw is long or int or short or byte or decimal or double or float ? FieldKinds.Number : column.Kind;
            var value = FieldValues.FromDatabase(raw, kind, dateTimes) ?? raw;
            item[column.Field] = value switch { null => null, decimal number => number, bool flag => flag, _ => FieldValues.ToJson(value) };
        }
        return item;
    }

    /// <summary>A problem body that names nothing about the endpoint, its SQL or its database; those go to the log.</summary>
    public static ApiEndpointResult Problem(int status) => new(status, new {
        status,
        title = status switch {
            401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Internal Server Error",
        },
    });
}

/// <summary>
/// The API endpoints' one route: <c>AppBlocks:ApiBasePath</c> (by default <c>/api/data</c>) and anything below it, matched against the
/// published endpoints at request time. GET only; every answer is JSON and <c>no-store</c>, like the blocks' own endpoints.
/// </summary>
public static class ApiEndpointRoutes
{
    public const string BasePathSetting = "AppBlocks:ApiBasePath";
    public const string DefaultBasePath = "/api/data";

    /// <summary>The configured base path with one leading slash and none trailing.</summary>
    public static string BasePath(IConfiguration? configuration)
    {
        var configured = configuration?[BasePathSetting]?.Trim().Trim('/');
        return string.IsNullOrEmpty(configured) ? DefaultBasePath : "/" + configured;
    }

    public static void MapApiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var basePath = BasePath(endpoints.ServiceProvider.GetService<IConfiguration>());
        // Matching against the published templates happens in the handler, on the request path, so every path below the base gets a JSON answer.
        endpoints.Map(basePath + "/{**path}", (Delegate)((HttpContext http) => HandleAsync(http, basePath)));
    }

    private static async Task<IResult> HandleAsync(HttpContext http, string basePath)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsGet(http.Request.Method))
        {
            http.Response.Headers.Allow = "GET";
            return Json(ApiEndpointRunner.Problem(405));
        }
        var services = http.RequestServices;
        var catalog = services.GetRequiredService<ApiEndpointCatalog>();
        await catalog.LoadAsync();
        var path = http.Request.Path.Value ?? "";
        if (catalog.Find(ApiRouteTemplate.SplitPath(path.Length > basePath.Length ? path[(basePath.Length + 1)..] : "")) is not { } found)
            return Json(ApiEndpointRunner.Problem(404));
        var principal = await PrincipalAsync(http);
        return Json(await services.GetRequiredService<ApiEndpointRunner>().RunAsync(found.Endpoint, principal, found.Values,
            FormEndpoints.Parameters(http.Request.Query), http.RequestAborted));
    }

    private static IResult Json(ApiEndpointResult result) => Results.Json(result.Body, statusCode: result.Status);

    /// <summary>
    /// The signed-in Member and backoffice User, Member first, as the block endpoints see them, or nobody. The data source's resolver
    /// then decides each account's standing, so a cookie for a locked-out account stays refused rather than becoming anonymous.
    /// </summary>
    private static async Task<ClaimsPrincipal> PrincipalAsync(HttpContext http)
    {
        var identities = new List<ClaimsIdentity>();
        var member = await http.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (member.Succeeded && member.Principal?.Identity?.IsAuthenticated == true) identities.AddRange(member.Principal.Identities);
        if (await ApplicationAccessPolicy.BackOfficeTicketAsync(http) is { } backOffice) identities.AddRange(backOffice.Identities);
        return identities.Count == 0 ? AppPageAccess.Anonymous : new ClaimsPrincipal(identities);
    }
}

public static class ApiEndpointExtensions
{
    /// <summary>Registers the published API endpoints: their catalogue, the runner, and the check publishing makes.</summary>
    public static IUmbracoBuilder AddApiEndpoints(this IUmbracoBuilder builder)
    {
        builder.Services.AddOptions<ApiEndpointOptions>().Bind(builder.Config.GetSection(ApiEndpointOptions.Section));
        builder.Services.AddScoped<ApiEndpointCatalog>();
        builder.Services.AddScoped<ApiEndpointRunner>();
        builder.AddNotificationAsyncHandler<ContentPublishingNotification, ApiEndpointValidator>();
        return builder;
    }
}
