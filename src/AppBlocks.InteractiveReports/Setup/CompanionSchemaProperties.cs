using System.Xml.Linq;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using AppBlocks.Setup;

namespace AppBlocks.InteractiveReports.Setup;

/// <summary>Applies the companion's schema and its core data-type dependencies through the package migration contract.</summary>
internal sealed class CompanionSchemaProperties(IContentTypeService contentTypes, IDataTypeService dataTypes, IDataTypeContainerService dataFolders,
    PropertyEditorCollection editors, IConfigurationEditorJsonSerializer serializer, IShortStringHelper strings)
{
    public Task AddMissingAsync(IContentType type, XElement definition, IEnumerable<string> aliases)
        => new SchemaMigration(contentTypes, dataTypes, dataFolders, editors, serializer, strings).AddMissingAsync(type, definition, aliases,
            [new(typeof(CompanionSchemaProperties).Assembly), new(typeof(SchemaInstaller).Assembly)]);
}
