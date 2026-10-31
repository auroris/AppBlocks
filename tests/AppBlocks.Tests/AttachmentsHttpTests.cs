using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Services;
using AppBlocks.Blocks;

namespace AppBlocks.Tests;

/// <summary>
/// Record attachments on a real site with AppBlocks alone: an Orders form's block through /api/attachments as a member, every
/// operation, the owner check, a read-only block, the limits, antiforgery and the administrator's purge.
/// </summary>
[Collection("Standalone Umbraco")]
public sealed class AttachmentsHttpTests
{
    [Fact]
    public async Task Files_follow_the_record_its_access_and_the_blocks_limits()
    {
        await using var site = await BlockSite.StartAsync();
        await site.WaitForPublishedAsync(BlockSite.FormPageKey);
        var OrdersAttachments = site.AttachmentsBlock;
        var member = site.CreateMember("files", "files@example.invalid", "Files member");
        using var client = await site.SignInMemberAsync(member);
        var files = site.Attachments(client, BlockSite.FormPageKey, OrdersAttachments);

        // Nobody signed in, no record yet, and a record that does not exist.
        using (var anonymous = site.CreateClient())
            Assert.Equal(HttpStatusCode.Forbidden, (await site.Attachments(anonymous, BlockSite.FormPageKey, OrdersAttachments).ListAsync("id=4")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await files.ListAsync()).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await files.ListAsync("id=999999")).Response.StatusCode);
        Assert.Throws<TestSiteException>(() => files.RemoveAsync(1, "id=4").GetAwaiter().GetResult()); // No list, no token.

        var list = await files.ListAsync("id=4");
        Assert.True(list.IsSuccess, list.Problem);
        Assert.True(list.CanWrite);
        Assert.Equal(["Invoice", "Delivery note", "Other"], list.Body.GetProperty("purposes").EnumerateArray().Select(p => p.GetString()!).ToArray());
        Assert.Empty(list.Items);

        // A write without the token is refused; a purpose the block does not offer too. Then the first file is filed.
        using (var request = new HttpRequestMessage(HttpMethod.Post, $"{site.PathBase}/api/attachments/{BlockSite.FormPageKey}/{OrdersAttachments}?id=4") { Content = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes("x")), "file", "x.pdf" } } })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await files.UploadAsync(Pdf("invoice.pdf", "first"), purpose: "Receipt", query: "id=4")).Response.StatusCode);
        var uploaded = await files.UploadAsync(Pdf("invoice.pdf", "first"), "Invoice", "October", "Paid", "id=4");
        Assert.Equal(HttpStatusCode.Created, uploaded.Response.StatusCode);
        var id = uploaded.Id!.Value;
        Assert.Equal("Invoice", uploaded.Body.GetProperty("purpose").GetString());
        Assert.Equal(5, uploaded.Body.GetProperty("size").GetInt64());
        Assert.Equal("Files member", uploaded.Body.GetProperty("uploadedBy").GetString());

        // The download carries the stored name and type and never renders in place.
        using var download = await files.DownloadAsync(id, "id=4");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/pdf", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("invoice.pdf", download.Content.Headers.ContentDisposition?.ToString());
        Assert.Equal("nosniff", download.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-store", download.Headers.CacheControl?.ToString());
        Assert.Equal("first", await download.Content.ReadAsStringAsync());
        // Another record's block never sees this file, whatever its id.
        Assert.Equal(HttpStatusCode.NotFound, (await files.DownloadAsync(id, "id=5")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await files.ReplaceAsync(id, Pdf("x.pdf", "x"), query: "id=5")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await files.RemoveAsync(id, "id=5")).StatusCode);

        // A replacement is a new revision; the earlier one stays readable under its own id.
        var replaced = await files.ReplaceAsync(id, Pdf("invoice-v2.pdf", "second"), label: "", query: "id=4");
        Assert.True(replaced.IsSuccess, replaced.Problem);
        var current = replaced.Id!.Value;
        Assert.NotEqual(id, current);
        Assert.Equal(JsonValueKind.Null, replaced.Body.GetProperty("label").ValueKind);
        Assert.Equal("Paid", replaced.Body.GetProperty("notes").GetString());
        Assert.Equal(id, replaced.Body.GetProperty("revisions").EnumerateArray().Single().GetProperty("id").GetInt64());
        Assert.Equal("first", await (await files.DownloadAsync(id, "id=4")).Content.ReadAsStringAsync());
        Assert.Equal("second", await (await files.DownloadAsync(current, "id=4")).Content.ReadAsStringAsync());
        Assert.Equal(current, (await files.ListAsync("id=4")).Items.Single().GetProperty("id").GetInt64());

        // Removal takes the item out of the list; the administrator's purge deletes its rows for good.
        Assert.Equal(HttpStatusCode.NoContent, (await files.RemoveAsync(current, "id=4")).StatusCode);
        Assert.Empty((await files.ListAsync("id=4")).Items);
        Assert.Equal(HttpStatusCode.NotFound, (await files.DownloadAsync(current, "id=4")).StatusCode);
        using var administrator = await site.SignInAsAdministratorAsync();
        var purged = await Json(await administrator.PostAsJsonAsync($"{site.PathBase}/umbraco/management/api/v1/attachments/purge", new { retentionDays = 0 }));
        Assert.Equal(1, purged.GetProperty("links").GetInt32());
        Assert.Equal(2, purged.GetProperty("files").GetInt32());
        using var editor = await EditorAsync(site);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PostAsJsonAsync($"{site.PathBase}/umbraco/management/api/v1/attachments/purge", new { retentionDays = 0 })).StatusCode);

        // A page whose blocks are read only, or tightly limited: uploads are refused by the block, by type and by size.
        var limited = await PublishLimitedPageAsync(site);
        var readOnly = site.Attachments(client, limited.Page, limited.ReadOnly);
        var readOnlyList = await readOnly.ListAsync("id=4");
        Assert.True(readOnlyList.IsSuccess, readOnlyList.Problem);
        Assert.False(readOnlyList.CanWrite);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.UploadAsync(new("a.txt", "text/plain", Encoding.UTF8.GetBytes("a")), query: "id=4")).Response.StatusCode);
        var small = site.Attachments(client, limited.Page, limited.Small);
        var smallList = await small.ListAsync("id=4");
        Assert.True(smallList.CanWrite);
        Assert.Equal(1024 * 1024, smallList.Body.GetProperty("maxFileSize").GetInt64());
        Assert.Equal(".txt,.pdf", smallList.Body.GetProperty("accept").GetString());
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await small.UploadAsync(new("tool.exe", "application/octet-stream", Encoding.UTF8.GetBytes("MZ")), query: "id=4")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await small.UploadAsync(new("big.txt", "text/plain", new byte[1024 * 1024 + 1]), query: "id=4")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await small.UploadAsync(new("ok.txt", "text/plain", Encoding.UTF8.GetBytes("fits")), query: "id=4")).Response.StatusCode);
        // The files belong to the record, so every block bound to it shows them.
        Assert.Single((await files.ListAsync("id=4")).Items);
        Assert.Single((await readOnly.ListAsync("id=4")).Items);
    }

    /// <summary>A copy of the Orders form page with two more Attachments blocks: one read only, one accepting 1 MB of .txt or .pdf.</summary>
    private static async Task<(Guid Page, Guid ReadOnly, Guid Small)> PublishLimitedPageAsync(BlockSite site)
    {
        var blocks = site.FormContent.DeepClone();
        var readOnly = Guid.NewGuid(); var small = Guid.NewGuid();
        var typeKey = AppBlockSchema.Key(AppBlockSchema.AttachmentsAlias).ToString();
        static JsonObject Value(string alias, string value) => new() { ["alias"] = alias, ["culture"] = null, ["editorAlias"] = null, ["segment"] = null, ["value"] = value };
        blocks["contentData"]!.AsArray().Add(new JsonObject { ["contentTypeKey"] = typeKey, ["key"] = readOnly.ToString(), ["values"] = new JsonArray(Value("heading", "Read only"), Value("readOnly", "1")) });
        blocks["contentData"]!.AsArray().Add(new JsonObject { ["contentTypeKey"] = typeKey, ["key"] = small.ToString(), ["values"] = new JsonArray(Value("heading", "Small"), Value("maxSize", "1"), Value("allowedTypes", ".txt .pdf")) });
        foreach (var key in new[] { readOnly, small })
        {
            blocks["expose"]!.AsArray().Add(new JsonObject { ["contentKey"] = key.ToString(), ["culture"] = null, ["segment"] = null });
            // The form's fields area is the first area of the page's one layout item.
            blocks["layout"]!["Umbraco.BlockGrid"]![0]!["areas"]![0]!["items"]!.AsArray().Add(new JsonObject {
                ["areas"] = new JsonArray(), ["columnSpan"] = 12, ["contentKey"] = key.ToString(), ["rowSpan"] = 1, ["settingsKey"] = null });
        }
        using var scope = site.CreateScope();
        var contents = scope.ServiceProvider.GetRequiredService<IContentService>();
#pragma warning disable CS0618
        var page = contents.Create("Limited attachments", contents.GetById(BlockSite.FormPageKey)!.ParentId, AppBlockSchema.PageAlias);
        page.SetValue(AppBlockSchema.BodyAlias, blocks.ToJsonString());
        contents.Save(page);
        var published = contents.Publish(page, ["*"]);
#pragma warning restore CS0618
        Assert.True(published.Success, string.Join(" ", published.EventMessages?.GetAll().Select(m => m.Message) ?? []));
        await site.WaitForPublishedAsync(page.Key);
        return (page.Key, readOnly, small);
    }

    /// <summary>A backoffice user who is not an Administrator, signed in to the management API.</summary>
    private static async Task<HttpClient> EditorAsync(BlockSite site)
    {
        var editor = await site.CreateUserAsync("files.editor", "files.editor@example.invalid", "Files editor");
        const string password = "Local-test-only-Editor91!";
        await site.SetPasswordAsync(editor, password);
        return await site.SignInManagementApiAsync(editor.Username, password);
    }

    private static AttachmentFile Pdf(string name, string text) => new(name, "application/pdf", Encoding.UTF8.GetBytes(text));

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
