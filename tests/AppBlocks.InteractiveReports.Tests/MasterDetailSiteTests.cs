using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AppBlocks.Connections;
using AppBlocks.DataSources;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace AppBlocks.InteractiveReports.Tests;

/// <summary>
/// A form on the data source engine and the Tabular form block inside it, through the site's real endpoints: the block's rows go with
/// the form's own request, the order's backing class sees them with the order, and one transaction keeps the order and its lines or
/// neither. A new order's lines are linked to its new key, and a retried request writes nothing twice.
/// </summary>
[Collection("Standalone Umbraco")]
public sealed class MasterDetailSiteTests
{
    [Fact]
    public async Task A_form_and_its_tabular_rows_save_in_one_request_and_one_transaction()
    {
        await using var site = await PublicSite.StartAsync();
        IMember member = null!;
        await site.WithServices(services => {
            var members = services.GetRequiredService<IMemberService>();
            member = members.CreateMemberWithIdentity("orders-" + Guid.NewGuid().ToString("N")[..8], $"orders-{Guid.NewGuid():N}@example.invalid", "Orders member", "Member", isApproved: true);
            members.Save(member);
            return Task.CompletedTask;
        });
        using var client = await site.SignedIn("member", member.Id.ToString(), member.Username);
        var form = PublicSite.FormUrl(PublicSite.OrderPage, PublicSite.OrderForm);
        var lines = $"/api/tabular/{PublicSite.OrderPage}/{PublicSite.OrderLines}";
        var html = await client.GetStringAsync("/order/");
        Assert.Contains($"data-block=\"{PublicSite.OrderLines}\"", html);
        Assert.Contains("data-owner=\"form\"", html);

        // A new order: the form starts empty, and the block lists no lines from /new, ready to add some.
        var state = await Json(await client.GetAsync(form));
        var fresh = await Json(await client.GetAsync(lines + "/new"));
        Assert.Empty(fresh.GetProperty("rows").EnumerateArray());
        Assert.True(fresh.GetProperty("canAdd").GetBoolean());
        var revision = fresh.GetProperty("revision").GetString();
        var token = state.GetProperty("antiforgeryToken").GetString()!;
        var orderKey = Guid.NewGuid().ToString();
        var linesKey = Guid.NewGuid().ToString();
        object Create(params string[] items) => new {
            values = new { customer = "Initech" }, definitionVersion = state.GetProperty("definitionVersion").GetString(), submissionKey = orderKey,
            details = new[] { new { block = PublicSite.OrderLines, revision, submissionKey = linesKey, changes = items.Select(item => new { operation = "create", values = new { item } }).ToArray() } },
        };

        // The order and its lines in one request: the lines carry the order's new key, and its class counted them with it.
        using var created = await Send(client, HttpMethod.Post, form, token, Create("Bolt", "Nut"));
        var body = await Json(created);
        var key = body.GetProperty("record").GetProperty("key").GetString()!;
        // The form shows its bound fields only; the count the class kept is in the row.
        Assert.Equal(2L, await Scalar(site, $"SELECT lines FROM PUBLIC_ORDERS WHERE id = {key}"));
        var list = Assert.Single(body.GetProperty("details").EnumerateArray()).GetProperty("list");
        Assert.Equal(key, list.GetProperty("owner").GetProperty("key").GetString());
        Assert.Equal(["Bolt", "Nut"], list.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("values").GetProperty("item").GetString()));
        Assert.Equal(2L, await Scalar(site, $"SELECT COUNT(*) FROM PUBLIC_ORDER_LINES WHERE order_id = {key}"));
        Assert.Contains(site.Service<OrderRules>().Seen, seen => seen == (DataSourceAction.Create, 2));

        // The same request again, as after a lost answer: the same order, and nothing written twice.
        using (var retried = await Send(client, HttpMethod.Post, form, token, Create("Bolt", "Nut")))
            Assert.Equal(key, (await Json(retried)).GetProperty("record").GetProperty("key").GetString());
        Assert.Equal(1L, await Scalar(site, "SELECT COUNT(*) FROM PUBLIC_ORDERS"));
        Assert.Equal(2L, await Scalar(site, $"SELECT COUNT(*) FROM PUBLIC_ORDER_LINES WHERE order_id = {key}"));

        // An update with a line the class refuses: neither the order's change nor any line is written.
        var loaded = await Json(await client.GetAsync($"{form}?id={key}"));
        var rows = list.GetProperty("rows").EnumerateArray().ToArray();
        object Update(string customer, params object[] changes) => new {
            values = new { customer }, definitionVersion = loaded.GetProperty("definitionVersion").GetString(), version = loaded.GetProperty("record").GetProperty("version").GetString(),
            submissionKey = Guid.NewGuid().ToString(), details = new[] { new { block = PublicSite.OrderLines, revision, submissionKey = Guid.NewGuid().ToString(), changes } },
        };
        using (var refused = await Send(client, HttpMethod.Put, $"{form}/{key}?id={key}", token,
            Update("Initech Two", new { operation = "create", values = new { item = "Washer" } }, new { operation = "create", values = new { item = "Forbidden" } })))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Contains("Forbidden is not sold.", await refused.Content.ReadAsStringAsync());
        }
        // A line the engine refuses names its block and its place, and rolls the order back with it.
        using (var refused = await Send(client, HttpMethod.Put, $"{form}/{key}?id={key}", token,
            Update("Initech Two", new { operation = "delete", key = rows[0].GetProperty("key").GetString(), version = rows[0].GetProperty("version").GetString() },
                new { operation = "create", values = new { item = (string?)null } })))
        {
            var refusal = await Json(refused, HttpStatusCode.UnprocessableEntity);
            Assert.Equal(PublicSite.OrderLines, refusal.GetProperty("block").GetGuid());
            Assert.Equal(1, refusal.GetProperty("index").GetInt32());
        }
        Assert.Equal("Initech", await Scalar(site, $"SELECT customer FROM PUBLIC_ORDERS WHERE id = {key}"));
        Assert.Equal(2L, await Scalar(site, $"SELECT COUNT(*) FROM PUBLIC_ORDER_LINES WHERE order_id = {key}"));

        // The same update without the refused line: one commit for the order, the deletion and the new line.
        using (var updated = await Send(client, HttpMethod.Put, $"{form}/{key}?id={key}", token,
            Update("Initech Two", new { operation = "delete", key = rows[0].GetProperty("key").GetString(), version = rows[0].GetProperty("version").GetString() },
                new { operation = "create", values = new { item = "Washer" } })))
        {
            var saved = await Json(updated);
            Assert.Equal("Initech Two", saved.GetProperty("record").GetProperty("values").GetProperty("customer").GetString());
            Assert.Equal(2L, await Scalar(site, $"SELECT lines FROM PUBLIC_ORDERS WHERE id = {key}"));
            Assert.Equal(["Nut", "Washer"], saved.GetProperty("details")[0].GetProperty("list").GetProperty("rows").EnumerateArray()
                .Select(row => row.GetProperty("values").GetProperty("item").GetString()));
        }
        Assert.Equal(2L, await Scalar(site, $"SELECT COUNT(*) FROM PUBLIC_ORDER_LINES WHERE order_id = {key}"));
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string url, string token, object body)
    {
        var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("RequestVerificationToken", token);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{response.RequestMessage?.RequestUri}: {response.StatusCode} {text}");
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

    private static async Task<object?> Scalar(PublicSite site, string sql)
    {
        object? value = null;
        await site.WithServices(async services => {
            await using var connection = services.GetRequiredService<ConnectionCatalog>().Openable(ConnectionCatalog.ApplicationConnection);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            value = await command.ExecuteScalarAsync();
        });
        return value;
    }
}
