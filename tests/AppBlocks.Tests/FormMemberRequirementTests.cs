using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using AppBlocks.Blocks;
using AppBlocks.Forms;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Services;

namespace AppBlocks.Tests;

/// <summary><see cref="IFormResource.RequireMember"/>: who reaches a form class that asks for a signed-in Member, and what everyone else is told.</summary>
[Collection("Standalone Umbraco")]
public sealed class FormMemberRequirementTests
{
    private static ClaimsPrincipal Signed(string scheme) => new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "1")], scheme));

    [Fact]
    public void A_member_only_class_refuses_a_visitor_with_401_and_a_backoffice_user_alone_with_403()
    {
        var member = Signed(IdentityConstants.ApplicationScheme);
        var user = Signed(Umbraco.Cms.Core.Constants.Security.BackOfficeAuthenticationType);
        var required = new MemberResource(requireMember: true);
        Assert.Equal(401, FormResourceAccess.Refusal(required, AppPageAccess.Anonymous));
        Assert.Equal(403, FormResourceAccess.Refusal(required, user));
        Assert.Null(FormResourceAccess.Refusal(required, member));
        Assert.Null(FormResourceAccess.Refusal(required, new ClaimsPrincipal(member.Identities.Concat(user.Identities))));
        var open = new MemberResource(requireMember: false);
        Assert.Null(FormResourceAccess.Refusal(open, AppPageAccess.Anonymous));
        Assert.Null(FormResourceAccess.Refusal(open, user));
    }

    [Fact]
    public void The_interface_default_and_the_typed_base_leave_a_class_open()
    {
        IFormResource plain = new PlainResource();
        Assert.False(plain.RequireMember);
        Assert.False(((IFormResource)new TypedResource()).RequireMember);
        Assert.True(((IFormResource)new TypedResource(requireMember: true)).RequireMember);
    }

    [Fact]
    public async Task The_form_endpoints_answer_401_to_a_visitor_before_the_class_runs()
    {
        var required = new MemberResource(requireMember: true, alias: "member.required");
        var open = new MemberResource(requireMember: false, alias: "member.open");
        await using var site = await BlockSite.BootAsync(configure: builder => {
            builder.Services.AddSingleton<IFormResource>(required);
            builder.Services.AddSingleton<IFormResource>(open);
        });
        await site.PublishAsync();
        Guid pageKey, requiredForm, openForm;
        using (var scope = site.CreateScope())
        {
            var contents = scope.ServiceProvider.GetRequiredService<IContentService>();
            var blocks = BlockPages.Empty();
            var form = BlockPages.FormBlock(blocks, required.Alias, [BlockPages.Add(blocks, "appText", ("field", "title"))], []);
            requiredForm = Guid.Parse(form["contentKey"]!.GetValue<string>());
            BlockPages.Layout(blocks).Add(form);
            var other = BlockPages.FormBlock(blocks, open.Alias, [BlockPages.Add(blocks, "appText", ("field", "title"))], []);
            openForm = Guid.Parse(other["contentKey"]!.GetValue<string>());
            BlockPages.Layout(blocks).Add(other);
            var page = contents.Create("Members only", contents.GetById(BlockSite.FormPageKey)!.ParentId, BlockSite.PageAlias);
            page.SetValue(BlockSite.BodyAlias, blocks.ToJsonString());
#pragma warning disable CS0618
            Assert.True(contents.Save(page).Success);
            var published = contents.Publish(page, ["*"]);
            Assert.True(published.Success, string.Join("; ", published.EventMessages?.GetAll().Select(m => m.Message) ?? []));
#pragma warning restore CS0618
            pageKey = page.Key;
        }
        await site.WaitForPublishedAsync(pageKey);
        using var client = site.CreateClient();
        var html = await client.GetStringAsync("/members-only/");
        Assert.Contains("Sign in to use this form.", html);
        Assert.Equal(0, required.Calls);

        using (var load = await client.GetAsync($"/api/forms/{pageKey}/{requiredForm}")) Assert.Equal(HttpStatusCode.Unauthorized, load.StatusCode);
        var body = new { values = new { title = "x" }, definitionVersion = "any", checksum = (string?)null, submissionKey = Guid.NewGuid().ToString() };
        using (var create = await client.PostAsJsonAsync($"/api/forms/{pageKey}/{requiredForm}", body)) Assert.Equal(HttpStatusCode.Unauthorized, create.StatusCode);
        using (var update = await client.PutAsJsonAsync($"/api/forms/{pageKey}/{requiredForm}/1", body)) Assert.Equal(HttpStatusCode.Unauthorized, update.StatusCode);
        using (var delete = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/forms/{pageKey}/{requiredForm}/1") { Content = JsonContent.Create(body) }))
            Assert.Equal(HttpStatusCode.Unauthorized, delete.StatusCode);
        Assert.Equal(0, required.Calls);

        // A class that does not ask keeps today's behaviour: the anonymous visitor reaches it, and it decides.
        var state = await client.GetFromJsonAsync<JsonElement>($"/api/forms/{pageKey}/{openForm}");
        Assert.True(state.TryGetProperty("record", out _));
        Assert.True(open.Calls > 0);
    }

    private sealed class MemberResource(bool requireMember, string alias = "member.test") : IFormResource
    {
        public int Calls { get; private set; }
        public string Alias => alias;
        public string ContractVersion => "1";
        public bool RequireMember => requireMember;
        public IReadOnlyList<FormField> Fields => [new("title", "Title", "text")];
        private Task<FormResult> Answer(string? key)
        {
            Calls++;
            return Task.FromResult(FormResult.Ok(new(key, null, new Dictionary<string, object?> { ["title"] = null }, new(Create: true, Update: true))));
        }
        public Task<FormResult> InitializeAsync(FormExecutionContext context, CancellationToken ct) => Answer(null);
        public Task<FormResult> LoadAsync(string key, FormExecutionContext context, CancellationToken ct) => Answer(key);
        public Task<FormResult> CreateAsync(JsonElement input, FormExecutionContext context, CancellationToken ct) => Answer("1");
        public Task<FormResult> UpdateAsync(string key, JsonElement input, string? checksum, FormExecutionContext context, CancellationToken ct) => Answer(key);
        public Task<FormResult> DeleteAsync(string key, string? checksum, FormExecutionContext context, CancellationToken ct) => Answer(key);
    }

    private sealed class PlainResource : IFormResource
    {
        public string Alias => "plain.test";
        public string ContractVersion => "1";
        public IReadOnlyList<FormField> Fields => [];
        public Task<FormResult> InitializeAsync(FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Denied());
        public Task<FormResult> LoadAsync(string key, FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Denied());
        public Task<FormResult> CreateAsync(JsonElement input, FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Denied());
        public Task<FormResult> UpdateAsync(string key, JsonElement input, string? checksum, FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Denied());
        public Task<FormResult> DeleteAsync(string key, string? checksum, FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Denied());
    }

    private sealed record Input(string? Title);

    private sealed class TypedResource(bool requireMember = false) : FormResource<Input, Input>
    {
        public override string Alias => "typed.test";
        public override bool RequireMember => requireMember;
        public override IReadOnlyList<FormField> Fields => [];
        public override Task<FormResult> InitializeAsync(FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Denied());
        public override Task<FormResult> LoadAsync(string key, FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Denied());
        public override Task<FormResult> DeleteAsync(string key, string? checksum, FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Denied());
        protected override Task<FormResult> CreateAsync(Input input, FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Denied());
        protected override Task<FormResult> UpdateAsync(string key, Input input, string? checksum, FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Denied());
    }
}
