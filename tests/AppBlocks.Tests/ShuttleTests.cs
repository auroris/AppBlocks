using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AppBlocks.Blocks;
using AppBlocks.Connections;
using AppBlocks.DataSources;
using AppBlocks.Forms;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Services;

namespace AppBlocks.Tests;

[Collection("Standalone Umbraco")]
public sealed class ShuttleTests
{
    [Theory]
    [InlineData("")]
    [InlineData("third,first,second")]
    [InlineData("\"a,b\",\"say \"\"yes\"\"\", plain ")]
    [InlineData("é,東京,A,a")]
    public void Csv_round_trips_in_order(string csv)
    {
        Assert.True(ShuttleValue.TryParse(csv, out var values));
        Assert.Equal(csv, ShuttleValue.Serialize(values));
    }

    [Theory]
    [InlineData("a,a")]
    [InlineData("a,\"a\"")]
    [InlineData("a,")]
    [InlineData(",a")]
    [InlineData("\"\"")]
    [InlineData(" ")]
    [InlineData("a\nb")]
    [InlineData("\"a\rb\"")]
    [InlineData("\"unclosed")]
    [InlineData("a\"b")]
    [InlineData("\"a\"b")]
    public void Csv_rejects_ambiguous_or_multiline_values(string csv) => Assert.False(ShuttleValue.TryParse(csv, out _));

    [Fact]
    public void Shuttle_requires_a_text_field_with_choices_or_a_list()
    {
        Assert.True(AppBlocksValidator.Compatible(AppBlockSchema.ShuttleAlias, new("x", "X", "text"), true));
        Assert.True(AppBlocksValidator.Compatible(AppBlockSchema.ShuttleAlias, new("x", "X", "text", Choices: [new("a", "A")])));
        Assert.False(AppBlocksValidator.Compatible(AppBlockSchema.ShuttleAlias, new("x", "X", "number"), true));
        Assert.False(AppBlocksValidator.Compatible(AppBlockSchema.ShuttleAlias, new("x", "X", "text")));
        Assert.Throws<ArgumentException>(() => ShuttleValue.Serialize(["a", "a"]));
        Assert.Throws<ArgumentException>(() => ShuttleValue.Serialize(["a\nb"]));
    }

    [Fact]
    public async Task Shuttle_installs_renders_and_validates_ordered_csv_through_real_form_endpoints()
    {
        var resource = new ShuttleResource();
        await using var site = await BlockSite.BootAsync(configure: builder => builder.Services.AddSingleton<IFormResource>(resource));
        await site.PublishAsync();
        Guid pageKey, formKey;
        using (var scope = site.CreateScope())
        {
            var contents = scope.ServiceProvider.GetRequiredService<IContentService>();
            var parent = contents.GetById(BlockSite.FormPageKey)!.ParentId;
#pragma warning disable CS0618
            var sources = contents.GetRootContent().Single(content => content.ContentType.Alias == DataSourceSchema.RootAlias);
            var list = contents.Create("Shuttle orders", sources.Id, PublishedDataCatalog.ListAlias);
            list.SetValue("alias", "shuttle.orders");
            list.SetValue("connection", ConnectionCatalog.ApplicationConnection);
            list.SetValue("sql", "SELECT ORDER_ID, CUSTOMER FROM ORDERS WHERE CUSTOMER <> 'Hidden'");
            list.SetValue("valueColumn", "ORDER_ID"); list.SetValue("displayColumn", "CUSTOMER");
            Assert.True(contents.Save(list).Success);
            Assert.True(contents.Publish(list, ["*"]).Success);
            var blocks = BlockPages.Empty();
            var items = BlockPages.Add(blocks, AppBlockSchema.ShuttleAlias, ("field", "items"), ("required", "1"), ("defaultValue", "b,a"));
            var orders = BlockPages.Add(blocks, AppBlockSchema.ShuttleAlias, ("field", "orders"), ("listOfValues", "shuttle.orders"));
            var form = BlockPages.FormBlock(blocks, resource.Alias, [items, orders], []);
            formKey = Guid.Parse(form["contentKey"]!.GetValue<string>());
            BlockPages.Layout(blocks).Add(form);
            var validator = ActivatorUtilities.CreateInstance<AppBlocksValidator>(scope.ServiceProvider);
            await validator.LoadAsync();
            Assert.Empty(validator.Validate(blocks.ToJsonString()));
            var page = contents.Create("Shuttle", parent, BlockSite.PageAlias);
            page.SetValue(BlockSite.BodyAlias, blocks.ToJsonString());
            Assert.True(contents.Save(page).Success);
            var published = contents.Publish(page, ["*"]);
            Assert.True(published.Success, string.Join("; ", published.EventMessages?.GetAll().Select(m => m.Message) ?? []));
            pageKey = page.Key;
#pragma warning restore CS0618
        }
        await site.WaitForPublishedAsync(pageKey);
        using var client = await site.SignInMemberAsync(site.CreateMember("shuttle-reader", "shuttle@example.test", "Shuttle reader"));
        var html = await client.GetStringAsync("/shuttle/");
        Assert.Contains("data-app-shuttle", html);
        Assert.Contains("data-default=\"b,a\"", html);
        Assert.Contains("data-kind=\"shuttle\"", html);
        Assert.Contains("Selected (in order)", html);
        Assert.Contains("Acme Corp", html);
        Assert.Contains("data-shuttle-action=\"up\"", html);
        var api = $"/api/forms/{pageKey}/{formKey}";
        var state = await client.GetFromJsonAsync<JsonElement>(api);
        client.DefaultRequestHeaders.Add("RequestVerificationToken", state.GetProperty("antiforgeryToken").GetString());
        async Task<HttpResponseMessage> Send(object values, bool update = false) => await client.SendAsync(new HttpRequestMessage(update ? HttpMethod.Put : HttpMethod.Post, api + (update ? "/1" : "")) {
            Content = JsonContent.Create(new { values, definitionVersion = state.GetProperty("definitionVersion").GetString(), checksum = "1", submissionKey = Guid.NewGuid().ToString() }),
        });
        const string csv = "b,\"a,b\",\"say \"\"yes\"\"\",a";
        using (var created = await Send(new { items = csv, orders = "5,4" }))
        {
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            Assert.Equal(csv, resource.Saved["items"]);
            Assert.Equal("5,4", resource.Saved["orders"]);
        }
        foreach (var invalid in new[] { "a,a", "a,", "\"bad", "unknown", "a\nb", "\n", "", "a,unknown" })
        {
            using var response = await Send(new { items = invalid });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        }
        using (var response = await Send(new { items = new[] { "a", "b" } })) Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using (var response = await Send(new { items = "a", orders = "4,999" })) Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using (var connection = site.OpenDatabase())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE ORDERS SET CUSTOMER = 'Hidden' WHERE ORDER_ID = 4";
            command.ExecuteNonQuery();
        }
        // An already selected retired value survives reordering, while a new record cannot select it.
        using (var response = await Send(new { items = csv, orders = "4,5" }, update: true)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var response = await Send(new { items = "a", orders = "4" })) Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using (var response = await Send(new { items = "a", orders = "" }, update: true)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("", resource.Saved["orders"]);
    }

    private sealed class ShuttleResource : IFormResource
    {
        public string Alias => "shuttle.test";
        public string ContractVersion => "1";
        public IReadOnlyList<FormField> Fields => [new("items", "Items", "text", MaxLength: 100, Choices: [new("a", "Alpha"), new("b", "Beta"), new("a,b", "Comma"), new("say \"yes\"", "Quote")]), new("orders", "Orders", "text")];
        public Dictionary<string, object?> Saved { get; private set; } = [];
        public Task<FormResult> InitializeAsync(FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Ok(new(null, null, new Dictionary<string, object?>(), new(Create: true))));
        public Task<FormResult> LoadAsync(string key, FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Ok(new(key, "1", Saved, new(Update: true))));
        public Task<FormResult> CreateAsync(JsonElement input, FormExecutionContext context, CancellationToken ct) { Saved = input.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.GetString()); return LoadAsync("1", context, ct); }
        public Task<FormResult> UpdateAsync(string key, JsonElement input, string? checksum, FormExecutionContext context, CancellationToken ct) => CreateAsync(input, context, ct);
        public Task<FormResult> DeleteAsync(string key, string? checksum, FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Denied());
    }
}
