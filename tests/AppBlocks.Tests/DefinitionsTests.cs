using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AppBlocks.Api;
using AppBlocks.Blocks;
using AppBlocks.DataSources;
using AppBlocks.Setup;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Packaging;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Serialization;
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
    public async Task Single_Block_upgrade_changes_only_the_packages_interface_and_is_safe_to_repeat()
    {
        await using var site = await BlockSite.StartAsync();
        using var scope = site.CreateScope();
        var services = scope.ServiceProvider;
        var dataTypes = services.GetRequiredService<IDataTypeService>();
        var types = services.GetRequiredService<IContentTypeService>();
        var editors = services.GetRequiredService<PropertyEditorCollection>();
        var serializer = services.GetRequiredService<IConfigurationEditorJsonSerializer>();
        var keyValues = services.GetRequiredService<IKeyValueService>();
        var current = (await dataTypes.GetAsync(UseSingleBlockFormInterface.DataTypeKey))!;
        var originalId = current.Id;
        Assert.Equal("Umbraco.SingleBlock", current.EditorAlias);
        Assert.Equal("Umb.PropertyEditorUi.BlockSingle", current.EditorUiAlias);
        Assert.True(types.Get("appForm")!.PropertyTypes.Single(p => p.Alias == "formInterface").Mandatory);

        var listEditor = editors.Single(e => e.Alias == "Umbraco.BlockList");
        var oldConfiguration = new Dictionary<string, object> {
            ["blocks"] = new[] { new { contentElementTypeKey = AppBlockSchema.Key("appFormInterface") } },
            ["validationLimit"] = new { min = 1, max = 1 }
        };
        current.Editor = listEditor;
        current.EditorUiAlias = "Umb.PropertyEditorUi.BlockList";
        current.ConfigurationData = oldConfiguration;
        Assert.True((await dataTypes.UpdateAsync(current, Constants.Security.SuperUserKey)).Success);
        var host = new DataType(listEditor, serializer) {
            Key = Guid.NewGuid(), Name = "Host-owned single-item list", EditorUiAlias = "Umb.PropertyEditorUi.BlockList",
            ConfigurationData = oldConfiguration
        };
        Assert.True((await dataTypes.CreateAsync(host, Constants.Security.SuperUserKey)).Success);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            keyValues.SetValue(Constants.Conventions.Migrations.KeyValuePrefix + "AppBlocks", "2026-11-02-package-install");
            await RunPlanAsync(services);
            var upgraded = (await dataTypes.GetAsync(current.Key))!;
            Assert.Equal(originalId, upgraded.Id);
            Assert.Equal("Umbraco.SingleBlock", upgraded.EditorAlias);
            Assert.Equal("Umb.PropertyEditorUi.BlockSingle", upgraded.EditorUiAlias);
            var configuration = Assert.IsType<SingleBlockConfiguration>(upgraded.ConfigurationObject);
            Assert.Equal(AppBlockSchema.Key("appFormInterface"), Assert.Single(configuration.Blocks).ContentElementTypeKey);
            Assert.False(upgraded.ConfigurationData.ContainsKey("validationLimit"));
            Assert.Equal("Umbraco.SingleBlock", types.Get("appForm")!.PropertyTypes.Single(p => p.Alias == "formInterface").PropertyEditorAlias);
            var untouched = (await dataTypes.GetAsync(host.Key))!;
            Assert.Equal("Umbraco.BlockList", untouched.EditorAlias);
            Assert.IsType<BlockListConfiguration>(untouched.ConfigurationObject);
        }
    }

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
        var source = Assert.Single(contents.GetPagedOfTypes([types.Get(DataSourceSchema.SqlAlias)!.Id], 0, 10, out _, null));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrency_upgrade_removes_version_columns_and_preserves_explicit_choices(bool hasMode)
    {
        await using var site = await BlockSite.StartAsync();
        using var scope = site.CreateScope();
        var services = scope.ServiceProvider;
        var types = services.GetRequiredService<IContentTypeService>();
        var dataTypes = services.GetRequiredService<IDataTypeService>();
        var type = types.Get(DataSourceSchema.SqlAlias)!;
        var mode = type.PropertyTypes.Single(p => p.Alias == "concurrencyMode");
        var picker = (await dataTypes.GetAsync(mode.DataTypeKey))!;
        Assert.Equal("Umbraco.RadioButtonList", picker.EditorAlias);
        var options = JsonSerializer.SerializeToElement(picker.ConfigurationData).GetProperty("items").EnumerateArray().Select(v => v.GetString()).ToArray();
        Assert.Equal(Enum.GetNames<DataSourceConcurrency>(), options);
        var contents = services.GetRequiredService<IContentService>();
        var source = Assert.Single(contents.GetPagedOfTypes([type.Id], 0, 10, out _, null));
        Assert.False(type.PropertyTypeExists("concurrency"));

        // Recreate either the original text field or the three-choice preview schema.
        var strings = services.GetRequiredService<IShortStringHelper>();
        var textType = (await dataTypes.GetAsync(new Guid("90cb2487-adcb-2ec5-c4a0-840175ba5424")))!;
        type.AddPropertyType(new PropertyType(strings, textType) { Alias = "concurrency", Name = "Version column" }, "writes");
        if (!hasMode) type.RemovePropertyType("concurrencyMode");
        Assert.True((await types.UpdateAsync(type, Constants.Security.SuperUserKey)).Success);
        source = contents.GetById(source.Id)!;
        source.SetValue("concurrency", "VERSION");
        if (hasMode)
        {
            source.SetValue("concurrencyMode", "VersionColumn");
            picker.ConfigurationData = new Dictionary<string, object> { ["items"] = new[] { "LastSaveWins", "Checksum", "VersionColumn" } };
            Assert.True((await dataTypes.UpdateAsync(picker, Constants.Security.SuperUserKey)).Success);
        }
        else Assert.True((await dataTypes.DeleteAsync(picker.Key, Constants.Security.SuperUserKey)).Success);
        contents.Save(source);
        var lastSaveWins = contents.Create("Last save wins", source.ParentId, DataSourceSchema.SqlAlias);
        lastSaveWins.SetValue("concurrency", hasMode ? "VERSION" : "");
        if (hasMode) lastSaveWins.SetValue("concurrencyMode", nameof(DataSourceConcurrency.LastSaveWins));
        contents.Save(lastSaveWins);
        services.GetRequiredService<IKeyValueService>().SetValue(Constants.Conventions.Migrations.KeyValuePrefix + "AppBlocks",
            hasMode ? "2026-11-04-data-source-concurrency" : "2026-11-03-api-endpoints");
        await RunPlanAsync(services);
        type = types.Get(DataSourceSchema.SqlAlias)!;
        Assert.Single(type.PropertyTypes, p => p.Alias == "concurrencyMode");
        Assert.False(type.PropertyTypeExists("concurrency"));
        Assert.Equal(nameof(DataSourceConcurrency.Checksum), contents.GetById(source.Id)!.GetValue<string>("concurrencyMode"));
        Assert.Equal(hasMode ? nameof(DataSourceConcurrency.LastSaveWins) : null, contents.GetById(lastSaveWins.Id)!.GetValue<string>("concurrencyMode"));
        picker = (await dataTypes.GetAsync(mode.DataTypeKey))!;
        options = JsonSerializer.SerializeToElement(picker.ConfigurationData).GetProperty("items").EnumerateArray().Select(v => v.GetString()).ToArray();
        Assert.Equal(Enum.GetNames<DataSourceConcurrency>(), options);
        await RunPlanAsync(services);
        Assert.Single(types.Get(DataSourceSchema.SqlAlias)!.PropertyTypes, p => p.Alias == "concurrencyMode");
    }

    [Fact]
    public async Task The_backing_class_step_gives_an_existing_site_what_a_fresh_install_has_and_skips_a_site_without_the_type()
    {
        await using (var site = await BlockSite.StartAsync())
        {
            using var scope = site.CreateScope();
            var services = scope.ServiceProvider;
            var types = services.GetRequiredService<IContentTypeService>();
            var dataTypes = services.GetRequiredService<IDataTypeService>();
            var fresh = types.Get(DataSourceSchema.SqlAlias)!.PropertyTypes.Single(p => p.Alias == AddDataSourceBackingClass.Property);
            var freshGroup = types.Get(DataSourceSchema.SqlAlias)!.PropertyGroups.Single(g => g.PropertyTypes!.Contains(fresh.Alias)).Alias;
            var picker = (await dataTypes.GetAsync(fresh.DataTypeKey))!;
            Assert.Equal("AppBlocks.BackingClassPicker", picker.EditorUiAlias);
            // The picker lists the registered backing classes, of which this site has none.
            using (var admin = await site.SignInAsAdministratorAsync())
                Assert.Empty((await admin.GetFromJsonAsync<JsonElement>("/umbraco/management/api/v1/application-blocks/catalogue")).GetProperty("dataSourceHandlers").EnumerateArray());

            // The site as it was before the step: no property, no picker data type, and the plan at its first state.
            await BeforeTheStepAsync(services);
            var source = Assert.Single(services.GetRequiredService<IContentService>().GetPagedOfTypes([types.Get(DataSourceSchema.SqlAlias)!.Id], 0, 10, out _, null));
            await RunPlanAsync(services);

            var type = types.Get(DataSourceSchema.SqlAlias)!;
            var added = type.PropertyTypes.Single(p => p.Alias == AddDataSourceBackingClass.Property);
            Assert.Equal((fresh.Key, fresh.Name, fresh.Description, fresh.SortOrder, fresh.Mandatory, fresh.DataTypeKey),
                (added.Key, added.Name, added.Description, added.SortOrder, added.Mandatory, added.DataTypeKey));
            Assert.Equal(freshGroup, type.PropertyGroups.Single(g => g.PropertyTypes!.Contains(added.Alias)).Alias);
            var restored = (await dataTypes.GetAsync(fresh.DataTypeKey))!;
            Assert.Equal((picker.Name, picker.EditorUiAlias, picker.EditorAlias, picker.ParentId), (restored.Name, restored.EditorUiAlias, restored.EditorAlias, restored.ParentId));
            Assert.Equal("orders.lines", services.GetRequiredService<IContentService>().GetById(source.Id)!.GetValue<string>("alias"));

            // Run again over a site that has it: nothing changes.
            services.GetRequiredService<IKeyValueService>().SetValue(Constants.Conventions.Migrations.KeyValuePrefix + "AppBlocks", "2026-11-02-tracked");
            await RunPlanAsync(services);
            Assert.Single(types.Get(DataSourceSchema.SqlAlias)!.PropertyTypes, p => p.Alias == AddDataSourceBackingClass.Property);
        }

        await using var bare = await BlockSite.BootAsync();
        using var bareScope = bare.CreateScope();
        var bareServices = bareScope.ServiceProvider;
        var bareTypes = bareServices.GetRequiredService<IContentTypeService>();
        await WaitForInstallAsync(bareTypes);
        var pickerKey = bareTypes.Get(DataSourceSchema.SqlAlias)!.PropertyTypes.Single(p => p.Alias == AddDataSourceBackingClass.Property).DataTypeKey;
        Assert.Equal(ContentTypeOperationStatus.Success, await bareTypes.DeleteAsync(bareTypes.Get(DataSourceSchema.SqlAlias)!.Key, Constants.Security.SuperUserKey));
        await BeforeTheStepAsync(bareServices);
        await RunPlanAsync(bareServices);
        // The later installation step restores missing definitions in the same package plan.
        Assert.NotNull(bareTypes.Get(DataSourceSchema.SqlAlias));
        Assert.NotNull(await bareServices.GetRequiredService<IDataTypeService>().GetAsync(pickerKey));
    }

    [Fact]
    public async Task An_existing_site_gains_API_endpoints_and_their_root()
    {
        await using var site = await BlockSite.BootAsync();
        using var scope = site.CreateScope();
        var services = scope.ServiceProvider;
        var types = services.GetRequiredService<IContentTypeService>();
        var dataTypes = services.GetRequiredService<IDataTypeService>();
        await WaitForInstallAsync(types);
        var fresh = types.Get(ApiEndpointCatalog.Alias)!;
        var editors = fresh.PropertyTypes.Select(p => p.DataTypeKey).ToHashSet();
        string[] Allowed() => types.Get(DataSourceSchema.RootAlias)!.AllowedContentTypes!.OrderBy(c => c.SortOrder).Select(c => c.Alias).ToArray();
        Assert.Equal("apiEndpoint", Allowed()[^1]);

        var container = types.Get(ApiEndpointCatalog.RootAlias)!;

        // The site as it was before the step: no endpoint type or root, none of their data types, and the plan at the step before.
        Assert.Equal(ContentTypeOperationStatus.Success, await types.DeleteAsync(container.Key, Constants.Security.SuperUserKey));
        Assert.Equal(ContentTypeOperationStatus.Success, await types.DeleteAsync(fresh.Key, Constants.Security.SuperUserKey));
        var own = new List<Guid>();
        foreach (var dataType in await dataTypes.GetAllAsync())
            if (dataType.EditorUiAlias is "AppBlocks.ApiRoute" or "AppBlocks.ApiEndpointTest" || dataType.Name == "Application: API shape")
            {
                own.Add(dataType.Key);
                Assert.True((await dataTypes.DeleteAsync(dataType.Key, Constants.Security.SuperUserKey)).Success);
            }
        Assert.Equal(3, own.Count);
        Assert.DoesNotContain("apiEndpoint", Allowed());
        services.GetRequiredService<IKeyValueService>().SetValue(Constants.Conventions.Migrations.KeyValuePrefix + "AppBlocks", "2026-11-02-single-block-form-interface");
        await RunPlanAsync(services);

        var added = types.Get(ApiEndpointCatalog.Alias)!;
        Assert.Equal(fresh.Key, added.Key);
        Assert.Equal(fresh.PropertyTypes.Select(p => (p.Alias, p.DataTypeKey)).Order(), added.PropertyTypes.Select(p => (p.Alias, p.DataTypeKey)).Order());
        foreach (var key in own) Assert.Contains(key, editors);
        foreach (var key in editors) Assert.NotNull(await dataTypes.GetAsync(key));
        Assert.Equal(["sqlDataSource", "listOfValues", "apiEndpoint"], Allowed());
        var root = types.Get(ApiEndpointCatalog.RootAlias)!;
        Assert.Equal((container.Key, true), (root.Key, root.AllowedAsRoot));
        Assert.Equal(["apiEndpoint"], root.AllowedContentTypes!.Select(c => c.Alias));

        // Run again over a site that has it: nothing changes.
        services.GetRequiredService<IKeyValueService>().SetValue(Constants.Conventions.Migrations.KeyValuePrefix + "AppBlocks", "2026-11-02-single-block-form-interface");
        await RunPlanAsync(services);
        Assert.Equal(["sqlDataSource", "listOfValues", "apiEndpoint"], Allowed());
    }

    [Fact]
    public async Task The_date_time_and_list_access_step_gives_an_existing_site_both_properties_and_tolerates_a_missing_type()
    {
        await using var site = await BlockSite.BootAsync();
        using var scope = site.CreateScope();
        var services = scope.ServiceProvider;
        var types = services.GetRequiredService<IContentTypeService>();
        var dataTypes = services.GetRequiredService<IDataTypeService>();
        await WaitForInstallAsync(types);
        var mode = types.Get(DataSourceSchema.SqlAlias)!.PropertyTypes.Single(p => p.Alias == AddDateTimeModeAndListAccess.DateTimeProperty);
        var audit = types.Get(DataSourceSchema.SqlAlias)!.PropertyTypes.Single(p => p.Alias == "auditColumns").Description;
        var access = types.Get(PublishedDataCatalog.ListAlias)!.PropertyTypes.Single(p => p.Alias == AddDateTimeModeAndListAccess.AccessProperty);
        Assert.Equal("Application: Data source date-times", (await dataTypes.GetAsync(mode.DataTypeKey))!.Name);

        // The site as it was before the step: neither property, no date-time data type, the old audit description, and the plan at the step before.
        var source = types.Get(DataSourceSchema.SqlAlias)!;
        source.RemovePropertyType(mode.Alias);
        source.PropertyTypes.Single(p => p.Alias == "auditColumns").Description = "The engine writes the username and the server's local time.";
        Assert.True((await types.UpdateAsync(source, Constants.Security.SuperUserKey)).Success);
        Assert.True((await dataTypes.DeleteAsync(mode.DataTypeKey, Constants.Security.SuperUserKey)).Success);
        var list = types.Get(PublishedDataCatalog.ListAlias)!;
        list.RemovePropertyType(access.Alias);
        Assert.True((await types.UpdateAsync(list, Constants.Security.SuperUserKey)).Success);
        var keyValues = services.GetRequiredService<IKeyValueService>();
        keyValues.SetValue(Constants.Conventions.Migrations.KeyValuePrefix + "AppBlocks", "2026-11-04-checksum-concurrency");
        await RunPlanAsync(services);

        var added = types.Get(DataSourceSchema.SqlAlias)!.PropertyTypes.Single(p => p.Alias == mode.Alias);
        Assert.Equal((mode.Key, mode.Name, mode.Description, mode.DataTypeKey), (added.Key, added.Name, added.Description, added.DataTypeKey));
        Assert.NotNull(await dataTypes.GetAsync(mode.DataTypeKey));
        Assert.Equal(audit, types.Get(DataSourceSchema.SqlAlias)!.PropertyTypes.Single(p => p.Alias == "auditColumns").Description);
        var restored = types.Get(PublishedDataCatalog.ListAlias)!.PropertyTypes.Single(p => p.Alias == access.Alias);
        Assert.Equal((access.Key, access.DataTypeKey, access.Mandatory), (restored.Key, restored.DataTypeKey, restored.Mandatory));

        // A site without the list type: the step adds what it can and the plan still completes.
        Assert.Equal(ContentTypeOperationStatus.Success, await types.DeleteAsync(types.Get(PublishedDataCatalog.ListAlias)!.Key, Constants.Security.SuperUserKey));
        keyValues.SetValue(Constants.Conventions.Migrations.KeyValuePrefix + "AppBlocks", "2026-11-04-checksum-concurrency");
        await RunPlanAsync(services);
        Assert.Null(types.Get(PublishedDataCatalog.ListAlias));
        Assert.Single(types.Get(DataSourceSchema.SqlAlias)!.PropertyTypes, p => p.Alias == mode.Alias);
    }

    /// <summary>Takes the backing class away again, as a site that has not run its step has it, and sets the plan back before the step.</summary>
    private static async Task BeforeTheStepAsync(IServiceProvider services)
    {
        var types = services.GetRequiredService<IContentTypeService>();
        var dataTypes = services.GetRequiredService<IDataTypeService>();
        Guid? picker = null;
        if (types.Get(DataSourceSchema.SqlAlias) is { } type)
        {
            picker = type.PropertyTypes.Single(p => p.Alias == AddDataSourceBackingClass.Property).DataTypeKey;
            type.RemovePropertyType(AddDataSourceBackingClass.Property);
            Assert.True((await types.UpdateAsync(type, Constants.Security.SuperUserKey)).Success);
        }
        foreach (var dataType in await dataTypes.GetAllAsync())
            if (dataType.EditorUiAlias == "AppBlocks.BackingClassPicker" && (picker is null || dataType.Key == picker))
                Assert.True((await dataTypes.DeleteAsync(dataType.Key, Constants.Security.SuperUserKey)).Success);
        services.GetRequiredService<IKeyValueService>().SetValue(Constants.Conventions.Migrations.KeyValuePrefix + "AppBlocks", "2026-11-02-tracked");
    }

    private static async Task RunPlanAsync(IServiceProvider services)
    {
        var results = await services.GetRequiredService<Umbraco.Cms.Infrastructure.Install.PackageMigrationRunner>().RunPackagePlansAsync(["AppBlocks"]);
        Assert.All(results, result => Assert.True(result.Successful, result.Exception?.ToString()));
        var plan = Assert.Single(services.GetRequiredService<PackageMigrationPlanCollection>().OfType<AppBlocksPackagePlan>());
        Assert.Equal(plan.FinalState, services.GetRequiredService<IKeyValueService>().GetValue(Constants.Conventions.Migrations.KeyValuePrefix + plan.Name));
    }

    /// <summary>The package migration must have installed definitions before the site is used.</summary>
    private static async Task WaitForInstallAsync(IContentTypeService types)
    {
        for (var i = 0; i < 100 && types.Get(DataSourceSchema.SqlAlias) is null; i++) await Task.Delay(100);
        Assert.NotNull(types.Get(DataSourceSchema.SqlAlias));
    }
}
