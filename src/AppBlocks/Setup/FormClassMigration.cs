using Umbraco.Cms.Core.Services;
using UmbracoAppHost.Blocks;

namespace AppBlocks.Setup;

public sealed class FormClassMigration(IContentTypeService contentTypes, IDataTypeService dataTypes) : IAppBlocksSchemaMigration
{
    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        var form = contentTypes.Get(AppBlockSchema.Key("appFormInterface"));
        var property = form?.PropertyTypes.FirstOrDefault(p => p.Alias == "resource");
        if (property is null) return;
        var current = await dataTypes.GetAsync(Guid.Parse("01f7ca62-d968-44c8-a514-4847c73f70de"));
        if (current is null || property.DataTypeId == current.Id) return;
        property.DataTypeId = current.Id;
        property.Name = "Form class";
        var result = await contentTypes.UpdateAsync(form!, Umbraco.Cms.Core.Constants.Security.SuperUserKey);
        if (!result.Success) throw new InvalidOperationException("AppBlocks could not update the form class picker.");
    }
}
