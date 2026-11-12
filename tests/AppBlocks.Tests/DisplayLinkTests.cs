using System.Net;
using System.Text.Json;
using AppBlocks.Blocks;
using AppBlocks.Forms;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Services;

namespace AppBlocks.Tests;

/// <summary>
/// A Display value's link template: which addresses a page may link to, which fields a template names, and what publishing
/// refuses. The browser builds the address from the loaded record; tests/ui/display-link.browser.test.mjs covers that.
/// </summary>
public sealed class DisplayLinkTests
{
    [Theory]
    [InlineData("")]
    [InlineData("/customers/?id={customerId}")]
    [InlineData("../customer/?id={customerId}")]
    [InlineData("?tab=orders&id={id}")]
    [InlineData("mailto:{email}?subject=Order%20{orderId}")]
    [InlineData("tel:{phone}")]
    [InlineData("https://example.org/track/{reference}")]
    [InlineData("{website}")]
    [InlineData(" { website } ")]
    [InlineData("{folder}/{file}")]
    public void A_template_is_a_path_or_a_web_mail_or_phone_address(string template) => Assert.Null(AppBlockField.LinkProblem(template));

    [Theory]
    [InlineData("javascript:alert({id})")]
    // A field with no value leaves nothing behind, so the text around it is judged joined up as well.
    [InlineData("java{blank}script:alert(1)")]
    [InlineData("{scheme}:alert(1)")]
    [InlineData("data:text/html,{html}")]
    [InlineData("ftp://example.org/{file}")]
    [InlineData("//example.org/{id}")]
    [InlineData("/\\example.org/{id}")]
    [InlineData("{a}//example.org")]
    [InlineData("mail\tto:{email}")]
    public void A_template_that_could_run_script_or_leave_the_site_unseen_is_refused(string template)
        => Assert.Equal("A link is a path, or starts with http:, https:, mailto: or tel:.", AppBlockField.LinkProblem(template));

    [Fact]
    public void A_template_names_its_fields_in_braces()
    {
        Assert.Equal(["email", "reference"], AppBlockField.LinkFields("mailto:{email}?subject={ reference }&cc={email}&x={}"));
        Assert.Empty(AppBlockField.LinkFields("/customers/"));
        Assert.Empty(AppBlockField.LinkFields(null));
    }

    [Theory]
    [InlineData("/orders", true)]
    [InlineData("orders/4", true)]
    [InlineData("?id=4", true)]
    [InlineData("#lines", true)]
    [InlineData("https://example.org", true)]
    [InlineData("HTTP://example.org", true)]
    [InlineData("mailto:someone@example.org", true)]
    [InlineData("tel:+16135550100", true)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("//example.org", false)]
    [InlineData("\\\\example.org", false)]
    [InlineData("/\\example.org", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData(" JaVaScRiPt:alert(1)", false)]
    [InlineData("data:,x", false)]
    [InlineData("orders\tlist", false)]
    public void An_address_is_safe_when_it_is_a_path_or_has_a_known_scheme(string link, bool safe) => Assert.Equal(safe, AppBlockField.IsSafeLink(link));

    [Fact]
    public void Publishing_checks_a_display_values_link_template_against_the_forms_fields()
    {
        var validator = new AppBlocksValidator(new FormResourceRegistry([new Contacts()], []));
        string Page(string block, string template, bool withReference = true)
        {
            var page = BlockPages.Empty();
            var fields = new List<System.Text.Json.Nodes.JsonNode> { BlockPages.Add(page, block, ("field", "email"), ("linkTemplate", template)) };
            if (withReference) fields.Add(BlockPages.Add(page, "appText", ("field", "reference"), ("hidden", "1")));
            BlockPages.Layout(page).Add(BlockPages.FormBlock(page, "contacts.test", [.. fields], []));
            return page.ToJsonString();
        }
        const string template = "mailto:{email}?subject=Request%20{reference}";
        Assert.Empty(validator.Validate(Page("appDisplay", template)));
        Assert.Empty(validator.Validate(Page("appDisplay", "{email}", withReference: false)));

        // The browser builds the address from the record the form loaded, which holds the form's own fields only.
        Assert.Equal(["The link template of 'email' names {reference}, which is not a field on this form. Add a field block for it; it may be hidden."],
            validator.Validate(Page("appDisplay", template, withReference: false)));
        Assert.Contains(validator.Validate(Page("appDisplay", "/people/?id={id}")), e => e.Contains("names {id}, which is not a field on this form"));
        Assert.Equal(["The link template of 'email' is not one a page may link to. A link is a path, or starts with http:, https:, mailto: or tel:."],
            validator.Validate(Page("appDisplay", "javascript:send('{email}')")));

        // Only a Display value has the setting; a stray value on another field block is nobody's link.
        Assert.Empty(validator.Validate(Page("appText", "javascript:send('{nowhere}')")));
    }

    internal sealed class Contacts : IFormResource
    {
        public string Alias => "contacts.test";
        public string ContractVersion => "1";
        public IReadOnlyList<FormField> Fields => [new("email", "Email", "text"), new("reference", "Reference", "text"), new("website", "Website", "text"), new("name", "Name", "text")];
        private static readonly Dictionary<string, object?> Stored = new() { ["email"] = "jane@example.org", ["reference"] = "DOA-2026-0042", ["website"] = "https://example.org/jane", ["name"] = "Jane" };
        public Task<FormResult> InitializeAsync(FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Ok(new(null, null, new Dictionary<string, object?>(), new(Create: true))));
        public Task<FormResult> LoadAsync(string key, FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Ok(new(key, "1", Stored, new(Update: true))));
        public Task<FormResult> CreateAsync(JsonElement input, FormExecutionContext context, CancellationToken ct) => LoadAsync("1", context, ct);
        public Task<FormResult> UpdateAsync(string key, JsonElement input, string? checksum, FormExecutionContext context, CancellationToken ct) => LoadAsync(key, context, ct);
        public Task<FormResult> DeleteAsync(string key, string? checksum, FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Denied());
    }
}

[Collection("Standalone Umbraco")]
public sealed class DisplayLinkSiteTests
{
    [Fact]
    public async Task A_display_value_with_a_link_template_renders_a_link_for_the_runtime_to_fill()
    {
        await using var site = await BlockSite.BootAsync(configure: builder => builder.Services.AddSingleton<IFormResource>(new DisplayLinkTests.Contacts()));
        await site.PublishAsync();
        Guid pageKey;
        using (var scope = site.CreateScope())
        {
            // The installed type has the setting, after the fields' shared ones.
            var installed = scope.ServiceProvider.GetRequiredService<IContentTypeService>().Get("appDisplay")!;
            Assert.Equal(AppBlockField.LinkTemplateAlias, installed.PropertyTypes.OrderBy(p => p.SortOrder).Last().Alias);
            var contents = scope.ServiceProvider.GetRequiredService<IContentService>();
            var blocks = BlockPages.Empty();
            var email = BlockPages.Add(blocks, "appDisplay", ("field", "email"), ("label", "Email the requester"), ("helpText", "Opens a message in your mail program."),
                ("linkTemplate", " mailto:{email}?subject=Request%20{reference} "));
            var reference = BlockPages.Add(blocks, "appText", ("field", "reference"), ("hidden", "1"));
            var website = BlockPages.Add(blocks, "appDisplay", ("field", "website"), ("linkTemplate", "{website}"));
            var name = BlockPages.Add(blocks, "appDisplay", ("field", "name"));
            BlockPages.Layout(blocks).Add(BlockPages.FormBlock(blocks, "contacts.test", [email, reference, website, name], []));
            Assert.Empty(ActivatorUtilities.CreateInstance<AppBlocksValidator>(scope.ServiceProvider).Validate(blocks.ToJsonString()));
            var page = contents.Create("Contacts", contents.GetById(BlockSite.FormPageKey)!.ParentId, BlockSite.PageAlias);
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
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/contacts/"));

        // The value sits in a hidden input, as any field's does, and the link beside it carries the template, trimmed.
        Assert.Contains("type=\"hidden\"", html);
        Assert.Contains("data-display-link=\"email\" data-link-template=\"mailto:{email}?subject=Request%20{reference}\"", html);
        Assert.Contains("data-display-link=\"website\" data-link-template=\"{website}\"", html);
        Assert.Equal(2, html.Split("class=\"app-display-link\"").Length - 1);
        Assert.Contains(">Email the requester</label>", html);
        Assert.Contains("Opens a message in your mail program.", html);
        // No label points at a link: the link names its label instead.
        var label = html[..html.IndexOf(">Email the requester</label>", StringComparison.Ordinal)];
        Assert.DoesNotContain(" for=", label[label.LastIndexOf("<label", StringComparison.Ordinal)..]);
        Assert.Contains("aria-labelledby=", html);
        // A Display value without a template is the read-only control it always was.
        Assert.Contains("data-field=\"name\" data-kind=\"text\" data-readonly=\"true\"", html);
        Assert.DoesNotContain("data-display-link=\"name\"", html);
        Assert.Contains("[data-display-link]", await client.GetStringAsync("/App_Plugins/ApplicationBlocks/forms.js"));
        Assert.Contains(".app-display-link", await client.GetStringAsync("/App_Plugins/ApplicationBlocks/runtime.css"));
    }
}
