using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using UmbracoAppHost.Blocks;

namespace AppBlocks.Tests;

public sealed class DemoHttpTests
{
    public static bool DemoIsConfigured => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APPBLOCKS_DEMO_SETTINGS"));
    private static Guid Key(string name) => AppBlockSchema.Key("standalone-demo:" + name);

    [Fact(Skip = "Start the seeded demo and set APPBLOCKS_DEMO_SETTINGS to its ignored local settings file.", SkipUnless = nameof(DemoIsConfigured))]
    public async Task Native_blocks_work_on_arbitrary_page_properties_with_block_owned_reports_and_page_authorization()
    {
        using var anonymous = new HttpClient { BaseAddress = new Uri("http://localhost:5079") };
        var html = await anonymous.GetStringAsync("/a-normal-content-page/");
        Assert.Contains("Native Block List", html); Assert.Contains("Native Block Grid", html);
        Assert.Contains("Sign in to use this form", html);
        var firstReport = AppBlockContent.ReportName(Key("page"), Key("main-report"));
        Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.GetAsync($"/api/reports/{firstReport}/default")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.GetAsync($"/api/forms/{Key("page")}/{Key("main-form")}/1")).StatusCode);
        using var client = await Administrator();
        var authenticated = await client.GetStringAsync("/a-normal-content-page/");
        Assert.Contains("data-app-form", authenticated); Assert.Contains("data-app-report", authenticated);
        Assert.DoesNotContain("invalid resource", authenticated);
        foreach (var prefix in new[] { "main", "side" }) {
            using var form = await client.GetAsync($"/api/forms/{Key("page")}/{Key(prefix + "-form")}/1");
            Assert.True(form.IsSuccessStatusCode, await form.Content.ReadAsStringAsync());
            Assert.Contains("A task from a normal content page", await form.Content.ReadAsStringAsync());
            using var report = await client.GetAsync($"/api/reports/{AppBlockContent.ReportName(Key("page"), Key(prefix + "-report"))}/default");
            var body = await report.Content.ReadAsStringAsync(); Assert.True(report.IsSuccessStatusCode, body);
            using var json = JsonDocument.Parse(body);
            Assert.Single(json.RootElement.GetProperty("result").GetProperty("rows").EnumerateArray());
            Assert.Contains("A task from a normal content page", body);
        }
        using var inventory = await client.GetAsync("/umbraco/management/api/v1/appblocks/forms");
        Assert.True(inventory.IsSuccessStatusCode, await inventory.Content.ReadAsStringAsync());
        using var forms = JsonDocument.Parse(await inventory.Content.ReadAsStringAsync());
        Assert.Equal(2, forms.RootElement.GetProperty("forms").EnumerateArray().Count(f => f.GetProperty("pageKey").GetGuid() == Key("page")));
        var sample = await client.GetStringAsync($"/umbraco/management/api/v1/appblocks/forms/{Key("page")}/{Key("main-form")}/sample");
        Assert.Contains("JsonPropertyName(\"title\")", sample);
        Assert.Contains("Business rules", await client.GetStringAsync("/umbraco/management/api/v1/appblocks/guide"));
    }

    private static async Task<HttpClient> Administrator()
    {
        using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("APPBLOCKS_DEMO_SETTINGS")!));
        var unattended = settings.RootElement.GetProperty("Umbraco").GetProperty("CMS").GetProperty("Unattended");
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri("http://localhost:5079") };
        const string security = "/umbraco/management/api/v1/security/back-office";
        using var login = await client.PostAsJsonAsync(security + "/login", new { username = unattended.GetProperty("UnattendedUserEmail").GetString(), password = unattended.GetProperty("UnattendedUserPassword").GetString() });
        Assert.True(login.IsSuccessStatusCode, "Demo administrator sign-in failed.");
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        const string redirect = "http://localhost:5079/umbraco/oauth_complete";
        using var authorize = await client.GetAsync(QueryHelpers.AddQueryString(security + "/authorize", new Dictionary<string, string?> {
            ["client_id"] = "umbraco-back-office", ["response_type"] = "code", ["redirect_uri"] = redirect,
            ["code_challenge"] = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), ["code_challenge_method"] = "S256", ["state"] = "appblocks-tests",
        }));
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        var code = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["code"].ToString();
        using var token = await client.PostAsync(security + "/token", new FormUrlEncodedContent(new Dictionary<string, string> {
            ["grant_type"] = "authorization_code", ["client_id"] = "umbraco-back-office", ["redirect_uri"] = redirect, ["code"] = code, ["code_verifier"] = verifier,
        }));
        Assert.True(token.IsSuccessStatusCode, "Demo management token request failed.");
        using var response = JsonDocument.Parse(await token.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", response.RootElement.GetProperty("access_token").GetString());
        return client;
    }
}
