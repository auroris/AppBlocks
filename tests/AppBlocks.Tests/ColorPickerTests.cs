using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AppBlocks.Blocks;
using AppBlocks.Forms;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Services;

namespace AppBlocks.Tests;

[Collection("Standalone Umbraco")]
public sealed class ColorPickerTests
{
    [Theory]
    [InlineData("text", true)]
    [InlineData("number", false)]
    [InlineData("checkbox", false)]
    [InlineData("date", false)]
    public void Color_picker_binds_to_text(string kind, bool compatible)
        => Assert.Equal(compatible, AppBlocksValidator.Compatible(AppBlockSchema.ColorAlias, new("color", "Color", kind)));

    [Fact]
    public async Task Color_picker_installs_renders_and_validates_through_form_endpoints()
    {
        var resource = new ColorResource();
        await using var site = await BlockSite.BootAsync(configure: builder => builder.Services.AddSingleton<IFormResource>(resource));
        await site.PublishAsync();
        Guid pageKey, formKey;
        using (var scope = site.CreateScope())
        {
            var types = scope.ServiceProvider.GetRequiredService<IContentTypeService>();
            var installed = types.Get(AppBlockSchema.ColorAlias);
            Assert.NotNull(installed);
            Assert.Equal(AppBlockSchema.Key(AppBlockSchema.ColorAlias), installed.Key);
            Assert.True(installed.IsElement);
            var contents = scope.ServiceProvider.GetRequiredService<IContentService>();
            var blocks = BlockPages.Empty();
            var required = BlockPages.Add(blocks, AppBlockSchema.ColorAlias, ("field", "required"), ("required", "1"), ("defaultValue", "#2a5d8f"));
            var optional = BlockPages.Add(blocks, AppBlockSchema.ColorAlias, ("field", "optional"), ("helpText", "Pick an accent color."));
            var readOnly = BlockPages.Add(blocks, AppBlockSchema.ColorAlias, ("field", "readOnly"), ("readOnly", "1"));
            var hidden = BlockPages.Add(blocks, AppBlockSchema.ColorAlias, ("field", "hidden"), ("hidden", "1"), ("defaultValue", "#ffffff"));
            var form = BlockPages.FormBlock(blocks, resource.Alias, [required, optional, readOnly, hidden], []);
            formKey = Guid.Parse(form["contentKey"]!.GetValue<string>());
            BlockPages.Layout(blocks).Add(form);
            var validator = ActivatorUtilities.CreateInstance<AppBlocksValidator>(scope.ServiceProvider);
            Assert.Empty(validator.ValidateEmbedded(blocks.ToJsonString()));
            var outside = BlockPages.Empty();
            BlockPages.Layout(outside).Add(BlockPages.Add(outside, AppBlockSchema.ColorAlias, ("field", "optional")));
            Assert.Contains(validator.ValidateEmbedded(outside.ToJsonString()), error => error.Contains("inside a form"));
            var page = contents.Create("Colors", contents.GetById(BlockSite.FormPageKey)!.ParentId, AppBlockSchema.PageAlias);
            page.SetValue(AppBlockSchema.BodyAlias, blocks.ToJsonString());
#pragma warning disable CS0618
            Assert.True(contents.Save(page).Success);
            var published = contents.Publish(page, ["*"]);
            Assert.True(published.Success, string.Join("; ", published.EventMessages?.GetAll().Select(m => m.Message) ?? []));
#pragma warning restore CS0618
            pageKey = page.Key;
        }
        await site.WaitForPublishedAsync(pageKey);
        using var client = site.CreateClient();
        var html = await client.GetStringAsync("/colors/");
        Assert.Equal(3, html.Split("data-app-color").Length - 1);
        Assert.Contains("type=\"color\"", html);
        Assert.Contains("pattern=\"#[0-9a-fA-F]{6}\"", html);
        Assert.Contains("data-default=\"#2a5d8f\"", html);
        Assert.Contains("data-color-clear", html);
        Assert.Contains("Pick an accent color.", html);
        Assert.Contains("data-field=\"readOnly\" data-kind=\"text\" data-readonly=\"true\"", html);
        Assert.Contains("name=\"hidden\" data-field=\"hidden\"", html);

        var api = $"/api/forms/{pageKey}/{formKey}";
        var state = await client.GetFromJsonAsync<JsonElement>(api);
        client.DefaultRequestHeaders.Add("RequestVerificationToken", state.GetProperty("antiforgeryToken").GetString());
        async Task<HttpResponseMessage> Send(object values, bool update = false) => await client.SendAsync(new HttpRequestMessage(update ? HttpMethod.Put : HttpMethod.Post, api + (update ? "/1" : "")) {
            Content = JsonContent.Create(new { values, definitionVersion = state.GetProperty("definitionVersion").GetString(), version = "1", submissionKey = Guid.NewGuid().ToString() }),
        });
        foreach (var color in new[] { "#000000", "#ffffff", "#2A5d8F" })
        {
            using var response = await Send(new { required = color, optional = color, hidden = color });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(color, resource.Saved["required"]);
            Assert.Equal(color, resource.Saved["hidden"]);
        }
        foreach (var invalid in new object[] { "red", "#fff", "123456", "#12345678", "#gg0000", " #123456", "#123456\n", " ", 123456, true, new[] { "#123456" } })
        {
            var before = resource.Writes;
            using var response = await Send(new { required = "#123456", optional = invalid });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal(before, resource.Writes);
            var error = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Contains("#RRGGBB", error.GetProperty("errors").GetProperty("optional")[0].GetString());
        }
        foreach (var empty in new string?[] { null, "" })
        {
            using (var response = await Send(new { required = empty })) Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            using (var response = await Send(new { required = "#123456", optional = empty })) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using (var response = await Send(new { optional = "#123456" })) Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using (var response = await Send(new { required = "#123456", readOnly = "#ffffff" })) Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using (var response = await Send(new { required = "#123456", hidden = "invalid" })) Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using (var response = await Send(new { optional = "#abcdef" }, update: true)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var response = await Send(new { optional = (string?)null }, update: true)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(resource.Saved["optional"]);
    }

    private sealed class ColorResource : IFormResource
    {
        public string Alias => "color.test";
        public string ContractVersion => "1";
        public IReadOnlyList<FormField> Fields => [new("required", "Primary color", "text", MaxLength: 7), new("optional", "Accent color", "text"), new("readOnly", "Fixed color", "text"), new("hidden", "Hidden color", "text")];
        public Dictionary<string, object?> Saved { get; private set; } = [];
        public int Writes { get; private set; }
        public Task<FormResult> InitializeAsync(FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Ok(new(null, null, new Dictionary<string, object?>(), new(Create: true))));
        public Task<FormResult> LoadAsync(string key, FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Ok(new(key, "1", Saved, new(Update: true))));
        public Task<FormResult> CreateAsync(JsonElement input, FormExecutionContext context, CancellationToken ct)
        {
            Writes++;
            Saved = input.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.GetString());
            return LoadAsync("1", context, ct);
        }
        public Task<FormResult> UpdateAsync(string key, JsonElement input, string? version, FormExecutionContext context, CancellationToken ct) => CreateAsync(input, context, ct);
        public Task<FormResult> DeleteAsync(string key, string? version, FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Denied());
    }
}
