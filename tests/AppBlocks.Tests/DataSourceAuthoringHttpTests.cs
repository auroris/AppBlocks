using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AppBlocks.Api;
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
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Core.Sync;
using Umbraco.Extensions;

namespace AppBlocks.Tests;

/// <summary>The backoffice's Test button over a plain Umbraco site with only AppBlocks installed.</summary>
[Collection("Standalone Umbraco")]
public sealed class DataSourceAuthoringHttpTests
{
    [Fact]
    public async Task Previews_and_code_editing_require_AppBlocks_administration_and_Umbraco_permissions()
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
            ["AppBlocks:AdministratorGroups:0"] = "appBlocksAdministrators",
            // The removed setting must not grant code editing or preview access.
            ["AppBlocks:CodeEditorGroups:0"] = "legacyCodeEditors",
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
            node.SetValue("access", "Public");
            node.SetValue("connection", ConnectionCatalog.UmbracoConnection);
            node.SetValue("source", "MISSING_TABLE");
            contents.Save(node);
            const string path = "/umbraco/management/api/v1/data-sources/test";
            var request = new {
                key = node.Key, parentKey = sources.Key,
                values = new { alias = "current-draft", access = "Public", connection = ConnectionCatalog.UmbracoConnection, source = "SELECT :PARAM.label AS TITLE" },
                samples = new Dictionary<string, object?> { ["PARAM.label"] = "' OR 1=1 --" },
            };
            var endpoints = contents.Create("Authoring endpoints", -1, ApiEndpointCatalog.RootAlias);
            contents.Save(endpoints);
            var endpoint = contents.Create("Draft endpoint", endpoints.Id, ApiEndpointCatalog.Alias);
            contents.Save(endpoint);
            const string endpointPath = "/umbraco/management/api/v1/api-endpoints/test";
            var endpointRequest = new {
                key = endpoint.Key, parentKey = endpoints.Key,
                values = new { alias = "draft-endpoint", route = "authoring-test", access = "Public", connection = ConnectionCatalog.UmbracoConnection,
                    sql = "SELECT :PARAM.label AS TITLE", queryParameters = "label" },
                query = new { label = "Endpoint preview" },
            };
            (string Path, object Saved, object New)[] previews = [
                (path, request, new { parentKey = sources.Key, request.values, request.samples }),
                (endpointPath, endpointRequest, new { parentKey = endpoints.Key, endpointRequest.values, endpointRequest.query }),
            ];
            async Task ExpectPreviews(HttpClient caller, HttpStatusCode status)
            {
                foreach (var preview in previews)
                    foreach (var body in new[] { preview.Saved, preview.New })
                    {
                        using var tested = await caller.PostAsJsonAsync(preview.Path, body);
                        Assert.Equal(status, tested.StatusCode);
                        if (status == HttpStatusCode.OK)
                        {
                            var answer = await tested.Content.ReadFromJsonAsync<JsonElement>();
                            Assert.Empty(answer.GetProperty("errors").EnumerateArray());
                            if (preview.Path == endpointPath)
                            {
                                Assert.Equal(200, answer.GetProperty("status").GetInt32());
                                Assert.Equal("Endpoint preview", answer.GetProperty("body").GetProperty("items")[0].GetProperty("title").GetString());
                            }
                        }
                    }
            }
            using var anonymous = new HttpClient { BaseAddress = address };
            await ExpectPreviews(anonymous, HttpStatusCode.Unauthorized);

            var users = scope.ServiceProvider.GetRequiredService<IUserService>();
            var groups = scope.ServiceProvider.GetRequiredService<IUserGroupService>();
            var administrators = new UserGroup(scope.ServiceProvider.GetRequiredService<IShortStringHelper>()) {
                Alias = "appBlocksAdministrators", Name = "AppBlocks administrators",
            };
            administrators.AddAllowedSection(Constants.Applications.Content);
            Assert.True((await groups.CreateAsync(administrators, Constants.Security.SuperUserKey, [])).Success);
            var legacyEditors = new UserGroup(scope.ServiceProvider.GetRequiredService<IShortStringHelper>()) {
                Alias = "legacyCodeEditors", Name = "Legacy code editors",
            };
            legacyEditors.AddAllowedSection(Constants.Applications.Settings);
            Assert.True((await groups.CreateAsync(legacyEditors, Constants.Security.SuperUserKey, [])).Success);
            const string email = "source.editor@example.test";
            var created = await users.CreateAsync(Constants.Security.SuperUserKey, new UserCreateModel {
                UserName = email, Email = email, Name = "Source editor", Kind = UserKind.Default,
                UserGroupKeys = new HashSet<Guid> { Constants.Security.EditorGroupKey, legacyEditors.Key },
            }, approveUser: true);
            Assert.True(created.Success, created.Status.ToString());
            var editor = created.Result.CreatedUser!;
            const string password = "Source-editor-test91!";
            var manager = scope.ServiceProvider.GetRequiredService<IBackOfficeUserManager>();
            var identity = (await manager.FindByIdAsync(editor.Key.ToString()))!;
            var changed = await manager.ChangePasswordWithResetAsync(identity.Id, await manager.GeneratePasswordResetTokenAsync(identity), password);
            Assert.True(changed.Succeeded, string.Join("; ", changed.Errors.Select(error => error.Description)));
            using var client = await SignInAsync(address, editor.Username, password);
            await AssertNewSourcesStartPublic(scope.ServiceProvider, client, sources);
            // Content and Settings access, including the legacy code group, do not grant AppBlocks administration.
            await ExpectPreviews(client, HttpStatusCode.Forbidden);
            const string codePath = "/umbraco/management/api/v1/appblocks/code";
            Assert.False((await client.GetFromJsonAsync<JsonElement>(codePath + "/access")).GetProperty("allowed").GetBoolean());
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(codePath)).StatusCode);
            var codeFile = new { path = "PreviewAccessProbe.cs", content = "public sealed class PreviewAccessProbe { }" };
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(codePath + "/file/new", codeFile)).StatusCode);

            // A configured group grants AppBlocks access without adding the User to Umbraco's Administrators.
            editor = users.GetByUsername(email)!;
            editor.AddGroup(administrators.ToReadOnlyGroup());
            users.Save(editor);
            Assert.DoesNotContain(editor.Groups, group => group.Alias == Constants.Security.AdminGroupAlias);
            await ExpectPreviews(client, HttpStatusCode.OK);
            Assert.True((await client.GetFromJsonAsync<JsonElement>(codePath + "/access")).GetProperty("allowed").GetBoolean());
            var codeState = await client.GetFromJsonAsync<JsonElement>(codePath);
            var canCompile = codeState.GetProperty("canEdit").GetBoolean();
            Assert.Equal(canCompile ? HttpStatusCode.OK : HttpStatusCode.Conflict,
                (await client.PostAsJsonAsync(codePath + "/file/new", codeFile)).StatusCode);

            // Umbraco's own Administrators continue to pass the same rule without additional configuration.
            using var administrator = await SignInAsync(address, "authoring@example.test", "Authoring!Test5278");
            await ExpectPreviews(administrator, HttpStatusCode.OK);
            Assert.True((await administrator.GetFromJsonAsync<JsonElement>(codePath + "/access")).GetProperty("allowed").GetBoolean());
            Assert.Equal(HttpStatusCode.OK, (await administrator.GetAsync(codePath)).StatusCode);

            using var response = await client.PostAsJsonAsync(path, request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Empty(result.GetProperty("errors").EnumerateArray());
            Assert.Equal("' OR 1=1 --", result.GetProperty("row").GetProperty("TITLE").GetString());
            using var missingAccess = await client.PostAsJsonAsync(path, new {
                request.key, request.parentKey,
                values = new { request.values.alias, request.values.connection, request.values.source }, request.samples,
            });
            Assert.Equal(HttpStatusCode.OK, missingAccess.StatusCode);
            var invalid = await missingAccess.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Contains(invalid.GetProperty("errors").EnumerateArray(), error => error.GetString()!.Contains("Select an access value"));
            Assert.Equal(JsonValueKind.Null, invalid.GetProperty("row").ValueKind);
            var persisted = contents.GetById(node.Key)!;
            Assert.Equal("MISSING_TABLE", persisted.GetValue<string>("source"));
            Assert.False(persisted.Published);

            var outside = contents.Create("Other sources", -1, DataSourceSchema.RootAlias);
            contents.Save(outside);
#pragma warning restore CS0618
            var originalStartContentIds = editor.StartContentIds;
            editor.StartContentIds = [outside.Id];
            users.Save(editor);
            await ExpectPreviews(client, HttpStatusCode.Forbidden);

            // A retained login cannot preserve access after removal from the configured group.
            editor.StartContentIds = originalStartContentIds;
            editor.RemoveGroup(administrators.Alias);
            users.Save(editor);
            await ExpectPreviews(client, HttpStatusCode.Forbidden);
            Assert.False((await client.GetFromJsonAsync<JsonElement>(codePath + "/access")).GetProperty("allowed").GetBoolean());
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(codePath)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(codePath + "/file/new", codeFile)).StatusCode);

            // Removing Settings access still hides the code editor from an AppBlocks administrator.
            editor.AddGroup(administrators.ToReadOnlyGroup());
            editor.RemoveGroup(legacyEditors.Alias);
            users.Save(editor);
            using (var contentOnly = await SignInAsync(address, editor.Username, password))
            {
                await ExpectPreviews(contentOnly, HttpStatusCode.OK);
                Assert.Equal(HttpStatusCode.Forbidden, (await contentOnly.GetAsync(codePath + "/access")).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await contentOnly.GetAsync(codePath)).StatusCode);
            }

        }
        finally { await app.StopAsync(); }
    }

    /// <summary>
    /// A fresh install keeps Access mandatory, with Public one of its stored choices, and the backoffice registers the value
    /// preset that starts a new SQL data source at Public. The server never fills the value in: a source created in code starts empty.
    /// </summary>
    private static async Task AssertNewSourcesStartPublic(IServiceProvider services, HttpClient client, IContent sources)
    {
        var type = services.GetRequiredService<IContentTypeService>().Get(DataSourceSchema.SqlAlias)!;
        var access = type.PropertyTypes.Single(property => property.Alias == "access");
        Assert.True(access.Mandatory);
        var dataType = (await services.GetRequiredService<IDataTypeService>().GetAsync(access.DataTypeKey))!;
        Assert.Equal(Constants.PropertyEditors.Aliases.RadioButtonList, dataType.EditorAlias);
        Assert.Equal(["Public", "Not public", "Specific groups"], dataType.ConfigurationAs<ValueListConfiguration>()!.Items);
#pragma warning disable CS0618
        Assert.Null(services.GetRequiredService<IContentService>().Create("Code source", sources.Id, DataSourceSchema.SqlAlias).GetValue("access"));
#pragma warning restore CS0618

        using var manifests = JsonDocument.Parse(await client.GetStringAsync("/umbraco/management/api/v1/manifest/manifest/private"));
        var preset = Assert.Single(manifests.RootElement.EnumerateArray().SelectMany(package => package.GetProperty("extensions").EnumerateArray()),
            extension => extension.GetProperty("type").GetString() == "propertyValuePreset"
                && extension.TryGetProperty("forPropertyEditorSchemaAlias", out var schema) && schema.GetString() == dataType.EditorAlias
                && extension.GetProperty("alias").GetString()!.StartsWith("AppBlocks.", StringComparison.Ordinal));
        var script = await client.GetStringAsync(preset.GetProperty("api").GetString());
        Assert.Contains($"\"{type.Key}\"", script);
        Assert.Contains("\"access\"", script);
        Assert.Contains("\"Public\"", script);
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
