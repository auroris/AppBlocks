using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Services;
using AppBlocks.Blocks;
using AppBlocks.Forms;

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
        Assert.Equal(HttpStatusCode.NotFound, (await files.RemoveAsync(id, "id=5")).StatusCode);

        // Renaming needs write access and antiforgery, and keeps the same bytes and file identity.
        using (var noToken = new HttpRequestMessage(HttpMethod.Patch, $"{site.PathBase}/api/attachments/{BlockSite.FormPageKey}/{OrdersAttachments}/{id}?id=4") { Content = JsonContent.Create(new { fileName = "renamed.pdf" }) })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(noToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await files.RenameAsync(id, "renamed.pdf", "id=5")).Response.StatusCode);
        foreach (var invalid in new[] { null, "", "  ", ".", "..", "../a.pdf", @"..\a.pdf", "a\nb.pdf", "a:b.pdf", new string('a', 261) })
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await files.RenameAsync(id, invalid, "id=4")).Response.StatusCode);
        var renamed = await files.RenameAsync(id, "  résumé.pdf  ", "id=4");
        Assert.True(renamed.IsSuccess, renamed.Problem);
        Assert.Equal(id, renamed.Id);
        Assert.Equal("résumé.pdf", renamed.Body.GetProperty("fileName").GetString());
        Assert.Equal(uploaded.Body.GetProperty("sha256").GetString(), renamed.Body.GetProperty("sha256").GetString());
        Assert.False(renamed.Body.TryGetProperty("revisions", out _));
        using (var renamedDownload = await files.DownloadAsync(id, "id=4"))
        {
            Assert.Equal("résumé.pdf", renamedDownload.Content.Headers.ContentDisposition?.FileNameStar);
            Assert.Equal("first", await renamedDownload.Content.ReadAsStringAsync());
        }

        // A stored file is never replaced: nothing answers a write of new contents to it, and it keeps its own.
        using (var put = new HttpRequestMessage(HttpMethod.Put, $"{site.PathBase}/api/attachments/{BlockSite.FormPageKey}/{OrdersAttachments}/{id}?id=4") {
            Content = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes("second")), "file", "invoice-v2.pdf" } } })
            Assert.False((await client.SendAsync(put)).IsSuccessStatusCode);
        Assert.Equal("first", await (await files.DownloadAsync(id, "id=4")).Content.ReadAsStringAsync());

        // Removal takes the item out of the list; the administrator's purge deletes its rows for good.
        Assert.Equal(HttpStatusCode.NoContent, (await files.RemoveAsync(id, "id=4")).StatusCode);
        Assert.Empty((await files.ListAsync("id=4")).Items);
        Assert.Equal(HttpStatusCode.NotFound, (await files.DownloadAsync(id, "id=4")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await files.RenameAsync(id, "removed.pdf", "id=4")).Response.StatusCode);
        using var administrator = await site.SignInAsAdministratorAsync();
        var purged = await Json(await administrator.PostAsJsonAsync($"{site.PathBase}/umbraco/management/api/v1/attachments/purge", new { retentionDays = 0 }));
        Assert.Equal(1, purged.GetProperty("links").GetInt32());
        Assert.Equal(1, purged.GetProperty("files").GetInt32());
        using var editor = await EditorAsync(site);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PostAsJsonAsync($"{site.PathBase}/umbraco/management/api/v1/attachments/purge", new { retentionDays = 0 })).StatusCode);
        // The administrator policy guards the Settings screens' own data too; the pickers' lists stay open to every backoffice user.
        foreach (var screen in new[] { "appblocks/database-setup", "appblocks/definitions", "appblocks/forms", "appblocks/anti-spam" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync($"{site.PathBase}/umbraco/management/api/v1/{screen}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await administrator.GetAsync($"{site.PathBase}/umbraco/management/api/v1/{screen}")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await editor.GetAsync($"{site.PathBase}/umbraco/management/api/v1/appblocks/connections")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await editor.GetAsync($"{site.PathBase}/umbraco/management/api/v1/appblocks/database-kinds")).StatusCode);

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
        var smallId = (await small.ListAsync("id=4")).Items.Single().GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.RenameAsync(smallId, "renamed.txt", "id=4")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await small.RenameAsync(smallId, "tool.exe", "id=4")).Response.StatusCode);

        // A later site configuration can leave a published block with no compatible types.
        var settings = site.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AppBlocks.Attachments.AttachmentSettings>>().Value;
        settings.AllowedExtensions = [".docx"];
        foreach (var name in new[] { "file.docx", "file.txt", "file.exe" })
        {
            var refused = await small.UploadAsync(new(name, "application/octet-stream", Encoding.UTF8.GetBytes("x")), query: "id=4");
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, refused.Response.StatusCode);
            Assert.Contains("No file types are accepted here", refused.Problem);
        }
        Assert.Single((await files.ListAsync("id=4")).Items);
    }

    [Fact]
    public async Task Files_wait_under_the_submission_key_until_the_uploader_adopts_them_to_the_saved_record()
    {
        await using var site = await BlockSite.StartAsync();
        await site.WaitForPublishedAsync(BlockSite.FormPageKey);
        var block = site.AttachmentsBlock;
        using var client = await site.SignInMemberAsync(site.CreateMember("pending", "pending@example.invalid", "Pending member"));
        using var stranger = await site.SignInMemberAsync(site.CreateMember("stranger", "stranger@example.invalid", "Stranger"));
        var submission = Guid.NewGuid();
        var mine = new PendingFiles(client, $"{site.PathBase}/api/attachments/{BlockSite.FormPageKey}/{block}/new/{submission}");

        // The form has no record yet, so the page offers pending uploads; with a record it asks for the saved one.
        Assert.Contains("data-new-uploads=\"allowed\"", await client.GetStringAsync(site.PathBase + "/orders-form/"));
        Assert.Contains("data-new-uploads=\"save-first\"", await client.GetStringAsync(site.PathBase + "/orders-form/?id=4"));

        // The pending list answers as the record's list does, empty to start with.
        var (status, list) = await mine.ListAsync();
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(list.GetProperty("canWrite").GetBoolean());
        Assert.Empty(list.GetProperty("items").EnumerateArray());
        Assert.Equal("sample.orders", list.GetProperty("owner").GetProperty("alias").GetString());
        Assert.Equal(JsonValueKind.Null, list.GetProperty("owner").GetProperty("key").ValueKind);

        // Upload two, remove one, download the other.
        var (created, first) = await mine.UploadAsync("draft.pdf", "first draft");
        Assert.Equal(HttpStatusCode.Created, created);
        var (_, second) = await mine.UploadAsync("spare.pdf", "spare");
        Assert.Equal(HttpStatusCode.NoContent, await mine.RemoveAsync(second.GetProperty("id").GetInt64()));
        var kept = first.GetProperty("id").GetInt64();
        Assert.Equal([kept], (await mine.ListAsync()).Body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt64()));
        Assert.Equal("first draft", await client.GetStringAsync($"{site.PathBase}/api/attachments/{BlockSite.FormPageKey}/{block}/new/{submission}/{kept}"));
        var renamed = await mine.SendAsync(HttpMethod.Patch, "/" + kept, JsonContent.Create(new { fileName = "renamed-draft.pdf" }));
        Assert.Equal(HttpStatusCode.OK, renamed.Status);
        Assert.Equal("renamed-draft.pdf", renamed.Body.GetProperty("fileName").GetString());

        // Another member under the same submission key sees nothing of them, and may not adopt them to a record they can write.
        var theirs = new PendingFiles(stranger, $"{site.PathBase}/api/attachments/{BlockSite.FormPageKey}/{block}/new/{submission}");
        Assert.Empty((await theirs.ListAsync()).Body.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await theirs.SendAsync(HttpMethod.Patch, "/" + kept, JsonContent.Create(new { fileName = "stolen.pdf" }))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await theirs.AdoptAsync("4")).Status);
        // An anonymous visitor reaches no pending files of a class that refuses them.
        using (var anonymous = site.CreateClient())
            Assert.False((await new PendingFiles(anonymous, $"{site.PathBase}/api/attachments/{BlockSite.FormPageKey}/{block}/new/{submission}").ListAsync()).Status == HttpStatusCode.OK);

        // The uploader adopts them once the form has saved record 4: the answer is the record's list, under the same file id.
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await mine.SendAsync(HttpMethod.Post, "/adopt", new StringContent("4", Encoding.UTF8, "text/plain"))).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await mine.AdoptAsync(" ")).Status);
        var (adoptedStatus, adopted) = await mine.AdoptAsync("4", "?id=4");
        Assert.Equal(HttpStatusCode.OK, adoptedStatus);
        Assert.Equal("4", adopted.GetProperty("owner").GetProperty("key").GetString());
        Assert.Equal([kept], adopted.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt64()));
        Assert.Empty((await mine.ListAsync()).Body.GetProperty("items").EnumerateArray());
        Assert.Equal(kept, (await site.Attachments(client, BlockSite.FormPageKey, block).ListAsync("id=4")).Items.Single().GetProperty("id").GetInt64());
        // Adopting again moves nothing and still answers with the list.
        Assert.Equal(HttpStatusCode.OK, (await mine.AdoptAsync("4")).Status);
    }

    [Fact]
    public async Task A_members_only_owner_shows_the_sign_in_notice_and_an_open_one_takes_anonymous_pending_files()
    {
        await using var site = await BlockSite.BootAsync(configure: builder => {
            builder.Configuration["AppBlocks:ApiRoot"] = "/services";
            builder.Services.AddSingleton<IFormResource>(new FilesResource("files.members", requireMember: true));
            builder.Services.AddSingleton<IFormResource>(new FilesResource("files.public", requireMember: false));
            builder.Services.AddSingleton<IAppBlocksTextSource, FrenchSignIn>();
        });
        await site.PublishAsync();
        var (membersPage, membersFiles, membersRows) = await PublishFilesFormAsync(site, "Members files", "files.members", insideForm: false);
        var (publicPage, publicFiles, _) = await PublishFilesFormAsync(site, "Public files", "files.public", insideForm: true);
        using var anonymous = site.CreateClient();

        // The blocks name a class that takes Members only, so the anonymous visitor gets the notice, in the site's words where it has them.
        var html = await anonymous.GetStringAsync(site.PathBase + "/members-files/?id=1");
        Assert.Contains(FrenchSignIn.Text, html);
        Assert.Contains("Sign in to see the rows.", html);
        Assert.DoesNotContain("data-app-attachments", html);
        Assert.DoesNotContain("data-app-tabular", html);
        // The endpoints answer under the configured root, and refuse the visitor there as before.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{site.PathBase}/services/attachments/{membersPage}/{membersFiles}?id=1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{site.PathBase}/services/tabular/{membersPage}/{membersRows}?id=1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"{site.PathBase}/api/attachments/{membersPage}/{membersFiles}?id=1")).StatusCode);

        // An open class that lets anyone create a record and write its files takes an anonymous visitor's pending files.
        var page = await anonymous.GetStringAsync(site.PathBase + "/public-files/");
        Assert.Contains($"data-api=\"{site.PathBase}/services/attachments/{publicPage}/{publicFiles}\"", page);
        Assert.Contains("data-new-uploads=\"allowed\"", page);
        var pending = new PendingFiles(anonymous, $"{site.PathBase}/services/attachments/{publicPage}/{publicFiles}/new/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.OK, (await pending.ListAsync()).Status);
        var (created, file) = await pending.UploadAsync("form.pdf", "filled in");
        Assert.Equal(HttpStatusCode.Created, created);
        var (status, adopted) = await pending.AdoptAsync("1");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(file.GetProperty("id").GetInt64(), adopted.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task An_owner_can_let_a_caller_add_files_without_changing_the_ones_there()
    {
        await using var site = await BlockSite.BootAsync(configure: builder =>
            builder.Services.AddSingleton<IFormResource>(new FilesResource("files.added", requireMember: false, write: false, add: true)));
        await site.PublishAsync();
        var (page, block, _) = await PublishFilesFormAsync(site, "Added files", "files.added", insideForm: false);
        using var client = site.CreateClient();
        var files = site.Attachments(client, page, block);

        // The list says the caller may add and may not write, which is what the block shows: Upload, and no Rename or Remove.
        var list = await files.ListAsync("id=1");
        Assert.True(list.IsSuccess, list.Problem);
        Assert.False(list.CanWrite);
        Assert.True(list.Body.GetProperty("canAdd").GetBoolean());

        var added = await files.UploadAsync(Pdf("asked-for.pdf", "sent later"), query: "id=1");
        Assert.Equal(HttpStatusCode.Created, added.Response.StatusCode);
        // What is there stays as it is, the caller's own file included.
        var renamed = await files.RenameAsync(added.Id!.Value, "renamed.pdf", "id=1");
        Assert.Equal(HttpStatusCode.Forbidden, renamed.Response.StatusCode);
        Assert.Contains("add files", renamed.Problem);
        Assert.Equal(HttpStatusCode.Forbidden, (await files.RemoveAsync(added.Id.Value, "id=1")).StatusCode);
        Assert.Equal("asked-for.pdf", Assert.Single((await files.ListAsync("id=1")).Items).GetProperty("fileName").GetString());
    }

    [Fact]
    public async Task Files_sent_ahead_of_the_first_save_join_a_record_their_sender_may_only_add_to()
    {
        // Both classes let the visitor write a new record's files, and neither once it is saved; only the first lets them add then.
        await using var site = await BlockSite.BootAsync(configure: builder => {
            builder.Services.AddSingleton<IFormResource>(new FilesResource("files.asked", requireMember: false, add: true, writeNewOnly: true));
            builder.Services.AddSingleton<IFormResource>(new FilesResource("files.filed", requireMember: false, writeNewOnly: true));
        });
        await site.PublishAsync();
        var (askedPage, askedFiles, _) = await PublishFilesFormAsync(site, "Asked files", "files.asked", insideForm: true);
        var (filedPage, filedFiles, _) = await PublishFilesFormAsync(site, "Filed files", "files.filed", insideForm: true);
        using var anonymous = site.CreateClient();

        // While the record is new the visitor writes its files, a mistake included, which they may remove.
        var asked = new PendingFiles(anonymous, $"{site.PathBase}/api/attachments/{askedPage}/{askedFiles}/new/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.OK, (await asked.ListAsync()).Status);
        var (mistakeStatus, mistake) = await asked.UploadAsync("wrong.pdf", "not this one");
        Assert.Equal(HttpStatusCode.Created, mistakeStatus);
        Assert.Equal(HttpStatusCode.NoContent, await asked.RemoveAsync(mistake.GetProperty("id").GetInt64()));
        var (created, file) = await asked.UploadAsync("form.pdf", "filled in");
        Assert.Equal(HttpStatusCode.Created, created);
        // Adopting adds the files to the saved record and changes none there, so leave to add is enough.
        var (status, adopted) = await asked.AdoptAsync("1");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(adopted.GetProperty("canWrite").GetBoolean());
        Assert.True(adopted.GetProperty("canAdd").GetBoolean());
        Assert.Equal(file.GetProperty("id").GetInt64(), adopted.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetInt64());

        // A class that lets nobody add to the saved record keeps the files out of it, and they stay where they were sent.
        var filed = new PendingFiles(anonymous, $"{site.PathBase}/api/attachments/{filedPage}/{filedFiles}/new/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.OK, (await filed.ListAsync()).Status);
        Assert.Equal(HttpStatusCode.Created, (await filed.UploadAsync("form.pdf", "filled in")).Status);
        var (refused, problem) = await filed.AdoptAsync("1");
        Assert.Equal(HttpStatusCode.Forbidden, refused);
        Assert.Contains("may read these files", problem.GetProperty("detail").GetString());
        Assert.Single((await filed.ListAsync()).Body.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task A_class_that_adopts_files_as_it_creates_the_record_only_confirms_the_submission_afterwards()
    {
        var ledger = new SubmissionLedger();
        await using var site = await BlockSite.BootAsync(configure: builder => {
            builder.Services.AddSingleton(ledger);
            builder.Services.AddScoped<IFormResource, SubmittedFiles>();
        });
        await site.PublishAsync();
        var (page, block, _) = await PublishFilesFormAsync(site, "Submitted files", SubmittedFiles.Name, insideForm: true);
        using var anonymous = site.CreateClient();
        var html = await anonymous.GetStringAsync(site.PathBase + "/submitted-files/");
        var formApi = System.Text.RegularExpressions.Regex.Match(html, "data-app-form data-api=\"([^\"]+)\"").Groups[1].Value;
        var state = await anonymous.GetFromJsonAsync<JsonElement>(formApi);

        // The visitor attaches a file to the new record, and the class adopts it in the transaction that creates the record.
        var submission = Guid.NewGuid();
        var pending = new PendingFiles(anonymous, $"{site.PathBase}/api/attachments/{page}/{block}/new/{submission}");
        Assert.Equal(HttpStatusCode.OK, (await pending.ListAsync()).Status);
        var (uploaded, file) = await pending.UploadAsync("application.pdf", "filled in");
        Assert.Equal(HttpStatusCode.Created, uploaded);
        using var create = new HttpRequestMessage(HttpMethod.Post, formApi) { Content = JsonContent.Create(new {
            values = new { title = "Application" }, definitionVersion = state.GetProperty("definitionVersion").GetString(), checksum = (string?)null,
            submissionKey = submission.ToString(),
        }) };
        create.Headers.Add("RequestVerificationToken", state.GetProperty("antiforgeryToken").GetString());
        using var created = await anonymous.SendAsync(create);
        Assert.True(created.StatusCode == HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        Assert.Empty((await pending.ListAsync()).Body.GetProperty("items").EnumerateArray());

        // A file sent under the same key after the record was created stays where it was: the adoption moves nothing.
        Assert.Equal(HttpStatusCode.Created, (await pending.UploadAsync("late.pdf", "after the fact")).Status);
        var (status, adopted) = await pending.AdoptAsync("1", "?region=north");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(adopted.GetProperty("canWrite").GetBoolean());
        Assert.Equal(file.GetProperty("id").GetInt64(), Assert.Single(adopted.GetProperty("items").EnumerateArray()).GetProperty("id").GetInt64());
        Assert.Equal("late.pdf", Assert.Single((await pending.ListAsync()).Body.GetProperty("items").EnumerateArray()).GetProperty("fileName").GetString());
        // The class is asked with the page's parameters, as its other rules are.
        Assert.Equal("north", ledger.Parameters?["region"]);

        // Another submission is not this record's, which the class says, and nothing is listed.
        var other = new PendingFiles(anonymous, $"{site.PathBase}/api/attachments/{page}/{block}/new/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.OK, (await other.ListAsync()).Status);
        var (refused, problem) = await other.AdoptAsync("1");
        Assert.Equal(HttpStatusCode.Forbidden, refused);
        Assert.Contains("does not belong", problem.GetProperty("detail").GetString());
    }

    /// <summary>A page with an Attachments block and a Tabular form block whose records are <paramref name="resource"/>'s: inside a form over it with a title field, or on the page naming it as their owner with the key from the address.</summary>
    private static async Task<(Guid Page, Guid Files, Guid Rows)> PublishFilesFormAsync(BlockSite site, string name, string resource, bool insideForm)
    {
        var blocks = BlockPages.Empty();
        (string, string)[] owner = insideForm ? [] : [("ownerResource", resource), ("ownerKey", ":PARAM.id")];
        var files = BlockPages.Add(blocks, AppBlockSchema.AttachmentsAlias, [("heading", "Files"), .. owner]);
        var rows = BlockPages.Add(blocks, AppBlockSchema.TabularAlias, [("heading", "Lines"), ("dataSource", "orders.lines"), ("linkField", "orderId"), .. owner]);
        if (insideForm) BlockPages.Layout(blocks).Add(BlockPages.FormBlock(blocks, resource, [BlockPages.Add(blocks, "appText", ("field", "title")), files, rows], []));
        else { BlockPages.Layout(blocks).Add(files); BlockPages.Layout(blocks).Add(rows); }
        using var scope = site.CreateScope();
        var contents = scope.ServiceProvider.GetRequiredService<IContentService>();
#pragma warning disable CS0618
        var page = contents.Create(name, contents.GetById(BlockSite.FormPageKey)!.ParentId, BlockSite.PageAlias);
        page.SetValue(BlockSite.BodyAlias, blocks.ToJsonString());
        Assert.True(contents.Save(page).Success);
        var published = contents.Publish(page, ["*"]);
#pragma warning restore CS0618
        Assert.True(published.Success, string.Join("; ", published.EventMessages?.GetAll().Select(m => m.Message) ?? []));
        await site.WaitForPublishedAsync(page.Key);
        return (page.Key, Guid.Parse(files["contentKey"]!.GetValue<string>()), Guid.Parse(rows["contentKey"]!.GetValue<string>()));
    }

    /// <summary>
    /// A form class whose records anyone it admits may load and create, and attach files to as <paramref name="write"/> and <paramref name="add"/>
    /// say; with <paramref name="writeNewOnly"/>, writing is only for a record not saved yet.
    /// </summary>
    private sealed class FilesResource(string alias, bool requireMember, bool write = true, bool add = false, bool writeNewOnly = false)
        : IFormResource, AppBlocks.Attachments.IAttachmentOwner, AppBlocks.Attachments.IAddOnlyAttachmentOwner
    {
        public string Alias => alias;
        public string ContractVersion => "1";
        public bool RequireMember => requireMember;
        public IReadOnlyList<FormField> Fields => [new("title", "Title", "text")];
        private static Task<FormResult> Answer(string? key) => Task.FromResult(FormResult.Ok(new(key, "1", new Dictionary<string, object?> { ["title"] = null }, new(Create: key is null, Update: true))));
        public Task<FormResult> InitializeAsync(FormExecutionContext context, CancellationToken ct) => Answer(null);
        public Task<FormResult> LoadAsync(string key, FormExecutionContext context, CancellationToken ct) => Answer(key);
        public Task<FormResult> CreateAsync(JsonElement input, FormExecutionContext context, CancellationToken ct) => Answer("1");
        public Task<FormResult> UpdateAsync(string key, JsonElement input, string? checksum, FormExecutionContext context, CancellationToken ct) => Answer(key);
        public Task<FormResult> DeleteAsync(string key, string? checksum, FormExecutionContext context, CancellationToken ct) => Answer(key);
        public ValueTask<bool> MayWriteAttachmentsAsync(FormRecord record, FormExecutionContext context, CancellationToken ct)
            => ValueTask.FromResult(write && (!writeNewOnly || record.Key is null));
        public ValueTask<bool> MayAddAttachmentsAsync(FormRecord record, FormExecutionContext context, CancellationToken ct) => ValueTask.FromResult(add);
    }

    /// <summary>What <see cref="SubmittedFiles"/> keeps between requests: the submission each record was created from, and the parameters it was last asked with.</summary>
    private sealed class SubmissionLedger
    {
        public System.Collections.Concurrent.ConcurrentDictionary<string, Guid> Submissions { get; } = new();
        public IReadOnlyDictionary<string, string>? Parameters { get; set; }
    }

    /// <summary>
    /// An application whose files are filed when it is submitted: it adopts the visitor's pending files in the transaction that
    /// creates the record, lets nobody change them afterwards, and confirms a later adoption against the submission it recorded.
    /// </summary>
    private sealed class SubmittedFiles(AppBlocks.Attachments.AttachmentStore store, SubmissionLedger ledger)
        : IFormResource, AppBlocks.Attachments.IAttachmentOwner, AppBlocks.Attachments.ISubmittedAttachmentOwner
    {
        public const string Name = "files.submitted";
        public string Alias => Name;
        public string ContractVersion => "1";
        public IReadOnlyList<FormField> Fields => [new("title", "Title", "text")];
        private static Task<FormResult> Answer(string? key) => Task.FromResult(FormResult.Ok(new(key, "1", new Dictionary<string, object?> { ["title"] = null }, new(Create: key is null))));
        public Task<FormResult> InitializeAsync(FormExecutionContext context, CancellationToken ct) => Answer(null);
        public Task<FormResult> LoadAsync(string key, FormExecutionContext context, CancellationToken ct) => Answer(key);
        public async Task<FormResult> CreateAsync(JsonElement input, FormExecutionContext context, CancellationToken ct)
        {
            var submission = Guid.Parse(context.SubmissionKey!);
            await using var connection = await store.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            // The application's own row would be written here, on the same connection and transaction.
            await AppBlocks.Attachments.AttachmentStore.AdoptAsync(connection, transaction, AppBlocks.Attachments.AttachmentOwner.Pending(Name, submission, context.Member),
                new AppBlocks.Attachments.AttachmentOwner(Name, "1"), "applicant", ct);
            await transaction.CommitAsync(ct);
            ledger.Submissions["1"] = submission;
            return await Answer("1");
        }
        public Task<FormResult> UpdateAsync(string key, JsonElement input, string? checksum, FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Denied());
        public Task<FormResult> DeleteAsync(string key, string? checksum, FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Denied());
        public ValueTask<bool> MayWriteAttachmentsAsync(FormRecord record, FormExecutionContext context, CancellationToken ct) => ValueTask.FromResult(record.Key is null);
        public ValueTask<bool> ConfirmSubmissionAsync(string key, Guid submissionKey, FormExecutionContext context, CancellationToken ct)
        {
            ledger.Parameters = context.Parameters;
            return ValueTask.FromResult(ledger.Submissions.TryGetValue(key, out var saved) && saved == submissionKey);
        }
    }

    /// <summary>A site's own words for one text, as a dictionary-backed source would give them.</summary>
    private sealed class FrenchSignIn : IAppBlocksTextSource
    {
        public const string Text = "Connectez-vous pour voir les fichiers.";
        public string? Get(string id) => id == "attachmentsSignIn" ? Text : null;
        public IReadOnlyDictionary<string, string> All() => new Dictionary<string, string> { ["attachmentsSignIn"] = Text };
    }

    /// <summary>Drives the pending endpoints as the runtime does: the list's antiforgery token goes on every write.</summary>
    private sealed class PendingFiles(HttpClient client, string api)
    {
        private string? token;

        public async Task<(HttpStatusCode Status, JsonElement Body)> ListAsync()
        {
            var listed = await SendAsync(HttpMethod.Get, "", null);
            if (listed.Status == HttpStatusCode.OK) token = listed.Body.GetProperty("antiforgeryToken").GetString();
            return listed;
        }

        public Task<(HttpStatusCode Status, JsonElement Body)> UploadAsync(string name, string text)
        {
            var bytes = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
            bytes.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
            return SendAsync(HttpMethod.Post, "", new MultipartFormDataContent { { bytes, "file", name } });
        }

        public async Task<HttpStatusCode> RemoveAsync(long id) => (await SendAsync(HttpMethod.Delete, "/" + id, null)).Status;

        /// <param name="query">The page's query string, which block requests carry and the adoption ignores beside the body's key.</param>
        public Task<(HttpStatusCode Status, JsonElement Body)> AdoptAsync(string key, string query = "")
            => SendAsync(HttpMethod.Post, "/adopt" + query, JsonContent.Create(new { key }));

        public async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod method, string path, HttpContent? content)
        {
            using var request = new HttpRequestMessage(method, api + path) { Content = content };
            if (method != HttpMethod.Get)
                request.Headers.Add("RequestVerificationToken", token ?? throw new TestSiteException($"List {api} before writing to it."));
            using var response = await client.SendAsync(request);
            return (response.StatusCode, await Http.ReadJsonAsync(response));
        }
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
        var page = contents.Create("Limited attachments", contents.GetById(BlockSite.FormPageKey)!.ParentId, BlockSite.PageAlias);
        page.SetValue(BlockSite.BodyAlias, blocks.ToJsonString());
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
