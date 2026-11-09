using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AppBlocks.Blocks;
using AppBlocks.Connections;
using AppBlocks.DataSources;
using AppBlocks.Tabular;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace AppBlocks.Tests;

/// <summary>
/// A form and its Tabular form block saved in one request, on a real site whose form is a C# class: no transaction can span the class
/// and the data source, so the record is saved first and then its rows, and the answer says which rows failed. The rows of a record
/// not saved yet list from the block's /new endpoint, and every detail check is made before anything is written.
/// </summary>
[Collection("Standalone Umbraco")]
public sealed class MasterDetailHttpTests
{
    [Fact]
    public async Task A_form_on_a_csharp_class_saves_the_record_then_its_rows_and_names_the_rows_that_failed()
    {
        await using var site = await BlockSite.StartAsync();
        await site.WaitForPublishedAsync(BlockSite.FormPageKey);
        var member = site.CreateMember("details", "details@example.invalid", "Details member");
        using var client = await site.SignInMemberAsync(member);
        var api = $"{site.PathBase}/api/forms/{BlockSite.FormPageKey}/{FormKey(site.FormContent)}";
        var tabular = $"{site.PathBase}/api/tabular/{BlockSite.FormPageKey}/{BlockPages.LinesBlock}";

        // The page names the block, so the runtime can send its rows with the form.
        using (var page = await site.GetPageAsync(client, "/orders-form/"))
            Assert.Contains($"data-block=\"{BlockPages.LinesBlock}\"", await page.Content.ReadAsStringAsync());

        // A record not saved yet has no rows, and the block says what a new row starts from and under which revision.
        var fresh = await client.GetFromJsonAsync<JsonElement>(tabular + "/new");
        Assert.Empty(fresh.GetProperty("rows").EnumerateArray());
        Assert.True(fresh.GetProperty("canAdd").GetBoolean());
        Assert.Equal(JsonValueKind.Null, fresh.GetProperty("owner").GetProperty("key").ValueKind);
        Assert.Equal("1", fresh.GetProperty("newRow").GetProperty("values").GetProperty("quantity").GetString());
        var revision = fresh.GetProperty("revision").GetString()!;
        Assert.StartsWith("tabular:", revision);
        using (var anonymous = site.CreateClient())
            Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.GetAsync(tabular + "/new")).StatusCode);
        // A form that cannot start a record answers with its own status and reason, not as a record still to save.
        using (var offline = await client.GetAsync(tabular + "/new?unavailable=1"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, offline.StatusCode);
            Assert.Contains("Orders are offline.", await offline.Content.ReadAsStringAsync());
        }

        var state = await client.GetFromJsonAsync<JsonElement>(api);
        var token = state.GetProperty("antiforgeryToken").GetString()!;
        var definitionVersion = state.GetProperty("definitionVersion").GetString()!;
        Task<HttpResponseMessage> Send(HttpMethod method, string url, object values, string? checksum, params object[] details)
        {
            var request = new HttpRequestMessage(method, url) {
                Content = JsonContent.Create(new { values, definitionVersion, checksum, submissionKey = Guid.NewGuid().ToString(), details }),
            };
            request.Headers.Add("RequestVerificationToken", token);
            return client.SendAsync(request);
        }
        object Detail(object[] changes, string? revisionSent = null, Guid? block = null)
            => new { block = block ?? BlockPages.LinesBlock, revision = revisionSent ?? revision, submissionKey = Guid.NewGuid().ToString(), changes };

        // A new record with two rows: the record first, then its rows, linked to its new key.
        using var created = await Send(HttpMethod.Post, api, new { customer = "Initech", region = "NORTH", status = "NEW", amount = "10", orderDate = "2026-11-02" }, null,
            Detail([new { operation = "create", values = new { item = "Gear", quantity = "2" } }, new { operation = "create", values = new { item = "Belt", orderId = "4" } }]));
        var body = await Body(created);
        Assert.True(created.StatusCode == HttpStatusCode.OK, body.ToString());
        var key = body.GetProperty("record").GetProperty("key").GetString()!;
        var detail = Assert.Single(body.GetProperty("details").EnumerateArray());
        Assert.Equal(BlockPages.LinesBlock, detail.GetProperty("block").GetGuid());
        Assert.Equal(key, detail.GetProperty("list").GetProperty("owner").GetProperty("key").GetString());
        Assert.Equal(["Gear", "Belt"], detail.GetProperty("list").GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("values").GetProperty("item").GetString()));
        Assert.Equal(2L, Scalar(site, $"SELECT COUNT(*) FROM ORDER_LINES WHERE ORDER_ID = {key}"));

        // A row refused after the record was saved: the record stays, and the answer carries it with the row that failed.
        using var partial = await Send(HttpMethod.Put, $"{api}/{key}?id={key}", new { customer = "Initech Two" }, "1",
            Detail([new { operation = "create", values = new { item = "Chain" } }, new { operation = "create", values = new { quantity = "2" } }]));
        body = await Body(partial);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, partial.StatusCode);
        Assert.Equal(BlockPages.LinesBlock, body.GetProperty("block").GetGuid());
        Assert.Equal(1, body.GetProperty("index").GetInt32());
        Assert.True(body.GetProperty("errors").TryGetProperty("item", out _), body.ToString());
        Assert.Equal("Initech Two", body.GetProperty("record").GetProperty("values").GetProperty("customer").GetString());
        Assert.Equal("Initech Two", Scalar(site, $"SELECT CUSTOMER FROM ORDERS WHERE ORDER_ID = {key}"));
        Assert.Equal(2L, Scalar(site, $"SELECT COUNT(*) FROM ORDER_LINES WHERE ORDER_ID = {key}"));

        // Checked before anything is written: a stale revision, a block that is not the form's, a block sent twice, a column the block does not show.
        foreach (var (details, status) in new (object[] Details, HttpStatusCode Status)[] {
            ([Detail([new { operation = "create", values = new { item = "Stale" } }], "tabular:stale")], HttpStatusCode.Conflict),
            ([Detail([new { operation = "create", values = new { item = "Elsewhere" } }], block: Guid.NewGuid())], HttpStatusCode.UnprocessableEntity),
            ([Detail([]), Detail([])], HttpStatusCode.UnprocessableEntity),
            ([Detail([new { operation = "create", values = new { item = "Keyed", lineId = "1" } }])], HttpStatusCode.UnprocessableEntity),
        })
        {
            using var refused = await Send(HttpMethod.Put, $"{api}/{key}?id={key}", new { customer = "Never" }, "1", details);
            Assert.Equal(status, refused.StatusCode);
            Assert.True((await Body(refused)).TryGetProperty("block", out _));
        }
        Assert.Equal("Initech Two", Scalar(site, $"SELECT CUSTOMER FROM ORDERS WHERE ORDER_ID = {key}"));

        // A class that refuses the record leaves its rows unsaved.
        using (var refused = await Send(HttpMethod.Put, $"{api}/{key}?id={key}", new { customer = "Refused" }, "1", Detail([new { operation = "create", values = new { item = "Never" } }])))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.False((await Body(refused)).TryGetProperty("block", out _));
        }
        Assert.Equal(2L, Scalar(site, $"SELECT COUNT(*) FROM ORDER_LINES WHERE ORDER_ID = {key}"));

        // The block's own endpoint still saves rows on their own, for a block whose form cannot save them with its record.
        var lines = site.Tabular(client, BlockSite.FormPageKey, BlockPages.LinesBlock);
        var list = await lines.ListAsync($"id={key}");
        Assert.True(list.IsSuccess, list.Problem);
        Assert.Equal(revision, list.Body.GetProperty("revision").GetString());
        var gear = list.Rows[0];
        var saved = await lines.SaveAsync([new("update", gear.GetProperty("key").GetString(), gear.GetProperty("checksum").GetString(), new { quantity = "6" })], $"id={key}");
        Assert.True(saved.IsSuccess, saved.Body.ToString());
        Assert.Equal(6L, Scalar(site, $"SELECT QUANTITY FROM ORDER_LINES WHERE ORDER_ID = {key} AND ITEM = 'Gear'"));
    }

    [Fact]
    public async Task A_form_on_a_published_data_source_saves_the_record_and_its_rows_in_one_transaction()
    {
        await using var site = await BlockSite.StartAsync();
        var linesBlock = Guid.NewGuid(); var ownedBlock = Guid.NewGuid();
        Guid pageKey, formKey;
        using (var scope = site.CreateScope())
        {
            var contents = scope.ServiceProvider.GetRequiredService<IContentService>();
            var root = contents.GetRootContent().Single(c => c.ContentType.Alias == DataSourceSchema.RootAlias);
            var records = contents.Create("Order records", root.Id, DataSourceSchema.SqlAlias);
            records.SetValue("access", "Public"); records.SetValue("alias", "orders.records"); records.SetValue("connection", ConnectionCatalog.ApplicationConnection);
            records.SetValue("source", "ORDERS"); records.SetValue("keyColumns", "ORDER_ID");
            records.SetValue("allowInsert", 1); records.SetValue("allowUpdate", 1); records.SetValue("allowDelete", 1);
            Publish(contents, records);
        }
        using (var scope = site.CreateScope())
        {
            var contents = scope.ServiceProvider.GetRequiredService<IContentService>();
            var blocks = BlockPages.Empty();
            JsonNode Keyed(JsonNode layout, Guid key)
            {
                var block = blocks["contentData"]!.AsArray()[^1]!;
                block["key"] = key.ToString(); blocks["expose"]!.AsArray()[^1]!["contentKey"] = key.ToString(); layout["contentKey"] = key.ToString();
                return layout;
            }
            // The form's own rows, and rows that name their owner by a field of the form's record, in another letter case than the field's.
            var lines = Keyed(BlockPages.Add(blocks, AppBlockSchema.TabularAlias, ("dataSource", "orders.lines"), ("linkField", "orderId"), ("fields", "item, quantity")), linesBlock);
            var owned = Keyed(BlockPages.Add(blocks, AppBlockSchema.TabularAlias, ("dataSource", "orders.lines"), ("linkField", "orderId"), ("fields", "item"),
                (TabularBlock.OwnerResourceAlias, "orders.records"), (TabularBlock.OwnerKeyAlias, ":FIELD.ORDERID"), ("readOnly", "1")), ownedBlock);
            var form = BlockPages.FormBlock(blocks, "orders.records", [BlockPages.Add(blocks, "appText", ("field", "customer")), lines, owned], []);
            formKey = Guid.Parse(form["contentKey"]!.GetValue<string>());
            BlockPages.Layout(blocks).Add(form);
            var page = contents.Create("Order records", contents.GetById(BlockSite.FormPageKey)!.ParentId, BlockSite.PageAlias);
            page.SetValue(BlockSite.BodyAlias, blocks.ToJsonString());
            Publish(contents, page);
            pageKey = page.Key;
        }
        await site.WaitForPublishedAsync(pageKey);
        var member = site.CreateMember("records", "records@example.invalid", "Records member");
        using var client = await site.SignInMemberAsync(member);
        var api = $"{site.PathBase}/api/forms/{pageKey}/{formKey}";

        using (var page = await site.GetPageAsync(client, "/order-records/"))
            Assert.Contains($"data-api=\"{api}\"", await page.Content.ReadAsStringAsync());
        var state = await client.GetFromJsonAsync<JsonElement>(api);
        var token = state.GetProperty("antiforgeryToken").GetString()!;
        var definitionVersion = state.GetProperty("definitionVersion").GetString()!;
        var revision = (await client.GetFromJsonAsync<JsonElement>($"{site.PathBase}/api/tabular/{pageKey}/{linesBlock}/new")).GetProperty("revision").GetString()!;
        Task<HttpResponseMessage> Create(object values, params object[] changes)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, api) { Content = JsonContent.Create(new {
                values, definitionVersion, checksum = (string?)null, submissionKey = Guid.NewGuid().ToString(),
                details = new[] { new { block = linesBlock, revision, submissionKey = Guid.NewGuid().ToString(), changes } },
            }) };
            request.Headers.Add("RequestVerificationToken", token);
            return client.SendAsync(request);
        }

        using var created = await Create(new { customer = "Hooli" },
            new { operation = "create", values = new { item = "Gear", quantity = "2" } }, new { operation = "create", values = new { item = "Belt" } });
        var body = await Body(created);
        Assert.True(created.StatusCode == HttpStatusCode.OK, body.ToString());
        var key = body.GetProperty("record").GetProperty("key").GetString()!;
        Assert.Equal("Hooli", Scalar(site, $"SELECT CUSTOMER FROM ORDERS WHERE ORDER_ID = {key}"));
        Assert.Equal(2L, Scalar(site, $"SELECT COUNT(*) FROM ORDER_LINES WHERE ORDER_ID = {key}"));
        Assert.Equal(key, Assert.Single(body.GetProperty("details").EnumerateArray()).GetProperty("list").GetProperty("owner").GetProperty("key").GetString());

        // A row refused inside the shared transaction takes the record with it, which no save on a C# class can do.
        var orders = Scalar(site, "SELECT COUNT(*) FROM ORDERS");
        using var refused = await Create(new { customer = "Rolled back" },
            new { operation = "create", values = new { item = "Spoke" } }, new { operation = "create", values = new { quantity = "2" } });
        body = await Body(refused);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(linesBlock, body.GetProperty("block").GetGuid());
        Assert.Equal(1, body.GetProperty("index").GetInt32());
        Assert.Equal(orders, Scalar(site, "SELECT COUNT(*) FROM ORDERS"));
        Assert.Equal(0L, Scalar(site, "SELECT COUNT(*) FROM ORDER_LINES WHERE ITEM = 'Spoke'"));

        // :FIELD.ORDERID finds the record's orderId field whatever its letter case, as publishing accepted it.
        var named = await site.Tabular(client, pageKey, ownedBlock).ListAsync("id=4");
        Assert.True(named.IsSuccess, named.Problem);
        Assert.Equal(["Bearing", "Sprocket"], named.Rows.Select(row => row.GetProperty("values").GetProperty("item").GetString()));
    }

    private static void Publish(IContentService contents, IContent content)
    {
#pragma warning disable CS0618
        Assert.True(contents.Save(content).Success);
        var published = contents.Publish(content, ["*"]);
        Assert.True(published.Success, content.Name + ": " + string.Join("; ", published.EventMessages?.GetAll().Select(m => m.Message) ?? []));
#pragma warning restore CS0618
    }

    private static async Task<JsonElement> Body(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return text.Length == 0 ? default : JsonSerializer.Deserialize<JsonElement>(text);
    }

    /// <summary>The key of the form block in a page's blocks.</summary>
    private static Guid FormKey(JsonNode content) => Guid.Parse(content["contentData"]!.AsArray()
        .Single(block => block!["contentTypeKey"]!.GetValue<string>() == AppBlockSchema.Key("appForm").ToString())!["key"]!.GetValue<string>());

    private static object? Scalar(BlockSite site, string sql)
    {
        using DbConnection connection = site.OpenDatabase();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
