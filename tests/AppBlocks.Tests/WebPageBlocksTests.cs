using Microsoft.AspNetCore.Http;
using AppBlocks.Publishing;

namespace AppBlocks.Tests;

public sealed class WebPageBlocksTests
{
    [Fact]
    public void An_explicit_no_store_response_cannot_be_replaced_by_content_only_cache_validation()
    {
        var date = DateTime.UtcNow.AddMinutes(-1);
        var publicResponse = new DefaultHttpContext();
        Assert.False(publicResponse.ApplyCacheHeaders(date));
        var etag = publicResponse.Response.Headers.ETag.ToString();
        Assert.NotEmpty(etag);

        var signedIn = new DefaultHttpContext();
        signedIn.Request.Headers.IfNoneMatch = etag;
        signedIn.Response.Headers.CacheControl = "private, no-store";
        signedIn.Response.Headers.ETag = etag;
        signedIn.Response.Headers.LastModified = publicResponse.Response.Headers.LastModified;
        Assert.False(signedIn.ApplyCacheHeaders(date));
        Assert.Equal(200, signedIn.Response.StatusCode);
        Assert.Equal("private, no-store", signedIn.Response.Headers.CacheControl);
        Assert.False(signedIn.Response.Headers.ContainsKey("ETag"));
        Assert.False(signedIn.Response.Headers.ContainsKey("Last-Modified"));

        var unchanged = new DefaultHttpContext();
        unchanged.Request.Headers.IfNoneMatch = etag;
        Assert.True(unchanged.ApplyCacheHeaders(date));
        Assert.Equal(304, unchanged.Response.StatusCode);
    }

    [Fact]
    public void An_inherited_date_moves_the_cache_validators_but_not_the_date_the_page_reports()
    {
        // Dates after the process's deployment stamp, which the validators never fall below.
        var page = DateTime.UtcNow.AddDays(1);
        var ancestor = page.AddHours(1);
        var context = new DefaultHttpContext();
        context.RegisterPageUpdateDate(page);
        context.RegisterDependencyUpdateDate(ancestor);
        context.RegisterDependencyUpdateDate(page.AddHours(-3));
        var reported = context.GetPageUpdateDate(DateTime.MinValue);
        Assert.Equal(page, reported);
        Assert.False(context.ApplyCacheHeaders(reported));
        Assert.Equal(ancestor.ToString("R"), context.Response.Headers.LastModified.ToString());
    }
}
