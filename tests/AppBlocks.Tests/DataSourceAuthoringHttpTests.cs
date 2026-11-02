using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AppBlocks.Connections;
using AppBlocks.DataSources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Sync;
using Umbraco.Extensions;

namespace AppBlocks.Tests;

/// <summary>The backoffice's Test button over a plain Umbraco site with only AppBlocks installed.</summary>
[Collection("Standalone Umbraco")]
public sealed class DataSourceAuthoringHttpTests
{
    [Fact]
    public async Task Test_uses_unsaved_values_and_existing_editor_permissions_without_publishing()
    {
        var root = Directory.CreateTempSubdirectory("appblocks-authoring-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
            ApplicationName = typeof(DataSourceAuthoringHttpTests).Assembly.GetName().Name, ContentRootPath = root, EnvironmentName = "Development",
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["ConnectionStrings:umbracoDbDSN"] = $"Data Source={Path.Combine(root, "umbraco.db")};Pooling=False",
            ["ConnectionStrings:umbracoDbDSN_ProviderName"] = "Microsoft.Data.Sqlite",
            ["Umbraco:CMS:Global:Id"] = Guid.NewGuid().ToString(), ["Umbraco:CMS:Global:UseHttps"] = "false",
            ["Umbraco:CMS:ModelsBuilder:ModelsMode"] = "Nothing",
            ["Umbraco:CMS:TypeFinder:AdditionalAssemblyExclusionEntries:0"] = "xunit.",
            // As a site with the Oracle driver installed sets it, so that Umbraco's startup scan does not load the driver's optional cloud assemblies; see docs/connections.md.
            ["Umbraco:CMS:TypeFinder:AdditionalAssemblyExclusionEntries:1"] = "Oracle.ManagedDataAccess",
            ["Umbraco:CMS:Unattended:InstallUnattended"] = "true", ["Umbraco:CMS:Unattended:UpgradeUnattended"] = "true",
            ["Umbraco:CMS:Unattended:UnattendedUserName"] = "Authoring test",
            ["Umbraco:CMS:Unattended:UnattendedUserEmail"] = "authoring@example.test",
            ["Umbraco:CMS:Unattended:UnattendedUserPassword"] = "Authoring!Test5278",
        });
        builder.Services.Configure<InstallDefaultDataSettings>(Constants.Configuration.NamedOptions.InstallDefaultData.Languages,
            settings => { settings.InstallData = InstallDefaultDataOption.Values; settings.Values = new HashSet<string> { "en" }; });
        builder.CreateUmbracoBuilder().AddBackOffice().AddWebsite().AddComposers().Build();
        builder.Services.Replace(ServiceDescriptor.Singleton<IServerRoleAccessor, SingleServerRoleAccessor>());
        await using var app = builder.Build();
        try
        {
            await app.BootUmbracoAsync();
            app.UseUmbraco().WithMiddleware(u => { u.UseBackOffice(); u.UseWebsite(); })
                .WithEndpoints(u => { u.UseBackOfficeEndpoints(); u.UseWebsiteEndpoints(); });
            await app.StartAsync();
            var upgrade = app.Services.GetServices<IHostedService>().OfType<BackgroundService>()
                .Single(service => service.GetType().Name == "UnattendedUpgradeBackgroundService");
            if (upgrade.ExecuteTask is { } task) await task.WaitAsync(TimeSpan.FromSeconds(90));
            var runtime = app.Services.GetRequiredService<IRuntimeState>();
            Assert.True(runtime.Level == RuntimeLevel.Run, runtime.BootFailedException?.ToString() ?? runtime.Level.ToString());
            var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());

            using var scope = app.Services.CreateScope();
            var types = scope.ServiceProvider.GetRequiredService<IContentTypeService>();
            for (var i = 0; i < 100 && types.Get(DataSourceSchema.SqlAlias) is null; i++) await Task.Delay(100);
            var contents = scope.ServiceProvider.GetRequiredService<IContentService>();
#pragma warning disable CS0618
            var sources = contents.Create("Authoring sources", -1, DataSourceSchema.RootAlias);
            contents.Save(sources);
            var node = contents.Create("Draft source", sources.Id, DataSourceSchema.SqlAlias);
            node.SetValue("alias", "saved-draft");
            node.SetValue("connection", ConnectionCatalog.UmbracoConnection);
            node.SetValue("source", "MISSING_TABLE");
            contents.Save(node);
            const string path = "/umbraco/management/api/v1/data-sources/test";
            var request = new {
                key = node.Key, parentKey = sources.Key,
                values = new { alias = "current-draft", connection = ConnectionCatalog.UmbracoConnection, source = "SELECT :PARAM.label AS TITLE" },
                samples = new Dictionary<string, object?> { ["PARAM.label"] = "' OR 1=1 --" },
            };
            using var anonymous = new HttpClient { BaseAddress = address };
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(path, request)).StatusCode);

            var users = scope.ServiceProvider.GetRequiredService<IUserService>();
            const string email = "source.editor@example.test";
            var created = await users.CreateAsync(Constants.Security.SuperUserKey, new UserCreateModel {
                UserName = email, Email = email, Name = "Source editor", Kind = UserKind.Default, UserGroupKeys = new HashSet<Guid> { Constants.Security.EditorGroupKey },
            }, approveUser: true);
            Assert.True(created.Success, created.Status.ToString());
            var editor = created.Result.CreatedUser!;
            const string password = "Source-editor-test91!";
            var manager = scope.ServiceProvider.GetRequiredService<IBackOfficeUserManager>();
            var identity = (await manager.FindByIdAsync(editor.Key.ToString()))!;
            var changed = await manager.ChangePasswordWithResetAsync(identity.Id, await manager.GeneratePasswordResetTokenAsync(identity), password);
            Assert.True(changed.Succeeded, string.Join("; ", changed.Errors.Select(error => error.Description)));
            using var client = await SignInAsync(address, editor.Username, password);
            using var response = await client.PostAsJsonAsync(path, request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Empty(result.GetProperty("errors").EnumerateArray());
            Assert.Equal("' OR 1=1 --", result.GetProperty("row").GetProperty("TITLE").GetString());
            var persisted = contents.GetById(node.Key)!;
            Assert.Equal("MISSING_TABLE", persisted.GetValue<string>("source"));
            Assert.False(persisted.Published);

            // A new definition can be tested before its first save using its parent's create permission.
            using var newDraft = await client.PostAsJsonAsync(path, new { parentKey = sources.Key, request.values, request.samples });
            Assert.Equal(HttpStatusCode.OK, newDraft.StatusCode);

            var outside = contents.Create("Other sources", -1, DataSourceSchema.RootAlias);
            contents.Save(outside);
#pragma warning restore CS0618
            editor.StartContentIds = [outside.Id];
            users.Save(editor);
            using var denied = await client.PostAsJsonAsync(path, request);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        finally { await app.StopAsync(); }
    }

    private static async Task<HttpClient> SignInAsync(Uri address, string username, string password)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = address };
        const string security = "/umbraco/management/api/v1/security/back-office";
        using var login = await client.PostAsJsonAsync(security + "/login", new { username, password });
        Assert.True(login.IsSuccessStatusCode, "Test sign-in failed: " + login.StatusCode);
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var redirect = new Uri(address, "/umbraco/oauth_complete").ToString();
        using var authorize = await client.GetAsync(QueryHelpers.AddQueryString(security + "/authorize", new Dictionary<string, string?> {
            ["client_id"] = "umbraco-back-office", ["response_type"] = "code", ["redirect_uri"] = redirect,
            ["code_challenge"] = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), ["code_challenge_method"] = "S256", ["state"] = "authoring-tests",
        }));
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        var code = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["code"].ToString();
        using var token = await client.PostAsync(security + "/token", new FormUrlEncodedContent(new Dictionary<string, string> {
            ["grant_type"] = "authorization_code", ["client_id"] = "umbraco-back-office", ["redirect_uri"] = redirect, ["code"] = code, ["code_verifier"] = verifier,
        }));
        Assert.True(token.IsSuccessStatusCode, "Test management token request failed: " + token.StatusCode);
        using var response = JsonDocument.Parse(await token.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", response.RootElement.GetProperty("access_token").GetString());
        return client;
    }
}

[CollectionDefinition("Standalone Umbraco", DisableParallelization = true)]
public sealed class StandaloneUmbracoCollection;
