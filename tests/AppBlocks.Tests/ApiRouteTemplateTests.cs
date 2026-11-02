using AppBlocks.Api;

namespace AppBlocks.Tests;

public sealed class ApiRouteTemplateTests
{
    private static ApiRouteTemplate Template(string text)
    {
        var errors = new List<string>();
        var template = ApiRouteTemplate.Parse(text, errors);
        Assert.True(template is not null, string.Join(" ", errors));
        return template;
    }

    private static List<string> Errors(string text)
    {
        var errors = new List<string>();
        Assert.Null(ApiRouteTemplate.Parse(text, errors));
        return errors;
    }

    [Fact]
    public void Parses_literals_and_parameters()
    {
        var template = Template("/orders/:id/lines");
        Assert.Equal("orders/:id/lines", template.Text);
        Assert.Equal([new("orders", false), new("id", true), new("lines", false)], template.Segments);
        Assert.Equal(["id"], template.Parameters);
        Assert.Equal(2, template.Literals);
        Assert.Single(Template("v1.2/order-lines/~mine/:_key9").Parameters);
    }

    [Theory]
    [InlineData("", "Enter the route")]
    [InlineData("orders//lines", "empty segment")]
    [InlineData("orders/", "empty segment")]
    [InlineData("orders/../secret", "'..'")]
    [InlineData("./orders", "'.'")]
    [InlineData("orders?id=1", "query parameters")]
    [InlineData("orders#top", "query parameters")]
    [InlineData("a/b/c/d/e/f/g/h/i", "more than 8 segments")]
    [InlineData("orders/:1d", "not a route parameter")]
    [InlineData("orders/:", "not a route parameter")]
    [InlineData("orders/:id/x/:ID", "twice")]
    [InlineData("orders/a b", "may use only")]
    [InlineData("orders/%20", "may use only")]
    [InlineData("or:ders", "may use only")]
    public void Refuses_malformed_templates(string text, string message)
        => Assert.Contains(Errors(text), error => error.Contains(message, StringComparison.Ordinal));

    [Fact]
    public void Eight_segments_are_allowed() => Assert.Equal(8, Template("a/b/c/d/e/f/g/:h").Segments.Count);

    [Theory]
    [InlineData("orders/:id", "orders/:key", true)]
    [InlineData("orders/:id", "ORDERS/:id", true)]
    [InlineData("orders/:id", "orders/new", false)]
    [InlineData("orders/:id", "orders/:id/lines", false)]
    [InlineData(":kind/new", "orders/:id", false)]
    [InlineData(":a/:b", ":c/:d", true)]
    public void Templates_overlap_when_they_answer_the_same_requests(string a, string b, bool overlap)
    {
        Assert.Equal(overlap, Template(a).Overlaps(Template(b)));
        Assert.Equal(overlap, Template(b).Overlaps(Template(a)));
    }

    [Fact]
    public void Matches_segments_and_decodes_parameters()
    {
        var template = Template("orders/:id/lines");
        Assert.Equal("4", template.Match(ApiRouteTemplate.SplitPath("/orders/4/lines"))!["ID"]);
        Assert.Equal("a/b", template.Match(ApiRouteTemplate.SplitPath("Orders/a%2fb/LINES"))!["id"]);
        Assert.Null(template.Match(ApiRouteTemplate.SplitPath("orders//lines")));
        Assert.Null(template.Match(ApiRouteTemplate.SplitPath("orders/4/lines/")));
        Assert.Null(template.Match(ApiRouteTemplate.SplitPath("orders/4")));
        Assert.Null(template.Match(ApiRouteTemplate.SplitPath("customers/4/lines")));
    }

    [Fact]
    public void Most_literal_segments_match_first_then_earlier_literals()
    {
        var ordered = new[] { ":kind/:id", ":kind/new", "orders/:id", "orders/new" }.Select(Template).ToList();
        ordered.Sort(ApiRouteTemplate.Precedence);
        Assert.Equal(["orders/new", "orders/:id", ":kind/new", ":kind/:id"], ordered.Select(t => t.Text));
        var path = ApiRouteTemplate.SplitPath("orders/new");
        Assert.Equal("orders/new", ordered.First(t => t.Match(path) is not null).Text);
        Assert.Equal("orders/:id", ordered.First(t => t.Match(ApiRouteTemplate.SplitPath("orders/7")) is not null).Text);
        Assert.Equal(":kind/new", ordered.First(t => t.Match(ApiRouteTemplate.SplitPath("customers/new")) is not null).Text);
    }
}
