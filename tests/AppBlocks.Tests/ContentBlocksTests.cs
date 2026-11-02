using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using AppBlocks.Blocks;
using AppBlocks.Forms;

namespace AppBlocks.Tests;

/// <summary>
/// The parts of the content blocks that need no site: how a field's default resolves, how a width becomes a
/// grid span, how the page's CSS and JavaScript are collected and written, and that publishing accepts the
/// new blocks inside a form.
/// </summary>
public sealed class ContentBlocksTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 14, 5, 9, TimeSpan.FromHours(-6));

    // ASP.NET Core parses a query string into a dictionary that ignores case, so this one does too.
    private static AppBindVariables Variables(string? sid = "S-1-5-21-1-2-3-1105") => new("1105", sid, "test.user", "test.user@example.invalid", Now,
        new QueryCollection(new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase) { ["region"] = "EAST", ["empty"] = "" }));

    [Theory]
    [InlineData(":USER_SID", "text", "S-1-5-21-1-2-3-1105")]
    [InlineData(":USER_NAME", "text", "test.user")]
    [InlineData(":user_name", "text", "test.user")]
    [InlineData(" :USER_KEY ", "text", "1105")]
    [InlineData(":USER_EMAIL", "text", "test.user@example.invalid")]
    [InlineData(":PARAM.region", "text", "EAST")]
    [InlineData(":param.REGION", "text", "EAST")]
    [InlineData(":NOW", "date", "2026-10-23")]
    [InlineData(":NOW", "time", "14:05")]
    [InlineData(":NOW", "datetime", "2026-10-23T14:05:09-06:00")]
    [InlineData(":NOW", "text", "2026-10-23 14:05")]
    [InlineData("NEW", "text", "NEW")]
    [InlineData("true", "checkbox", "true")]
    [InlineData("12.50", "number", "12.50")]
    // Not a variable, so the editor meant the text: a typo shows up in the field rather than vanishing.
    [InlineData(":USER_SIDE", "text", ":USER_SIDE")]
    [InlineData(":PARAM.", "text", ":PARAM.")]
    [InlineData("Ordered by :USER_NAME", "text", "Ordered by :USER_NAME")]
    public void Defaults_resolve_a_whole_variable_in_any_case_and_otherwise_keep_the_text(string value, string kind, string expected)
        => Assert.Equal(expected, Variables().Resolve(value, kind));

    [Theory]
    [InlineData(null, "text")]
    [InlineData("", "text")]
    [InlineData("   ", "text")]
    [InlineData(":PARAM.missing", "text")]
    [InlineData(":PARAM.empty", "text")]
    [InlineData(":NOW", "number")]
    [InlineData(":NOW", "checkbox")]
    public void A_default_without_a_value_leaves_the_field_to_the_resource(string? value, string kind)
        => Assert.Null(Variables().Resolve(value, kind));

    [Fact]
    public void An_account_with_only_a_password_has_no_SID_to_default_to()
        => Assert.Null(Variables(sid: null).Resolve(":USER_SID", "text"));

    [Theory]
    [InlineData(12, null, 12, 12)]
    [InlineData(8, null, 12, 8)]
    [InlineData(12, 6, 12, 6)]
    [InlineData(12, 3, 12, 3)]
    [InlineData(6, 12, 6, 6)]
    [InlineData(4, 8, 4, 4)]
    public void A_width_replaces_the_grid_span_and_never_exceeds_the_grid(int columnSpan, int? width, int columns, int expected)
        => Assert.Equal(expected, AppBlockLayout.ItemSpan(columnSpan, width, columns));

    [Fact]
    public void Page_assets_are_written_once_each_in_order_and_carry_the_nonce()
    {
        var assets = new AppPageAssets();
        Assert.True(assets.IsEmpty);
        assets.AddStylesheet("/App_Plugins/ApplicationBlocks/runtime.css");
        assets.AddStylesheet("/App_Plugins/ApplicationBlocks/runtime.css");
        assets.AddStyle(".a { color: red; }");
        assets.AddStyle("  .a { color: red; }\r\n");
        assets.AddStyle(".b { content: \"</style>\"; }");
        assets.AddStyle("   ");
        assets.AddStyle(null);
        assets.AddScript("console.log('one');");
        assets.AddScript("console.log('one');");
        assets.AddScript("document.body.insertAdjacentHTML('beforeend', '<script></script>');");
        Assert.Equal(new[] { "/App_Plugins/ApplicationBlocks/runtime.css" }, assets.Stylesheets);
        Assert.Equal(new[] { ".a { color: red; }", ".b { content: \"</style>\"; }" }, assets.Styles);
        Assert.Equal(2, assets.Scripts.Count);

        var head = Html(assets.Head("n0nce"));
        Assert.Equal("""
            <link href="/App_Plugins/ApplicationBlocks/runtime.css" nonce="n0nce" rel="stylesheet" />
            <style nonce="n0nce">
            .a { color: red; }
            </style>
            <style nonce="n0nce">
            .b { content: "<\/style>"; }
            </style>

            """.ReplaceLineEndings("\n"), head.ReplaceLineEndings("\n"));
        var body = Html(assets.Body("n0nce"));
        Assert.Equal(2, body.Split("<script nonce=\"n0nce\">").Length - 1);
        Assert.Contains("'<script><\\/script>'", body);
        Assert.DoesNotContain("nonce", Html(assets.Head(null)) + Html(assets.Body(null)));
    }

    [Fact]
    public void The_nonce_is_read_from_the_request_when_the_site_sets_one()
    {
        var context = new DefaultHttpContext();
        Assert.Null(AppPageAssets.Nonce(context));
        Assert.Null(AppPageAssets.Nonce(null));
        context.Items[AppPageAssets.CspNonceItem] = "";
        Assert.Null(AppPageAssets.Nonce(context));
        context.Items[AppPageAssets.CspNonceItem] = "abc123";
        Assert.Equal("abc123", AppPageAssets.Nonce(context));
    }

    [Fact]
    public async Task Publishing_accepts_content_blocks_inside_a_form_and_still_rejects_unknown_blocks()
    {
        var services = new ServiceCollection();
        services.AddScoped<IFormResource, SampleOrdersResource>(); services.AddScoped<FormResourceRegistry>();
        services.AddScoped<AppBlocksValidator>();
        using var provider = services.BuildServiceProvider(); using var scope = provider.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<AppBlocksValidator>();
        await validator.LoadAsync();

        var page = BlockPages.ContentBlocksForm();
        var aliases = page["contentData"]!.AsArray().Select(block => Guid.Parse(block!["contentTypeKey"]!.GetValue<string>())).ToHashSet();
        Assert.All(AppBlockSchema.ContentAliases, alias => Assert.Contains(AppBlockSchema.Key(alias), aliases));
        Assert.Empty(validator.Validate(page.ToJsonString()));

        var unknown = page["contentData"]!.AsArray().First(block => block!["contentTypeKey"]!.GetValue<string>() == AppBlockSchema.Key(AppBlockSchema.HtmlAlias).ToString())!;
        unknown["contentTypeKey"] = Guid.NewGuid().ToString();
        Assert.Contains(validator.Validate(page.ToJsonString()), error => error.Contains("unsupported block"));
    }

    private static string Html(IHtmlContent content)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        content.WriteTo(writer, HtmlEncoder.Default);
        return writer.ToString();
    }
}
