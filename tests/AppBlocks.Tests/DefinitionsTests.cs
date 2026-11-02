using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AppBlocks.Blocks;
using AppBlocks.DataSources;
using AppBlocks.Setup;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Packaging;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.OperationStatus;
using Umbraco.Cms.Core.Strings;

namespace AppBlocks.Tests;

/// <summary>
/// The definitions on a site over time: Umbraco's package migration plan records where the site stands, Reinstall resets every
/// definition to the package without touching content, and Uninstall removes them, refusing while something of the site's depends on them.
/// </summary>
[Collection("Standalone Umbraco")]
public sealed class DefinitionsTests
{
    private const string Api = "/umbraco/management/api/v1/appblocks/definitions";
    private static readonly Guid FormsContainerGrid = Guid.Parse("97b36051-4abe-e750-6667-9cb7ee6a9a99");

    [Fact]
    public async Task The_plan_state_is_recorded_and_a_reinstall_resets_the_definitions_while_the_site_keeps_its_content()
    {
        await using var site = await BlockSite.StartAsync();
        using var scope = site.CreateScope();
        var services = scope.ServiceProvider;
        var plan = Assert.Single(services.GetRequiredService<PackageMigrationPlanCollection>().OfType<AppBlocksPackagePlan>());
        Assert.Equal("AppBlocks", plan.Name);
        Assert.Equal(plan.FinalState, services.GetRequiredService<IKeyValueService>().GetValue(Constants.Conventions.Migrations.KeyValuePrefix + plan.Name));

        using var admin = await site.SignInAsAdministratorAsync();
        var status = await admin.GetFromJsonAsync<JsonElement>(Api);
        Assert.False(status.GetProperty("uninstalled").GetBoolean());
        var package = Assert.Single(status.GetProperty("packages").EnumerateArray());
        Assert.Equal("AppBlocks", package.GetProperty("id").GetString());
        Assert.StartsWith("0.", package.GetProperty("version").GetString());
        Assert.False(package.GetProperty("pending").GetBoolean());
        Assert.Equal(plan.FinalState, package.GetProperty("state").GetString());
        var declared = package.GetProperty("declared").GetInt32();
        Assert.True(declared > 30);
        Assert.Equal(declared, package.GetProperty("installed").GetInt32());

        // What an editor might have done to the package's definitions in the backoffice.
        var types = services.GetRequiredService<IContentTypeService>();
        var dataTypes = services.GetRequiredService<IDataTypeService>();
        var strings = services.GetRequiredService<IShortStringHelper>();
        var form = types.Get("appForm")!;
        var formId = form.Id;
        var headingKey = form.PropertyTypes.Single(p => p.Alias == "heading").Key;
        form.Name = "Site form";
        form.PropertyTypes.Single(p => p.Alias == "heading").Name = "Site heading";
        form.AddPropertyType(new PropertyType(strings, (await dataTypes.GetAsync(form.PropertyTypes.Single(p => p.Alias == "heading").DataTypeKey))!) { Alias = "siteExtra", Name = "Site extra" }, "configuration");
        Assert.True((await types.UpdateAsync(form, Constants.Security.SuperUserKey)).Success);
        var grid = (await dataTypes.GetAsync(FormsContainerGrid))!;
        var gridConfiguration = JsonSerializer.Serialize(grid.ConfigurationData);
        grid.Name = "Site grid";
        grid.ConfigurationData = new Dictionary<string, object> { ["gridColumns"] = 6, ["blocks"] = Array.Empty<object>() };
        Assert.True((await dataTypes.UpdateAsync(grid, Constants.Security.SuperUserKey)).Success);
        var contents = services.GetRequiredService<IContentService>();
        var source = Assert.Single(contents.GetPagedOfType(types.Get(DataSourceSchema.SqlAlias)!.Id, 0, 10, out _, null));
        Assert.Equal("orders.lines", source.GetValue<string>("alias"));

        using var response = await admin.PostAsJsonAsync(Api + "/reinstall", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var change = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, change.GetProperty("created").GetInt32());
        Assert.Equal(declared, change.GetProperty("updated").GetInt32());
        Assert.Contains(change.GetProperty("notes").EnumerateArray(), note => note.GetString()!.Contains("'siteExtra'"));

        // The same types, by id, as the package has them again.
        var after = types.Get(form.Key)!;
        Assert.Equal(formId, after.Id);
        Assert.Equal("Form", after.Name);
        Assert.Equal("Heading", after.PropertyTypes.Single(p => p.Alias == "heading").Name);
        Assert.Equal(headingKey, after.PropertyTypes.Single(p => p.Alias == "heading").Key);
        Assert.False(after.PropertyTypeExists("siteExtra"));
        var gridAfter = (await dataTypes.GetAsync(FormsContainerGrid))!;
        Assert.Equal("AppBlocks: Forms container", gridAfter.Name);
        using (var expected = JsonDocument.Parse(gridConfiguration))
        using (var actual = JsonDocument.Parse(JsonSerializer.Serialize(gridAfter.ConfigurationData)))
            Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement));
        Assert.Contains(types.Get(DataSourceSchema.RootAlias)!.AllowedContentTypes!, child => child.Alias == DataSourceSchema.SqlAlias);

        // The site's content and its own type, which uses the package's grid, are as they were, and the page still works.
        Assert.Equal("orders.lines", contents.GetById(source.Id)!.GetValue<string>("alias"));
        Assert.Equal(FormsContainerGrid, types.Get(AppBlockSchema.PageAlias)!.PropertyTypes.Single(p => p.Alias == AppBlockSchema.BodyAlias).DataTypeKey);
        var member = site.CreateMember("definitions-" + Guid.NewGuid().ToString("N")[..8], Guid.NewGuid().ToString("N") + "@example.invalid", "Definitions member");
        using var client = await site.SignInMemberAsync(member);
        var lines = await site.Tabular(client, BlockSite.FormPageKey, BlockPages.LinesBlock).ListAsync("id=4");
        Assert.True(lines.IsSuccess, lines.Problem);
        Assert.Equal(2, lines.Rows.Count);
        status = await admin.GetFromJsonAsync<JsonElement>(Api);
        Assert.False(status.GetProperty("uninstalled").GetBoolean());
    }

    [Fact]
    public async Task A_reinstall_leaves_a_type_of_the_site_that_took_one_of_the_aliases()
    {
        await using var site = await BlockSite.BootAsync();
        using var scope = site.CreateScope();
        var services = scope.ServiceProvider;
        var types = services.GetRequiredService<IContentTypeService>();
        await WaitForInstallAsync(types);
        var packaged = types.Get("listOfValues")!;
        Assert.Equal(ContentTypeOperationStatus.Success, await types.DeleteAsync(packaged.Key, Constants.Security.SuperUserKey));
        var own = new ContentType(services.GetRequiredService<IShortStringHelper>(), -1) { Key = Guid.NewGuid(), Alias = "listOfValues", Name = "Site list", AllowedAsRoot = true };
        Assert.True((await types.CreateAsync(own, Constants.Security.SuperUserKey)).Success);

        var change = await services.GetRequiredService<SchemaInstaller>().ReinstallAsync(default);
        Assert.Contains(change.Notes, note => note.Contains("'Site list' (listOfValues)"));
        Assert.Null(types.Get(packaged.Key));
        Assert.Equal(own.Key, types.Get("listOfValues")!.Key);
        Assert.Equal("Site list", types.Get("listOfValues")!.Name);
    }

    [Fact]
    public async Task An_uninstall_refuses_while_content_or_the_site_depend_on_the_definitions_and_otherwise_removes_them_until_a_reinstall()
    {
        await using (var site = await BlockSite.StartAsync())
        {
            using var admin = await site.SignInAsAdministratorAsync();
            using var refused = await admin.PostAsJsonAsync(Api + "/uninstall", new { });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            var detail = (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!;
            Assert.Contains($"({DataSourceSchema.SqlAlias})", detail);
            Assert.Contains($"({AppBlockSchema.PageAlias})", detail);
            var status = await admin.GetFromJsonAsync<JsonElement>(Api);
            Assert.False(status.GetProperty("uninstalled").GetBoolean());
            var package = Assert.Single(status.GetProperty("packages").EnumerateArray());
            Assert.Equal(package.GetProperty("declared").GetInt32(), package.GetProperty("installed").GetInt32());
        }

        await using var bare = await BlockSite.BootAsync();
        using var scope = bare.CreateScope();
        var services = scope.ServiceProvider;
        var types = services.GetRequiredService<IContentTypeService>();
        var dataTypes = services.GetRequiredService<IDataTypeService>();
        var contentFolders = services.GetRequiredService<IContentTypeContainerService>();
        var dataFolders = services.GetRequiredService<IDataTypeContainerService>();
        await WaitForInstallAsync(types);
        var schema = services.GetServices<AppBlocksSchema>().DistinctBy(s => s.Assembly).Single();
        var declared = schema.Definitions("ContentTypes").Concat(schema.Definitions("DataTypes")).Count();
        using var administrator = await bare.SignInAsAdministratorAsync();
        var installer = services.GetRequiredService<SchemaInstaller>();
        var change = await installer.UninstallAsync(default);
        Assert.True(change.Deleted > declared, "The folders go too.");
        foreach (var definition in schema.Definitions("ContentTypes")) Assert.Null(types.Get(Guid.Parse(definition.Attribute("Key")!.Value)));
        foreach (var definition in schema.Definitions("DataTypes")) Assert.Null(await dataTypes.GetAsync(Guid.Parse(definition.Attribute("Key")!.Value)));
        Assert.DoesNotContain(await contentFolders.GetAllAsync(), folder => folder.Name == "AppBlocks");
        Assert.DoesNotContain(await dataFolders.GetAllAsync(), folder => folder.Name == "AppBlocks");
        var gone = await administrator.GetFromJsonAsync<JsonElement>(Api);
        Assert.True(gone.GetProperty("uninstalled").GetBoolean());
        Assert.Equal(0, Assert.Single(gone.GetProperty("packages").EnumerateArray()).GetProperty("installed").GetInt32());

        // A start installs nothing now.
        await installer.EnsureAsync(default);
        Assert.Null(types.Get(DataSourceSchema.SqlAlias));

        using var back = await administrator.PostAsJsonAsync(Api + "/reinstall", new { });
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.Equal(declared, (await back.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("created").GetInt32());
        foreach (var definition in schema.Definitions("ContentTypes")) Assert.NotNull(types.Get(Guid.Parse(definition.Attribute("Key")!.Value)));
        foreach (var definition in schema.Definitions("DataTypes")) Assert.NotNull(await dataTypes.GetAsync(Guid.Parse(definition.Attribute("Key")!.Value)));
        var again = await administrator.GetFromJsonAsync<JsonElement>(Api);
        Assert.False(again.GetProperty("uninstalled").GetBoolean());
        Assert.Equal(declared, Assert.Single(again.GetProperty("packages").EnumerateArray()).GetProperty("installed").GetInt32());
    }

    /// <summary>The started notification installs after the site is up; the SQL data source type is in the last of it.</summary>
    private static async Task WaitForInstallAsync(IContentTypeService types)
    {
        for (var i = 0; i < 100 && types.Get(DataSourceSchema.SqlAlias) is null; i++) await Task.Delay(100);
        Assert.NotNull(types.Get(DataSourceSchema.SqlAlias));
    }
}
