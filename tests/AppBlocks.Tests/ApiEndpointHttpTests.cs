using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AppBlocks.Api;
using AppBlocks.Connections;
using AppBlocks.DataSources;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace AppBlocks.Tests;

/// <summary>Published API endpoints over the block site's orders, called over HTTP as anonymous visitors, Members and backoffice Users.</summary>
[Collection("Standalone Umbraco")]
public sealed class ApiEndpointHttpTests
{
    private const string Api = "/api/data/";

    [Fact]
    public async Task Endpoints_serve_rows_as_json_under_their_sources_access()
    {
        await using var site = await BlockSite.BootAsync(configure: builder => builder.Services.AddScoped<IDataSourceHandler, RefuseQueries>());
        await site.PublishAsync();
        using var scope = site.CreateScope();
        var services = scope.ServiceProvider;
        var contents = services.GetRequiredService<IContentService>();
        var root = contents.GetRootContent().Single(c => c.ContentType.Alias == DataSourceSchema.RootAlias);
#pragma warning disable CS0618
        // Where endpoints sit is the editors' choice: some in a root of their own, some beside the data sources they read.
        var api = contents.Create("API", -1, ApiEndpointCatalog.RootAlias);
        Assert.True(contents.Save(api).Success);
        Publish(contents, api);
#pragma warning restore CS0618
        var buyers = new MemberGroup { Name = "Buyers" };
        Assert.True((await services.GetRequiredService<IMemberGroupService>().CreateAsync(buyers)).Success);

        // Data sources the endpoints pick, each with access of its own.
        Publish(contents, Node(contents, root, DataSourceSchema.SqlAlias, "Private orders", ("alias", "orders.private"), ("access", "Not public"), ("source", "ORDERS")));
        Publish(contents, Node(contents, root, DataSourceSchema.SqlAlias, "My orders", ("alias", "orders.mine"), ("access", "Public"), ("source", "SELECT ORDER_ID, CUSTOMER FROM ORDERS WHERE CUSTOMER = :USER_KEY")));
        Publish(contents, Node(contents, root, DataSourceSchema.SqlAlias, "Buyer orders", ("alias", "orders.buyers"), ("access", "Specific groups"), ("readGroups", buyers.Key.ToString()), ("source", "ORDERS")));
        Publish(contents, Node(contents, root, DataSourceSchema.SqlAlias, "Guarded orders", ("alias", "orders.guarded"), ("access", "Public"), ("source", "ORDERS"), ("handler", RefuseQueries.Name)));
        var lines = Endpoint(contents, api, "lines", "orders/:id/lines", ("sql", "SELECT LINE_ID, ORDER_ID, ITEM, QUANTITY, UNIT_PRICE, NOTES FROM ORDER_LINES WHERE ORDER_ID = :PARAM.id AND (:PARAM.item IS NULL OR ITEM = :PARAM.item)"),
            ("access", "Public"), ("queryParameters", "item"), ("orderBy", "LINE_ID"));
        var order = Endpoint(contents, api, "order", "orders/:id", ("sql", "SELECT ORDER_ID, CUSTOMER FROM ORDERS WHERE ORDER_ID = :PARAM.id"), ("access", "Public"), ("shape", "Single"));
        Endpoint(contents, api, "latest", "orders/latest", ("sql", "SELECT ORDER_ID, CUSTOMER FROM ORDERS"), ("access", "Public"), ("shape", "Single"), ("orderBy", "ORDER_ID DESC"));
        Endpoint(contents, api, "all-lines", "lines", ("dataSource", "orders.lines"), ("maximumRows", "3"));
        Endpoint(contents, root, "private", "private/orders", ("dataSource", "orders.private"));
        Endpoint(contents, root, "mine", "mine", ("dataSource", "orders.mine"));
        Endpoint(contents, root, "buyers", "buyers/orders", ("dataSource", "orders.buyers"));
        Endpoint(contents, root, "guarded", "guarded", ("dataSource", "orders.guarded"));

        using var anonymous = site.CreateClient();

        // A public collection, typed and keyed by field alias, in Order by's order.
        using (var response = await anonymous.GetAsync(Api + "orders/4/lines"))
        {
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType!.ToString());
            Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
            var items = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(["Bearing", "Sprocket"], items.Select(i => i.GetProperty("item").GetString()));
            Assert.Equal(JsonValueKind.Number, items[0].GetProperty("quantity").ValueKind);
            Assert.Equal(12.5m, items[0].GetProperty("unitPrice").GetDecimal());
            Assert.Equal(4, items[0].GetProperty("orderId").GetInt32());
            Assert.Equal(JsonValueKind.Null, items[0].GetProperty("notes").ValueKind);
        }
        // The query string fills what the route does not; a route segment wins over a query parameter of its name.
        Assert.Equal(["Sprocket"], await Items(anonymous, "orders/4/lines?item=Sprocket", "item"));
        Assert.Equal(["Bearing", "Sprocket"], await Items(anonymous, "orders/4/lines?id=5", "item"));
        Assert.Equal(["Chain", "Oil"], await Items(anonymous, "ORDERS/5/Lines", "item"));

        // A single item, and 404 when there is none; a literal route wins over a parameter, and Order by decides the first row.
        Assert.Equal("Globex", (await Json(anonymous, "orders/5", HttpStatusCode.OK)).GetProperty("customer").GetString());
        var missing = await Json(anonymous, "orders/99", HttpStatusCode.NotFound);
        Assert.Equal(404, missing.GetProperty("status").GetInt32());
        Assert.Equal(5, (await Json(anonymous, "orders/latest", HttpStatusCode.OK)).GetProperty("orderId").GetInt32());

        // More rows than the endpoint's maximum is a server error that names nothing of the query.
        using (var over = await anonymous.GetAsync(Api + "lines"))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, over.StatusCode);
            var text = await over.Content.ReadAsStringAsync();
            Assert.DoesNotContain("ORDER_LINES", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("rows", text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(500, JsonDocument.Parse(text).RootElement.GetProperty("status").GetInt32());
        }

        // Unknown routes, empty segment values and other methods.
        await Json(anonymous, "nothing/here", HttpStatusCode.NotFound);
        await Json(anonymous, "orders//lines", HttpStatusCode.NotFound);
        await Json(anonymous, "orders/4/lines/", HttpStatusCode.NotFound);
        using (var post = await anonymous.PostAsJsonAsync(Api + "orders/4/lines", new { }))
        {
            Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
            Assert.Equal(["GET"], post.Content.Headers.Allow);
            Assert.Equal(405, (await post.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetInt32());
        }

        // Anonymous callers: refused by Not public, and by a Public source that binds the caller; a backing class refuses too.
        await Json(anonymous, "private/orders", HttpStatusCode.Unauthorized);
        await Json(anonymous, "mine", HttpStatusCode.Unauthorized);
        await Json(anonymous, "buyers/orders", HttpStatusCode.Unauthorized);
        await Json(anonymous, "guarded", HttpStatusCode.Unauthorized);

        // A Member reaches Not public and their own rows, but Specific groups only once in the group; the backing class still refuses.
        var member = site.CreateMember("api.member", "api.member@example.test", "Api Member");
        using var signedIn = await site.SignInMemberAsync(member);
        Assert.Equal(2, (await Json(signedIn, "private/orders", HttpStatusCode.OK)).GetProperty("items").GetArrayLength());
        Assert.Equal(0, (await Json(signedIn, "mine", HttpStatusCode.OK)).GetProperty("items").GetArrayLength());
        var forbidden = await Json(signedIn, "buyers/orders", HttpStatusCode.Forbidden);
        Assert.Equal("{\"status\":403,\"title\":\"Forbidden\"}", forbidden.GetRawText());
        await Json(signedIn, "guarded", HttpStatusCode.Forbidden);
#pragma warning disable CS0618
        services.GetRequiredService<IMemberService>().AssignRole(member.Id, "Buyers");
#pragma warning restore CS0618
        Assert.Equal(2, (await Json(signedIn, "buyers/orders", HttpStatusCode.OK)).GetProperty("items").GetArrayLength());

        // Publishing refuses a :PARAM. nothing can send, a route another endpoint answers, and settings beside a selected data source.
        Assert.Contains(":PARAM.status, which is neither a route parameter nor listed under Query parameters",
            Refused(contents, Node(contents, api, ApiEndpointCatalog.Alias, "Typo", ("alias", "typo"), ("route", "typo/:id"), ("access", "Public"),
                ("sql", "SELECT ORDER_ID FROM ORDERS WHERE ORDER_ID = :PARAM.id AND CUSTOMER = :PARAM.status"))));
        Assert.Contains("already answers the route orders/:id",
            Refused(contents, Node(contents, api, ApiEndpointCatalog.Alias, "Other order", ("alias", "other-order"), ("route", "ORDERS/:key"), ("access", "Public"), ("sql", "ORDERS"))));
        Assert.Contains("clear the endpoint's Access",
            Refused(contents, Node(contents, api, ApiEndpointCatalog.Alias, "Mixed", ("alias", "mixed"), ("route", "mixed"), ("dataSource", "orders.lines"), ("access", "Public"))));
        Assert.Contains("Order by names MISSING",
            Refused(contents, Node(contents, api, ApiEndpointCatalog.Alias, "Unordered", ("alias", "unordered"), ("route", "unordered"), ("dataSource", "orders.lines"), ("orderBy", "MISSING"))));
        Assert.Contains("Select an access value",
            Refused(contents, Node(contents, api, ApiEndpointCatalog.Alias, "Open", ("alias", "open"), ("route", "open"), ("sql", "ORDERS"))));

        // The Test button answers as the signed-in backoffice User, from unsaved values.
        using var editor = await site.SignInAsAdministratorAsync();
        using (var test = await editor.PostAsJsonAsync("/umbraco/management/api/v1/api-endpoints/test", new {
            key = lines.Key,
            values = new { alias = "lines", route = "orders/:id/lines", access = "Not public", queryParameters = "item", orderBy = "LINE_ID DESC",
                sql = "SELECT LINE_ID, ITEM FROM ORDER_LINES WHERE ORDER_ID = :PARAM.id AND (:PARAM.item IS NULL OR ITEM = :PARAM.item)" },
            route = new { id = "5" }, query = new { item = "" },
        }))
        {
            Assert.Equal(HttpStatusCode.OK, test.StatusCode);
            var result = await test.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Empty(result.GetProperty("errors").EnumerateArray());
            Assert.Equal(200, result.GetProperty("status").GetInt32());
            Assert.Equal("/api/data/orders/5/lines", result.GetProperty("path").GetString());
            Assert.Equal(["Oil", "Chain"], result.GetProperty("body").GetProperty("items").EnumerateArray().Select(i => i.GetProperty("item").GetString()));
        }
        using (var settings = await editor.GetAsync("/umbraco/management/api/v1/api-endpoints/settings"))
            Assert.Equal("/api/data", (await settings.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("basePath").GetString());
        using (var untested = await editor.PostAsJsonAsync("/umbraco/management/api/v1/api-endpoints/test", new { key = order.Key, values = new { alias = "order", route = "orders/:id", access = "Public", sql = "ORDERS" } }))
            Assert.Contains("Enter a sample value for :id.", (await untested.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").EnumerateArray().Select(e => e.GetString()));
        // A body without values is refused before the controller reads them: the values are required, so it is not a server error.
        using (var valueless = await editor.PostAsJsonAsync("/umbraco/management/api/v1/api-endpoints/test", new { key = order.Key }))
            Assert.Equal(HttpStatusCode.BadRequest, valueless.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/umbraco/management/api/v1/api-endpoints/test", new { key = order.Key, values = new { } })).StatusCode);

        // Unpublishing takes the route away from the next request; the literal route beside it stays.
#pragma warning disable CS0618
        Assert.True(contents.Unpublish(contents.GetById(order.Key)!).Success);
#pragma warning restore CS0618
        await Json(anonymous, "orders/5", HttpStatusCode.NotFound);
        await Json(anonymous, "orders/latest", HttpStatusCode.OK);
        // Unpublishing the root that keeps endpoints together takes them all away; endpoints elsewhere still answer.
#pragma warning disable CS0618
        Assert.True(contents.Unpublish(contents.GetById(api.Key)!).Success);
#pragma warning restore CS0618
        await Json(anonymous, "orders/latest", HttpStatusCode.NotFound);
        await Json(anonymous, "orders/4/lines", HttpStatusCode.NotFound);
        Assert.Equal(2, (await Json(signedIn, "private/orders", HttpStatusCode.OK)).GetProperty("items").GetArrayLength());
    }

    private static async Task<JsonElement> Json(HttpClient client, string path, HttpStatusCode status)
    {
        using var response = await client.GetAsync(Api + path);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(status == response.StatusCode, $"GET {path}: {(int)response.StatusCode} {text}");
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<IEnumerable<string?>> Items(HttpClient client, string path, string field)
        => (await Json(client, path, HttpStatusCode.OK)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty(field).GetString()).ToArray();

#pragma warning disable CS0618
    private static IContent Node(IContentService contents, IContent root, string type, string name, params (string Alias, string Value)[] values)
    {
        var node = contents.Create(name, root.Id, type);
        if (type == DataSourceSchema.SqlAlias) node.SetValue("connection", ConnectionCatalog.ApplicationConnection);
        foreach (var (alias, value) in values) node.SetValue(alias, value);
        Assert.True(contents.Save(node).Success);
        return node;
    }

    private static IContent Endpoint(IContentService contents, IContent root, string alias, string route, params (string Alias, string Value)[] values)
        => Publish(contents, Node(contents, root, ApiEndpointCatalog.Alias, alias, [("alias", alias), ("route", route), .. values]));

    private static IContent Publish(IContentService contents, IContent node)
    {
        var published = contents.Publish(node, ["*"]);
        Assert.True(published.Success, node.Name + ": " + string.Join("; ", published.EventMessages?.GetAll().Select(m => m.Message) ?? []));
        return node;
    }

    private static string Refused(IContentService contents, IContent node)
    {
        var published = contents.Publish(node, ["*"]);
        Assert.False(published.Success, node.Name + " was published.");
        return string.Join("; ", published.EventMessages?.GetAll().Select(m => m.Message) ?? []);
    }
#pragma warning restore CS0618

    /// <summary>A backing class that refuses every query, as a site's own rule might.</summary>
    private sealed class RefuseQueries : IDataSourceHandler
    {
        public const string Name = "refuse-queries";
        public string Alias => Name;
        public ValueTask<DataSourceOutcome> QueryAsync(DataSourceOperation operation, CancellationToken ct) => ValueTask.FromResult(DataSourceOutcome.Refuse());
    }
}

/// <summary>
/// The block site for tests/ui/api-endpoints.browser.test.mjs: writes its address, an administrator's management API token and its Data
/// endpoints root to the file APPBLOCKS_API_HOST names, then serves until the browser test deletes that file.
/// </summary>
public sealed class ApiEndpointBrowserHost
{
    [Fact(Explicit = true)]
    public async Task Serve_for_the_browser_test()
    {
        var control = Environment.GetEnvironmentVariable("APPBLOCKS_API_HOST") ?? throw new InvalidOperationException("Set APPBLOCKS_API_HOST to a file path.");
        await using var site = await BlockSite.StartAsync();
        // The token goes with the backoffice's own cookies, which a browser signed in to the backoffice holds.
        var cookies = new CookieContainer();
        using var admin = await site.SignInAsAdministratorAsync(cookies);
        using var scope = site.CreateScope();
        var contents = scope.ServiceProvider.GetRequiredService<IContentService>();
#pragma warning disable CS0618
        var root = contents.Create("API", -1, ApiEndpointCatalog.RootAlias);
        Assert.True(contents.Save(root).Success);
        Assert.True(contents.Publish(root, ["*"]).Success);
#pragma warning restore CS0618
        var type = scope.ServiceProvider.GetRequiredService<IContentTypeService>().Get(ApiEndpointCatalog.Alias)!;
        await System.IO.File.WriteAllTextAsync(control, JsonSerializer.Serialize(new {
            address = site.Address.ToString().TrimEnd('/'), token = admin.DefaultRequestHeaders.Authorization!.Parameter, root = root.Key, type = type.Key,
            cookies = cookies.GetAllCookies().Select(cookie => new { name = cookie.Name, value = cookie.Value, path = cookie.Path, httpOnly = cookie.HttpOnly }),
        }), TestContext.Current.CancellationToken);
        while (System.IO.File.Exists(control)) await Task.Delay(200, TestContext.Current.CancellationToken);
    }
}
