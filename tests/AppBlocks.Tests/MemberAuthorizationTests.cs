using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using AppBlocks.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Services;

namespace AppBlocks.Tests;

/// <summary>A companion's own API controller for signed-in Members, as <see cref="MemberAuthorizationTests"/> exercises it.</summary>
[ApiController]
[Route("member-api/notes")]
[Authorize(Policy = AppBlocksAuthorization.MemberPolicy)]
public sealed class MemberNotesTestController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { member = User.FindFirstValue(ClaimTypes.NameIdentifier) });

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Post() => Ok(new { saved = true });
}

/// <summary>A page-style action under the same policy, which keeps the Member cookie's redirect to the login page.</summary>
[Route("member-page-test")]
[Authorize(Policy = AppBlocksAuthorization.MemberPolicy)]
public sealed class MemberPageTestController : Controller
{
    [HttpGet]
    public IActionResult Index() => Content("<p>Members only</p>", "text/html");
}

[Collection("Standalone Umbraco")]
public sealed class MemberAuthorizationTests
{
    [Fact]
    public async Task The_member_policy_answers_401_and_403_to_API_requests_and_hands_out_a_member_antiforgery_token()
    {
        await using var site = await BlockSite.BootAsync();
        using (var visitor = site.CreateClient())
        {
            using (var api = await visitor.GetAsync("/member-api/notes")) Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
            using (var anonymousToken = await visitor.GetAsync(AppBlocksAuthorization.AntiforgeryTokenPath)) Assert.Equal(HttpStatusCode.Unauthorized, anonymousToken.StatusCode);
            // A browser asking for a page still goes to the login page, as Umbraco's Member cookie sends it.
            using var page = new HttpRequestMessage(HttpMethod.Get, "/member-page-test");
            page.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            using var redirected = await visitor.SendAsync(page);
            Assert.Equal(HttpStatusCode.Redirect, redirected.StatusCode);
        }

        var member = site.CreateMember("policy.member", "policy.member@example.invalid", "Policy member");
        using var client = await site.SignInMemberAsync(member);
        var answer = await client.GetFromJsonAsync<JsonElement>("/member-api/notes");
        Assert.Equal(member.Id.ToString(), answer.GetProperty("member").GetString());

        using (var refused = await client.PostAsync("/member-api/notes", JsonContent.Create(new { }))) Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        using var tokenResponse = await client.GetAsync(AppBlocksAuthorization.AntiforgeryTokenPath);
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        Assert.True(tokenResponse.Headers.CacheControl?.NoStore);
        var token = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        using (var post = new HttpRequestMessage(HttpMethod.Post, "/member-api/notes") { Content = JsonContent.Create(new { }) })
        {
            post.Headers.Add(token.GetProperty("headerName").GetString()!, token.GetProperty("token").GetString());
            using var saved = await client.SendAsync(post);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }

        // A Member locked out after signing in is refused at once: the cookie still authenticates, so this is a 403.
        using (var scope = site.CreateScope())
        {
            var members = scope.ServiceProvider.GetRequiredService<IMemberService>();
            var stored = members.GetById(member.Id)!;
            stored.IsLockedOut = true;
#pragma warning disable CS0618
            members.Save(stored);
#pragma warning restore CS0618
        }
        using (var locked = await client.GetAsync("/member-api/notes")) Assert.Equal(HttpStatusCode.Forbidden, locked.StatusCode);
    }

    [Fact]
    public void A_request_is_API_style_by_path_endpoint_header_or_accepted_type()
    {
        static Microsoft.AspNetCore.Http.HttpContext Request(string path, string? accept = null, string? requestedWith = null)
        {
            var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            context.Request.Path = path;
            if (accept is not null) context.Request.Headers.Accept = accept;
            if (requestedWith is not null) context.Request.Headers.XRequestedWith = requestedWith;
            return context;
        }
        Assert.True(AppBlocksAuthorization.IsApiRequest(Request("/api/bbs/files")));
        Assert.True(AppBlocksAuthorization.IsApiRequest(Request("/bbs/files", "application/json")));
        Assert.True(AppBlocksAuthorization.IsApiRequest(Request("/bbs/files", requestedWith: "XMLHttpRequest")));
        Assert.False(AppBlocksAuthorization.IsApiRequest(Request("/bbs/files", "text/html,application/xhtml+xml")));
        Assert.False(AppBlocksAuthorization.IsApiRequest(Request("/apiary")));
    }
}
