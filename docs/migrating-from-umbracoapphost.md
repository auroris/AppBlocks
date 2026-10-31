# Migrating from UmbracoAppHost

AppBlocks began as part of [UmbracoAppHost](https://github.com/auroris/UmbracoAppHost). This is what changes for code written against the old names.

UmbracoAppHost consumes AppBlocks and the optional companions it needs. Its SSO, site shells and sample content remain in AppHost. Existing content keys, routes, element and editor aliases (including the `AppHost.*` editor UI aliases in stored definitions), configuration keys and table names are retained so a site's data keeps working. `AppBlockSchema.Key` still hashes the `UmbracoAppHost.Blocks.v1:` seed, because every stored key derives from it. Legacy configured reports and report-definition content continue to run, while new reports are configured directly on blocks.

AppBlocks includes forms, attachments, tabular editing and the schedule runtime. Interactive reports live in `AppBlocks.InteractiveReports`, which depends on AppBlocks. Existing callbacks typed as `Action<InteractiveReportBuilder>` move from `AddAppBlocks` to `AddAppBlocksInteractiveReports`; report definitions move from `PublishedDataCatalog` to the companion's `PublishedReportCatalog`. Public publishing, broader provider acceptance testing and API stabilization remain release work.

### Namespaces

Every C# namespace in this repository is `AppBlocks.*`, and no namespace spans two assemblies. Code written against the extracted packages under the old names changes as follows. A name in the third column moved separately from the rest of its old namespace.

| Old namespace | New namespace | Moved separately |
| --- | --- | --- |
| `UmbracoAppHost.Attachments` | `AppBlocks.Attachments` | |
| `UmbracoAppHost.Authentication` (`ApplicationAccessPolicy`) | `AppBlocks.Authentication` | Identity helpers; AppBlocks does not override Umbraco Public Access for administrators |
| `UmbracoAppHost.Blocks` | `AppBlocks.Blocks` | `AppReportDefinitionStore`, `ConfiguredReportStore`: `AppBlocks.InteractiveReports.Blocks` |
| `UmbracoAppHost.Databases` | `AppBlocks.Databases` | `ReportApplicationDatabases` is gone: register with `AppBlocksBuilder.AddApplicationDatabase` |
| `UmbracoAppHost.DataSources` | `AppBlocks.DataSources` | `PublishedReport`, `PublishedReportCatalog`, `PublishedReportValidator`, `DataSourceReportAccess`, `DataSourceReportBindings`, `DataSourceContextParameters`, `OriginalContextParameters`: `AppBlocks.InteractiveReports.DataSources` |
| `UmbracoAppHost.Forms` | `AppBlocks.Forms` | |
| `UmbracoAppHost.PaperReports` | `AppBlocks.Documents.PaperReports` | `PaperReportLinks`: `AppBlocks.DocumentOutput` |
| `UmbracoAppHost.Reports` | `AppBlocks.InteractiveReports.Reports` | AppHost's own `ReportAccessPolicy` stays in `UmbracoAppHost.Reports` |
| `UmbracoAppHost.Schedules` | `AppBlocks.Schedules` | |
| `UmbracoAppHost.Tabular` | `AppBlocks.Tabular` | |
| `UmbracoAppHost.Website` (`PageUpdateExtensions`, `PageCacheResponseFilter`, `ProtectedPageCacheHeaders`) | `AppBlocks.Publishing` | AppHost's own types, such as `SiteMenu`, stay in `UmbracoAppHost.Website` |
| `AppBlocks.Documents`, base assembly (`IAppDocumentLinks`, `IDocumentOutputService`, `DocumentRequest` and the other output types) | `AppBlocks.DocumentOutput` | The `AppBlocks.Documents` assembly's own types stay in `AppBlocks.Documents` |
| `AppBlocks`, companion assembly (`AppBlocksOptions`) | `AppBlocks.InteractiveReports` | The base assembly's `AppBlocksExtensions` stays in `AppBlocks` |

Also when updating a host of UmbracoAppHost:

- Razor views compile at run time there, so a build does not catch an old name. A start does not rewrite a host's `Views/_ViewImports.cshtml`. Change its `@inject UmbracoAppHost.Blocks.AppPageAssets PageAssets` to `@inject AppBlocks.Blocks.AppPageAssets PageAssets` and add `@using AppBlocks.Authentication` and `@using AppBlocks.Publishing`. Check every view of its own too, paper report views especially: `@using UmbracoAppHost.PaperReports` becomes `@using AppBlocks.Documents.PaperReports`.
- Connections go through `AppBlocksBuilder`. `IUmbracoAppHost.ConfigureReports` is now `ConfigureConnections(WebApplicationBuilder, AppBlocksBuilder)`, the callback of `AddUmbracoAppHost` takes an `AppBlocksBuilder`, and `TestSiteOptions.ConfigureReports` is `ConfigureConnections`. A `reports.AddApplicationDatabase(...)` call becomes `connections.AddApplicationDatabase(...)` with the same arguments. A host that also set other report options there moves them to a composer that calls `AddAppBlocksInteractiveReports`.
- `UmbracoAppHost:DataRoot` no longer applies to AppBlocks. AppHost sets `AppBlocks:DataRoot` to the same folder, unless a host has configured `AppBlocks:DataRoot` itself. A test or tool that configures AppBlocks services directly sets `AppBlocks:DataRoot`.
