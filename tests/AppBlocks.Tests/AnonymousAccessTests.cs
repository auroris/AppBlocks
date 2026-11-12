using System.Security.Claims;
using System.Text.Json;
using AppBlocks.Blocks;
using AppBlocks.DataSources;
using AppBlocks.Forms;
using AppBlocks.Samples;

namespace AppBlocks.Tests;

/// <summary>What the anonymous rule rests on, apart from a site. <c>AnonymousAccessSiteTests</c> proves it end to end.</summary>
public sealed class AnonymousAccessTests
{
    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    [Fact]
    public void Form_data_keeps_the_named_text_values_and_nothing_else()
    {
        var token = new string('t', FormRequestValues.MaxFormDataValueLength);
        var data = FormRequestValues.FormData(Json(JsonSerializer.Serialize(new Dictionary<string, object?> {
            ["g-recaptcha-response"] = token, ["h-captcha.response:1"] = "ok", ["_hp"] = "", ["number"] = 1, ["none"] = null, ["list"] = new[] { "a", "b" },
            ["too-long"] = token + "t", ["has space"] = "no", ["<script>"] = "no", ["__RequestVerificationToken"] = "secret", [new string('n', 65)] = "no",
        })), "__RequestVerificationToken");
        Assert.Equal(token, data["g-recaptcha-response"]);
        Assert.Equal("ok", data["h-captcha.response:1"]);
        Assert.Equal("", data["_hp"]);
        Assert.Equal(3, data.Count);
    }

    [Fact]
    public void A_repeated_form_data_name_is_left_out_rather_than_guessed()
    {
        var data = FormRequestValues.FormData(Json("""{ "a": "1", "b": "2", "A": "3", "c": "4", "c": "5", "c": "6", "d": ["7"], "d": "8" }"""));
        Assert.Equal(new Dictionary<string, string> { ["b"] = "2" }, data);
    }

    [Fact]
    public void Form_data_stops_at_its_name_limit_and_a_later_repeat_still_removes()
    {
        var names = Enumerable.Range(0, 60).Select(i => $"\"n{i}\": \"v\"").Append("\"n0\": \"again\"");
        var data = FormRequestValues.FormData(Json("{" + string.Join(",", names) + "}"));
        Assert.Equal(FormRequestValues.MaxFormDataNames - 1, data.Count);
        Assert.False(data.ContainsKey("n0"));
        Assert.True(data.ContainsKey("n49"));
        Assert.False(data.ContainsKey("n50"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("{}")]
    public void Form_data_that_is_not_a_set_of_controls_is_empty(string json) => Assert.Empty(FormRequestValues.FormData(Json(json)));

    [Fact]
    public void A_submission_without_form_data_and_existing_callers_still_work()
    {
        Assert.Empty(FormRequestValues.FormData(null));
        var submission = JsonSerializer.Deserialize<FormSubmission>("""{ "definitionVersion": "1", "values": {} }""", JsonSerializerOptions.Web)!;
        Assert.Null(submission.FormData);
        Assert.Equal("t", JsonSerializer.Deserialize<FormSubmission>("""{ "definitionVersion": "1", "values": {}, "formData": { "a": "t" } }""", JsonSerializerOptions.Web)!.FormData!.Value.GetProperty("a").GetString());
        _ = new FormSubmission("1", null, default, null);
        // The positional form every caller uses is untouched, and FormData is empty rather than null.
        var context = new FormExecutionContext(new ClaimsPrincipal(), Guid.Empty, Guid.Empty, "1", "key", new Dictionary<string, string>());
        Assert.Empty(context.FormData);
        var (member, _, _, _, key, _) = context;
        Assert.Same(context.Member, member);
        Assert.Equal("key", key);
    }

    [Fact]
    public void Someone_anonymous_is_nobody()
    {
        var anonymous = AppPageAccess.Anonymous;
        Assert.False(anonymous.Identity?.IsAuthenticated);
        Assert.Empty(anonymous.Claims);
        Assert.Equal("anonymous", BlockOwnerAccess.Actor(anonymous));
        var nobody = DataSourceUser.Anonymous;
        Assert.True(nobody.IsAnonymous);
        Assert.False(nobody.IsMember);
        Assert.False(nobody.IsBackOfficeUser);
        Assert.False(nobody.IsInMemberGroup("anything"));
        Assert.Empty(nobody.MemberGroupNames);
        Assert.False(new DataSourceUser("1", null, null, null, [], false).IsAnonymous);
        Assert.True(new DataSourceUser("1", null, null, null, [], false).IsMember);
    }

    [Fact]
    public void Field_defaults_about_a_person_resolve_to_nothing_for_someone_anonymous()
    {
        var variables = new AppBindVariables("", null, null, null, DateTimeOffset.Now, new Microsoft.AspNetCore.Http.QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { ["topic"] = "sales" }));
        foreach (var name in new[] { ":USER_KEY", ":USER_NAME", ":USER_SID", ":USER_EMAIL" }) Assert.Null(variables.Resolve(name, "text"));
        Assert.Equal("sales", variables.Resolve(":PARAM.topic", "text"));
        Assert.NotNull(variables.Resolve(":NOW", "date"));
    }

    [Fact]
    public void Generated_samples_begin_every_operation_with_a_sign_in_check()
    {
        var source = FormSampleGenerator.Generate(new DiscoveredForm(Guid.NewGuid(), Guid.NewGuid(), null, "Example", "Example", "", false, [new("title", "Title", "text")]));
        Assert.Contains("a class with no check is public", source);
        var operations = System.Text.RegularExpressions.Regex.Matches(source, @"public async Task<[^\n]+\n\s*\{\s*\n\s*(?<first>[^\n]+)");
        Assert.Equal(2, operations.Count);
        Assert.All(operations, operation => Assert.Equal("if (user.Identity?.IsAuthenticated != true) throw new UnauthorizedAccessException();", operation.Groups["first"].Value.Trim()));
    }
}
