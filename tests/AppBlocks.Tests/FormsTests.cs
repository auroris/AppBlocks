using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Services;
using AppBlocks.Forms;
using AppBlocks.Blocks;

namespace AppBlocks.Tests;

public sealed class FormsTests
{
    [Fact]
    public async Task Page_policies_preserve_the_hosts_existing_policy_provider()
    {
        var original = new TestPolicyProvider();
        var wrapper = new AppPagePolicyProvider(original);
        Assert.Same(original.Policy, await wrapper.GetPolicyAsync("existing-dynamic-policy"));
        Assert.Same(original.Policy, await wrapper.GetDefaultPolicyAsync());
        var page = Guid.NewGuid();
        var combined = await wrapper.GetPolicyAsync(AppPagePolicyProvider.Name(page, "existing-dynamic-policy"));
        Assert.Contains(combined!.Requirements, r => r is AppPageRequirement p && p.PageKey == page);
        Assert.All(original.Policy.Requirements, r => Assert.Contains(r, combined.Requirements));
    }

    private sealed class TestPolicyProvider : IAuthorizationPolicyProvider
    {
        public AuthorizationPolicy Policy { get; } = new AuthorizationPolicyBuilder().RequireClaim("host-permission").Build();
        public Task<AuthorizationPolicy?> GetPolicyAsync(string name) => Task.FromResult<AuthorizationPolicy?>(name == "existing-dynamic-policy" ? Policy : null);
        public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => Task.FromResult(Policy);
        public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => Task.FromResult<AuthorizationPolicy?>(null);
    }

    [Fact]
    public void Instance_names_distinguish_copied_pages_and_blocks()
    {
        var page = Guid.NewGuid(); var block = Guid.NewGuid();
        var name = AppBlockContent.BlockName(page, block);
        Assert.True(AppBlockContent.ParseBlockName(name, out var parsedPage, out var parsedBlock));
        Assert.Equal(page, parsedPage); Assert.Equal(block, parsedBlock);
        Assert.NotEqual(name, AppBlockContent.BlockName(Guid.NewGuid(), block));
        Assert.False(AppBlockContent.ParseBlockName("page-orders", out _, out _));
    }
}

/// <summary>What the form endpoints answer for themselves, around whatever the class decides.</summary>
[Collection("Standalone Umbraco")]
public sealed class FormEndpointHttpTests
{
    [Fact]
    public async Task A_delete_the_class_reports_as_done_without_a_record_is_answered_empty_and_a_body_that_is_not_JSON_is_refused()
    {
        var notes = new DeletingResource();
        await using var site = await BlockSite.BootAsync(configure: builder => builder.Services.AddSingleton<IFormResource>(notes));
        await site.PublishAsync();
        Guid pageKey, formKey;
        using (var scope = site.CreateScope())
        {
            var contents = scope.ServiceProvider.GetRequiredService<IContentService>();
            var blocks = BlockPages.Empty();
            var form = BlockPages.FormBlock(blocks, notes.Alias, [BlockPages.Add(blocks, "appText", ("field", "title"))], []);
            formKey = Guid.Parse(form["contentKey"]!.GetValue<string>());
            BlockPages.Layout(blocks).Add(form);
            var page = contents.Create("Notes", contents.GetById(BlockSite.FormPageKey)!.ParentId, BlockSite.PageAlias);
            page.SetValue(BlockSite.BodyAlias, blocks.ToJsonString());
#pragma warning disable CS0618
            Assert.True(contents.Save(page).Success);
            Assert.True(contents.Publish(page, ["*"]).Success);
#pragma warning restore CS0618
            pageKey = page.Key;
        }
        await site.WaitForPublishedAsync(pageKey);
        using var client = await site.SignInMemberAsync(site.CreateMember("notes.member", "notes.member@example.invalid", "Notes member"));
        var api = $"/api/forms/{pageKey}/{formKey}";
        var state = await client.GetFromJsonAsync<JsonElement>(api + "?id=1");
        HttpRequestMessage Delete(HttpContent content)
        {
            var request = new HttpRequestMessage(HttpMethod.Delete, api + "/1?id=1") { Content = content };
            request.Headers.Add("RequestVerificationToken", state.GetProperty("antiforgeryToken").GetString());
            return request;
        }

        // The runtime reads only an empty answer as "the record is gone and the form goes back".
        using (var deleted = await client.SendAsync(Delete(JsonContent.Create(new {
            values = new { }, definitionVersion = state.GetProperty("definitionVersion").GetString(), checksum = (string?)null, submissionKey = Guid.NewGuid().ToString(),
        }))))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            Assert.Empty(await deleted.Content.ReadAsByteArrayAsync());
        }
        Assert.Equal(1, notes.Deletes);
        // As the bound POST and PUT answer a body of another type, before the class is asked.
        using (var text = await client.SendAsync(Delete(new StringContent("not json", System.Text.Encoding.UTF8, "text/plain"))))
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, text.StatusCode);
        Assert.Equal(1, notes.Deletes);
    }

    /// <summary>A class whose delete reports success the way a class written to the old examples does: 200 and no record.</summary>
    private sealed class DeletingResource : IFormResource
    {
        public int Deletes { get; private set; }
        public string Alias => "notes.deleting";
        public string ContractVersion => "1";
        public IReadOnlyList<FormField> Fields => [new("title", "Title", "text")];
        private static Task<FormResult> Record(string? key) => Task.FromResult(FormResult.Ok(new(key, null, new Dictionary<string, object?> { ["title"] = "Note" }, new(Update: true, Delete: true))));
        public Task<FormResult> InitializeAsync(FormExecutionContext context, CancellationToken ct) => Record(null);
        public Task<FormResult> LoadAsync(string key, FormExecutionContext context, CancellationToken ct) => Record(key);
        public Task<FormResult> CreateAsync(JsonElement input, FormExecutionContext context, CancellationToken ct) => Record("1");
        public Task<FormResult> UpdateAsync(string key, JsonElement input, string? checksum, FormExecutionContext context, CancellationToken ct) => Record(key);
        public Task<FormResult> DeleteAsync(string key, string? checksum, FormExecutionContext context, CancellationToken ct) { Deletes++; return Task.FromResult(new FormResult(200)); }
    }
}
