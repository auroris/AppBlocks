using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Services;
using AppBlocks.Blocks;

namespace AppBlocks.Tests;

/// <summary>
/// Tabular forms on a real site with AppBlocks alone: an Orders form's order lines through /api/tabular as a member, the owner
/// check, the list's columns and new row, a batch of a create, an update and a delete applied together, batches
/// refused whole at the change that failed, the link to the order kept by the server, a retried submission, a second
/// page's copy of the block, the rendered frame, and publishing's check of the data source.
/// </summary>
[Collection("Standalone Umbraco")]
public sealed class TabularHttpTests
{
    private static readonly string[] Columns = ["item", "quantity", "unitPrice", "notes"];

    [Fact]
    public async Task Rows_follow_the_record_and_save_as_one_batch_applied_whole_or_not_at_all()
    {
        await using var site = await BlockSite.StartAsync();
        await site.WaitForPublishedAsync(BlockSite.FormPageKey);
        await site.WaitForPublishedAsync(BlockSite.WebsitePageKey);
        var member = site.CreateMember("lines", "lines@example.invalid", "Lines member");
        using var client = await site.SignInMemberAsync(member);
        var lines = site.Tabular(client, BlockSite.FormPageKey, BlockPages.LinesBlock);

        // Nobody signed in, no record yet, and a record that does not exist.
        using (var anonymous = site.CreateClient())
            Assert.Equal(HttpStatusCode.Forbidden, (await site.Tabular(anonymous, BlockSite.FormPageKey, BlockPages.LinesBlock).ListAsync("id=4")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await lines.ListAsync()).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await lines.ListAsync("id=999999")).Response.StatusCode);
        Assert.Throws<TestSiteException>(() => lines.SaveAsync([], "id=4").GetAwaiter().GetResult()); // No list, no token.

        // The seeded lines of order 4, in key order, with the block's four columns and what a new line starts from.
        var seeded = Keys(site, 4);
        Assert.Equal(2, seeded.Length);
        var list = await lines.ListAsync("id=4");
        Assert.True(list.IsSuccess, list.Problem);
        Assert.Equal(seeded, list.Rows.Select(r => r.GetProperty("key").GetString()).ToArray());
        Assert.True(list.CanAdd && list.CanUpdate && list.CanDelete);
        Assert.Equal("sample.orders", list.Body.GetProperty("owner").GetProperty("alias").GetString());
        Assert.Equal("4", list.Body.GetProperty("owner").GetProperty("key").GetString());
        Assert.Equal("orders.lines", list.Body.GetProperty("dataSource").GetString());
        Assert.Equal(50, list.Body.GetProperty("maxRows").GetInt32());
        Assert.Equal(Columns, list.Fields.Select(f => f.GetProperty("key").GetString()!).ToArray());
        var item = list.Fields[0];
        Assert.True(item.GetProperty("required").GetBoolean());
        Assert.Equal(200, item.GetProperty("maxLength").GetInt32());
        Assert.Equal(1m, list.Fields[1].GetProperty("minimum").GetDecimal());
        Assert.Equal(9999m, list.Fields[1].GetProperty("maximum").GetDecimal());
        Assert.All(list.Rows, row => Assert.Equal(Columns, row.GetProperty("values").EnumerateObject().Select(p => p.Name).ToArray()));
        Assert.Equal(Sample(site, $"SELECT ITEM FROM ORDER_LINES WHERE LINE_ID = {seeded[0]}"), list.Rows[0].GetProperty("values").GetProperty("item").GetString());
        var newRow = list.Body.GetProperty("newRow");
        Assert.Equal(JsonValueKind.Null, newRow.GetProperty("key").ValueKind);
        Assert.Equal(1m, Number(newRow, "quantity"));
        Assert.Equal(0m, Number(newRow, "unitPrice"));
        Assert.True(newRow.GetProperty("capabilities").GetProperty("create").GetBoolean());

        // A save without the token is refused before anything is read.
        using (var request = new HttpRequestMessage(HttpMethod.Post, $"{site.PathBase}/api/tabular/{BlockSite.FormPageKey}/{BlockPages.LinesBlock}?id=4")
        { Content = JsonContent.Create(new { submissionKey = Guid.NewGuid(), changes = new[] { new { operation = "create", values = new { item = "Tokenless" } } } }) })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);

        // One batch: a new line, a changed line and a deleted line, together.
        var (first, second) = (list.Rows[0], list.Rows[1]);
        var saved = await lines.SaveAsync([
            new("create", Values: new { item = "Gasket", quantity = "3", unitPrice = "0.50", notes = (string?)null }),
            new("update", Key(first), Checksum(first), new { quantity = "7" }),
            new("delete", Key(second), Checksum(second)),
        ], "id=4");
        Assert.True(saved.IsSuccess, saved.Body.ToString());
        Assert.Equal(2, saved.Rows.Count);
        Assert.Equal(Key(first), Key(saved.Rows[0]));
        Assert.Equal(7m, Number(saved.Rows[0], "quantity"));
        Assert.NotEqual(Checksum(first), Checksum(saved.Rows[0]));
        var created = saved.Rows[1];
        Assert.Equal("Gasket", created.GetProperty("values").GetProperty("item").GetString());
        Assert.Equal(0.5m, Number(created, "unitPrice"));
        Assert.False(string.IsNullOrEmpty(saved.Body.GetProperty("antiforgeryToken").GetString()));
        // The server linked the new line to order 4 and recorded who wrote it; the deleted line is gone.
        Assert.Equal(4L, Sample(site, $"SELECT ORDER_ID FROM ORDER_LINES WHERE LINE_ID = {Key(created)}"));
        Assert.Equal("lines", Sample(site, $"SELECT CREATED_BY FROM ORDER_LINES WHERE LINE_ID = {Key(created)}"));
        Assert.Equal("lines", Sample(site, $"SELECT UPDATED_BY FROM ORDER_LINES WHERE LINE_ID = {Key(first)}"));
        Assert.Equal(0L, Sample(site, $"SELECT COUNT(*) FROM ORDER_LINES WHERE LINE_ID = {Key(second)}"));
        Assert.Equal(new[] { Key(first), Key(created) }, Keys(site, 4));

        // A batch whose second change is invalid is refused at that change, and its first change is not written.
        var count = Sample(site, "SELECT COUNT(*) FROM ORDER_LINES");
        var invalid = await lines.SaveAsync([
            new("create", Values: new { item = "Never written" }),
            new("update", Key(first), Checksum(saved.Rows[0]), new { quantity = "0" }),
        ], "id=4");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.Response.StatusCode);
        Assert.Equal(1, invalid.Index);
        Assert.Contains("quantity", invalid.Errors.Keys);
        Assert.Equal(0L, Sample(site, "SELECT COUNT(*) FROM ORDER_LINES WHERE ITEM = 'Never written'"));
        Assert.Equal(count, Sample(site, "SELECT COUNT(*) FROM ORDER_LINES"));

        // A stale checksum is a conflict at its index; another order's line is not found here, at its index too.
        var stale = await lines.SaveAsync([new("update", Key(first), Checksum(first), new { quantity = "8" })], "id=4");
        Assert.Equal(HttpStatusCode.Conflict, stale.Response.StatusCode);
        Assert.Equal(0, stale.Index);
        Assert.Contains(stale.Errors[""], e => e.Contains("changed"));
        var foreign = Keys(site, 5)[0];
        var outside = await lines.SaveAsync([
            new("create", Values: new { item = "Never written" }),
            new("update", foreign, null, new { quantity = "2" }),
        ], "id=4");
        Assert.Equal(HttpStatusCode.NotFound, outside.Response.StatusCode);
        Assert.Equal(1, outside.Index);
        Assert.Equal(0L, Sample(site, "SELECT COUNT(*) FROM ORDER_LINES WHERE ITEM = 'Never written'"));
        Assert.Equal(HttpStatusCode.NotFound, (await lines.SaveAsync([new("delete", foreign)], "id=4")).Response.StatusCode);
        Assert.Equal(1L, Sample(site, $"SELECT COUNT(*) FROM ORDER_LINES WHERE LINE_ID = {foreign}"));

        // An operation that is not a create, an update or a delete is refused at its index and never deletes.
        var current = Sample(site, $"SELECT VERSION FROM ORDER_LINES WHERE LINE_ID = {Key(first)}")!.ToString();
        var unknown = await lines.SaveAsync([new("Update", Key(first), current, new { notes = "Cased" }), new("remove", Key(first), current)], "id=4");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.Response.StatusCode);
        Assert.Equal(1, unknown.Index);
        Assert.Equal(1L, Sample(site, $"SELECT COUNT(*) FROM ORDER_LINES WHERE LINE_ID = {Key(first)}"));

        // Only the block's columns can be sent: the link field is the server's and is dropped, any other field is refused.
        var relinked = await lines.SaveAsync([new("create", Values: new { item = "Washer", orderId = "5" })], "id=4");
        Assert.True(relinked.IsSuccess, relinked.Body.ToString());
        Assert.Equal(4L, Sample(site, "SELECT ORDER_ID FROM ORDER_LINES WHERE ITEM = 'Washer' AND CREATED_BY = 'lines'"));
        var foreignField = await lines.SaveAsync([new("create", Values: new { item = "Never written", customer = "Acme Corp" })], "id=4");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreignField.Response.StatusCode);
        Assert.Equal(0, foreignField.Index);
        Assert.Contains("customer", foreignField.Errors.Keys);

        // A retried submission answers with the rows and writes nothing again; the same key with other changes is refused.
        var retryKey = Guid.NewGuid().ToString();
        var checksum = Checksum((await lines.ListAsync("id=4")).Rows[0]);
        TabularChange[] retried = [new("update", Key(first), checksum, new { notes = "Checked twice" }), new("create", Values: new { item = "Pulley" })];
        var once = await lines.SaveAsync(retried, "id=4", retryKey);
        Assert.True(once.IsSuccess, once.Body.ToString());
        var rowsAfterOnce = Sample(site, "SELECT COUNT(*) FROM ORDER_LINES");
        var checksumAfterOnce = Checksum(once.Rows.Single(row => Key(row) == Key(first)));
        var twice = await lines.SaveAsync(retried, "id=4", retryKey);
        Assert.True(twice.IsSuccess, twice.Body.ToString());
        Assert.Equal(once.Rows.Select(Key), twice.Rows.Select(Key));
        Assert.Equal(rowsAfterOnce, Sample(site, "SELECT COUNT(*) FROM ORDER_LINES"));
        Assert.Equal(checksumAfterOnce, Checksum((await lines.ListAsync("id=4")).Rows.Single(row => Key(row) == Key(first))));
        Assert.Equal(1L, Sample(site, "SELECT COUNT(*) FROM ORDER_LINES WHERE ITEM = 'Pulley' AND CREATED_BY = 'lines'"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await lines.SaveAsync([new("create", Values: new { item = "Other" })], "id=4", retryKey)).Response.StatusCode);

        // Another page's block, inside its own Orders form, lists the same lines.
        var website = await site.Tabular(client, BlockSite.WebsitePageKey, BlockPages.LinesBlock).ListAsync("id=4");
        Assert.True(website.IsSuccess, website.Problem);
        Assert.Equal(Keys(site, 4), website.Rows.Select(Key).ToArray());

        // The page renders the frame and the column headings; the runtime and its stylesheet carry the block.
        using (var page = await site.GetPageAsync(client, "/orders-form/?id=4"))
        {
            var html = await page.Content.ReadAsStringAsync();
            Assert.True(page.IsSuccessStatusCode, html);
            Assert.Contains("data-app-tabular", html);
            Assert.Contains($"data-api=\"{site.PathBase}/api/tabular/{BlockSite.FormPageKey}/{BlockPages.LinesBlock}\"", html);
            Assert.Contains("data-owner=\"form\"", html);
            Assert.Contains("<th scope=\"col\" data-column=\"item\">Item *</th>", html);
            Assert.Contains("data-tabular-save", html);
        }
        Assert.Contains("[data-app-tabular]", await client.GetStringAsync(site.PathBase + "/App_Plugins/ApplicationBlocks/runtime.js"));
        Assert.Contains(".app-tabular-table", await client.GetStringAsync(site.PathBase + "/App_Plugins/ApplicationBlocks/runtime.css"));

        // Publishing judges the data source against the catalogue: a copy of the page naming one that does not exist is refused.
        var copy = site.FormContent.DeepClone();
        var block = copy["contentData"]!.AsArray().Single(b => b!["key"]!.GetValue<string>() == BlockPages.LinesBlock.ToString())!;
        BlockPages.SetValue(block, "dataSource", "orders.nowhere");
        using var scope = site.CreateScope();
        var contents = scope.ServiceProvider.GetRequiredService<IContentService>();
#pragma warning disable CS0618
        var draft = contents.Create("Lines without a source", contents.GetById(BlockSite.FormPageKey)!.ParentId, BlockSite.PageAlias);
        draft.SetValue(BlockSite.BodyAlias, copy.ToJsonString());
        contents.Save(draft);
        var published = contents.Publish(draft, ["*"]);
#pragma warning restore CS0618
        Assert.False(published.Success);
        Assert.Contains(published.EventMessages?.GetAll() ?? [], m => m.Message.Contains("'orders.nowhere' is not a published data source"));
    }

    private static string Key(JsonElement row) => row.GetProperty("key").GetString()!;
    /// <summary>A number cell's value, which the engine sends as invariant text so no precision is lost.</summary>
    private static decimal Number(JsonElement row, string column)
        => decimal.Parse(row.GetProperty("values").GetProperty(column).GetString()!, System.Globalization.CultureInfo.InvariantCulture);
    private static string Checksum(JsonElement row) => row.GetProperty("checksum").GetString()!;

    /// <summary>The keys of an order's lines, in key order.</summary>
    private static string[] Keys(BlockSite site, int order)
    {
        using var connection = site.OpenDatabase();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT LINE_ID FROM ORDER_LINES WHERE ORDER_ID = {order} ORDER BY LINE_ID";
        using var reader = command.ExecuteReader();
        var keys = new List<string>();
        while (reader.Read()) keys.Add(reader.GetInt64(0).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return [.. keys];
    }

    /// <summary>Runs SQL against the application database the orders live in.</summary>
    private static object? Sample(BlockSite site, string sql)
    {
        using var connection = site.OpenDatabase();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }
}
