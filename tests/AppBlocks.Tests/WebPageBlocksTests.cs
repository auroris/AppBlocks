using Microsoft.AspNetCore.Http;
using AppBlocks.Publishing;

namespace AppBlocks.Tests;

public sealed class WebPageBlocksTests
{
    [Theory]
    [InlineData(NoStorePages.AllPages, true)]
    [InlineData(NoStorePages.AppBlocksPages, true)]
    [InlineData(NoStorePages.Off, false)]
    public void Page_cache_setting_controls_rendered_blocks_without_erasing_host_headers(NoStorePages mode, bool noStore)
    {
        using var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .Configure<PageCacheOptions>(options => options.NoStorePages = mode).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Headers.CacheControl = "private, max-age=60";
        context.KeepAppBlockPageOutOfCaches();
        Assert.Equal(noStore, context.Response.GetTypedHeaders().CacheControl!.NoStore);
        if (!noStore) Assert.Equal("private, max-age=60", context.Response.Headers.CacheControl);
    }

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
    public void A_local_date_is_compared_in_utc_and_reported_as_registered()
    {
        var local = DateTime.UtcNow.AddDays(1).ToLocalTime();
        var context = new DefaultHttpContext();
        context.RegisterPageUpdateDate(local);
        // An earlier moment loses, whichever way it is written.
        context.RegisterPageUpdateDate(new DateTimeOffset(local).AddHours(-1).ToOffset(TimeSpan.FromHours(9)));
        context.RegisterPageUpdateDate(local.AddMinutes(-1));
        // A layout shows this as the page's "Date modified", so it is the date the page registered, not its UTC form.
        var reported = context.GetPageUpdateDate(DateTime.MinValue);
        Assert.Equal(local, reported);
        Assert.Equal(local.Kind, reported.Kind);
        Assert.False(context.ApplyCacheHeaders(reported));
        Assert.Equal(local.ToUniversalTime().ToString("R"), context.Response.Headers.LastModified.ToString());
    }

    [Fact]
    public void A_date_with_an_offset_is_reported_and_validated_in_utc()
    {
        // After the process's deployment stamp, which the validators never fall below.
        var page = new DateTimeOffset(DateTime.UtcNow.AddDays(1).Ticks, TimeSpan.Zero).ToOffset(TimeSpan.FromHours(5));
        var context = new DefaultHttpContext();
        context.RegisterPageUpdateDate(page);
        var reported = context.GetPageUpdateDate(DateTime.MinValue);
        Assert.Equal(DateTimeKind.Utc, reported.Kind);
        Assert.Equal(page.UtcDateTime, reported);
        Assert.False(context.ApplyCacheHeaders(reported));
        Assert.Equal(page.UtcDateTime.ToString("R"), context.Response.Headers.LastModified.ToString());
    }

    [Fact]
    public void Conditional_requests_compare_whole_tags_and_read_dates_in_any_culture()
    {
        var date = DateTime.UtcNow.AddDays(1);
        var first = new DefaultHttpContext();
        first.ApplyCacheHeaders(date);
        var etag = first.Response.Headers.ETag.ToString();
        var lastModified = first.Response.Headers.LastModified.ToString();

        var listed = new DefaultHttpContext();
        listed.Request.Headers.IfNoneMatch = $"\"other\", W/{etag}";
        Assert.True(listed.ApplyCacheHeaders(date));

        // A tag that only contains the current one is another representation; If-Modified-Since is then ignored.
        var longer = new DefaultHttpContext();
        longer.Request.Headers.IfNoneMatch = $"\"0{etag.Trim('"')}\"";
        longer.Request.Headers.IfModifiedSince = lastModified;
        Assert.False(longer.ApplyCacheHeaders(date));
        Assert.Equal(200, longer.Response.StatusCode);

        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("fr-FR");
            var since = new DefaultHttpContext();
            since.Request.Headers.IfModifiedSince = lastModified;
            Assert.True(since.ApplyCacheHeaders(date));
            Assert.Equal(304, since.Response.StatusCode);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
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
