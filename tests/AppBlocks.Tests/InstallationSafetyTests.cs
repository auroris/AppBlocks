using AppBlocks.Blocks;
using AppBlocks.Setup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;

namespace AppBlocks.Tests;

[Collection("Standalone Umbraco")]
public sealed class InstallationSafetyTests
{
    [Fact]
    public void Package_plan_should_resume_from_its_recorded_state()
        => Assert.False(new AppBlocksMigrationPlan().IgnoreCurrentState);

    [Fact]
    public async Task Uninstall_should_refuse_embedded_blocks_in_a_site_owned_grid()
    {
        await using var site = await BlockSite.BootAsync();
        using var scope = site.Services.CreateScope();
        var services = scope.ServiceProvider;
        var installer = services.GetRequiredService<SchemaInstaller>();
        await installer.EnsureAsync(default);
        var types = services.GetRequiredService<IContentTypeService>();
        var dataTypes = services.GetRequiredService<IDataTypeService>();
        var strings = services.GetRequiredService<IShortStringHelper>();
        var grid = new DataType(services.GetRequiredService<PropertyEditorCollection>()["Umbraco.BlockGrid"], services.GetRequiredService<IConfigurationEditorJsonSerializer>())
        {
            Key = Guid.NewGuid(), Name = "Review host grid", EditorUiAlias = "Umb.PropertyEditorUi.BlockGrid",
            ConfigurationData = new Dictionary<string, object>
            {
                ["gridColumns"] = 12,
                ["blocks"] = new[] { new { contentElementTypeKey = AppBlockSchema.Key(AppBlockSchema.FormsContainerAlias) } }
            }
        };
        Assert.True((await dataTypes.CreateAsync(grid, Constants.Security.SuperUserKey)).Success);
        var pageType = new ContentType(strings, -1) { Key = Guid.NewGuid(), Alias = "reviewHostPage", Name = "Review host page", AllowedAsRoot = true };
        pageType.AddPropertyType(new PropertyType(strings, grid) { Alias = "body", Name = "Body" });
        Assert.True((await types.CreateAsync(pageType, Constants.Security.SuperUserKey)).Success);
        var contents = services.GetRequiredService<IContentService>();
        var page = contents.Create("Host page with AppBlocks", -1, pageType.Alias);
        var blocks = BlockPages.Empty();
        BlockPages.Layout(blocks).Add(BlockPages.Add(blocks, AppBlockSchema.FormsContainerAlias, ("forms", BlockPages.Empty().ToJsonString())));
        page.SetValue("body", blocks.ToJsonString());
        Assert.True(contents.Save(page).Success);
        Assert.True(contents.Publish(page, ["*"]).Success);
        Assert.False(types.HasContentNodes(types.Get(AppBlockSchema.FormsContainerAlias)!.Id));
        await Assert.ThrowsAsync<SchemaInUseException>(() => installer.UninstallAsync(default));
        // Removing the picker option does not remove blocks already saved in content.
        grid.ConfigurationData = new Dictionary<string, object> { ["gridColumns"] = 12, ["blocks"] = Array.Empty<object>() };
        Assert.True((await dataTypes.UpdateAsync(grid, Constants.Security.SuperUserKey)).Success);
        var used = await Assert.ThrowsAsync<SchemaInUseException>(() => installer.UninstallAsync(default));
        Assert.Contains(used.Problems, problem => problem.Contains(page.Name!) && problem.Contains("body"));
        Assert.NotNull(types.Get(AppBlockSchema.FormsContainerAlias));
    }

    [Fact]
    public async Task Schema_install_should_honor_the_Umbraco_schema_migration_switch()
    {
        await using var site = await BlockSite.BootAsync(configure: builder => builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Umbraco:CMS:PackageMigration:RunSchemaAndContentMigrations"] = "false",
            ["Umbraco:CMS:PackageMigration:AllowComponentOverrideOfRunSchemaAndContentMigrations"] = "false"
        }));
        using var scope = site.Services.CreateScope();
        var types = scope.ServiceProvider.GetRequiredService<IContentTypeService>();
        await scope.ServiceProvider.GetRequiredService<SchemaInstaller>().EnsureAsync(default);
        Assert.Null(types.Get("sqlDataSource"));
    }
}

