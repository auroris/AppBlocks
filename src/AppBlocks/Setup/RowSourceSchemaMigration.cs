using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace AppBlocks.Setup;

/// <summary>Restores the table picker without giving SQL sources the TextBox storage limit.</summary>
public sealed class RowSourceSchemaMigration(IDataTypeService dataTypes, IContentTypeService contentTypes,
    PropertyEditorCollection editors, IScopeProvider scopes, SchemaProperties properties) : IAppBlocksSchemaMigration
{
    private static readonly Guid TablePicker = Guid.Parse("64d2fba9-1cb2-490b-9de9-37211a072cf4");
    private static readonly Guid PlainMultiline = Guid.Parse("306b51cd-102b-ad7d-459c-be97ee8778e7");

    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (await dataTypes.GetAsync(TablePicker) is not { } picker || picker.EditorUiAlias != "AppBlocks.TablePicker"
            || picker.EditorAlias is not ("Umbraco.TextBox" or "Umbraco.TextArea")
            || picker.DatabaseType is not (ValueStorageType.Nvarchar or ValueStorageType.Ntext)) return;

        // This scope joins the installer's transaction. Preserve all versions, languages and segments of any
        // properties using the old short-text picker before switching the data type to long-text storage.
        using var scope = scopes.CreateScope();
        if (picker.EditorAlias != "Umbraco.TextArea" || picker.DatabaseType != ValueStorageType.Ntext)
        {
            if (picker.DatabaseType == ValueStorageType.Nvarchar)
                await scope.Database.ExecuteAsync("""
                    UPDATE umbracoPropertyData SET textValue = varcharValue, varcharValue = NULL
                    WHERE propertyTypeId IN (SELECT id FROM cmsPropertyType WHERE dataTypeId = @0)
                    AND varcharValue IS NOT NULL
                    """, [picker.Id], cancellationToken);
            picker.Editor = editors.Single(editor => editor.Alias == "Umbraco.TextArea");
            picker.DatabaseType = ValueStorageType.Ntext;
            var updated = await dataTypes.UpdateAsync(picker, Constants.Security.SuperUserKey);
            if (!updated.Success) throw new InvalidOperationException($"AppBlocks could not update the row source picker: {updated.Status}.");
        }

        var definition = properties.Definition("sqlDataSource");
        var type = contentTypes.Get(Guid.Parse(definition.Attribute("Key")!.Value));
        if (type?.PropertyTypes.FirstOrDefault(property => property.Alias == "source") is { } source
            && source.DataTypeKey == PlainMultiline
            && (await dataTypes.GetAsync(PlainMultiline))?.DatabaseType == picker.DatabaseType)
        {
            source.DataTypeId = picker.Id;
            source.DataTypeKey = picker.Key;
            var updated = await contentTypes.UpdateAsync(type, Constants.Security.SuperUserKey);
            if (!updated.Success) throw new InvalidOperationException("AppBlocks could not restore the row source picker.");
        }
        scope.Complete();
    }
}
