# Renamed identifiers

AppBlocks was extracted from a larger host application, and some identifiers kept that application's names at first. This page lists every name that has since changed, so code, views, configuration and stored content written against the old names can be brought up to date.

## Namespaces

Every C# namespace is `AppBlocks.*`, and no namespace spans two assemblies. A name in the third column moved separately from the rest of its old namespace.

| Old namespace | New namespace | Moved separately |
| --- | --- | --- |
| `UmbracoAppHost.Attachments` | `AppBlocks.Attachments` | |
| `UmbracoAppHost.Authentication` (`ApplicationAccessPolicy`) | `AppBlocks.Authentication` | `CanEditAsync` and `BackOfficeEditorAsync` are gone from `ApplicationAccessPolicy`. AppBlocks admits nobody Umbraco's Public Access refuses; a site that admits a page's editors registers an `IProtectedPageAdmission` |
| `UmbracoAppHost.Blocks` | `AppBlocks.Blocks` | `AppReportDefinitionStore`, `ConfiguredReportStore`: `AppBlocks.InteractiveReports.Blocks` |
| `UmbracoAppHost.Databases` | `AppBlocks.Databases` | `ReportApplicationDatabases` is gone: register with `AppBlocksBuilder.AddApplicationDatabase` |
| `UmbracoAppHost.DataSources` | `AppBlocks.DataSources` | `PublishedReport`, `PublishedReportCatalog`, `PublishedReportValidator`, `DataSourceReportAccess`, `DataSourceReportBindings`, `DataSourceContextParameters`, `OriginalContextParameters`: `AppBlocks.InteractiveReports.DataSources` |
| `UmbracoAppHost.Forms` | `AppBlocks.Forms` | |
| `UmbracoAppHost.PaperReports` | `AppBlocks.Documents.PaperReports` | `PaperReportLinks`: `AppBlocks.DocumentOutput` |
| `UmbracoAppHost.Reports` | `AppBlocks.InteractiveReports.Reports` | |
| `UmbracoAppHost.Schedules` | `AppBlocks.Schedules` | |
| `UmbracoAppHost.Tabular` | `AppBlocks.Tabular` | |
| `UmbracoAppHost.Website` (`PageUpdateExtensions`, `PageCacheResponseFilter`, `ProtectedPageCacheHeaders`) | `AppBlocks.Publishing` | |
| `AppBlocks.Documents`, base assembly (`IAppDocumentLinks`, `IDocumentOutputService`, `DocumentRequest` and the other output types) | `AppBlocks.DocumentOutput` | The `AppBlocks.Documents` assembly's own types stay in `AppBlocks.Documents` |
| `AppBlocks`, companion assembly (`AppBlocksOptions`) | `AppBlocks.InteractiveReports` | The base assembly's `AppBlocksExtensions` stays in `AppBlocks` |

Razor views compile at run time, so a build does not catch an old name in one. Search a site's views, `Views/_ViewImports.cshtml` included, for `UmbracoAppHost.`: `@inject UmbracoAppHost.Blocks.AppPageAssets PageAssets` becomes `@inject AppBlocks.Blocks.AppPageAssets PageAssets`, and `@using UmbracoAppHost.PaperReports` in a paper report template becomes `@using AppBlocks.Documents.PaperReports`.

Report callbacks typed as `Action<InteractiveReportBuilder>` move from `AddAppBlocks` to `AddAppBlocksInteractiveReports`, and report definitions move from `PublishedDataCatalog` to the companion's `PublishedReportCatalog`. A connection registered on a report builder, `reports.AddApplicationDatabase(...)`, becomes `connections.AddApplicationDatabase(...)` on the `AppBlocksBuilder` that `AddAppBlocks` hands its callback, with the same arguments.

## Editor UI aliases

The data types the package installs name the backoffice editors that draw them. The aliases are stored in every site's data types.

| Old alias | New alias |
| --- | --- |
| `AppHost.DataSourcePicker` | `AppBlocks.DataSourcePicker` |
| `AppHost.DataSourceTest` | `AppBlocks.DataSourceTest` |
| `AppHost.FieldPicker` | `AppBlocks.FieldPicker` |
| `AppHost.ResourcePicker` | `AppBlocks.ResourcePicker` |
| `AppHost.ListPicker` | `AppBlocks.ListPicker` |
| `AppHost.ReportState` | `AppBlocks.ReportState` |

The package migration steps `2026-11-08-appblocks-editor-ui-aliases` (AppBlocks) and `2026-11-08-report-state-editor` (Interactive Reports) move an existing site's data types to the new aliases at startup. A data type the site gave another editor keeps it.

## Definition keys

`AppBlockSchema.Key(name)` derives the package's definition keys from a name, hashed with a seed. The seed was `UmbracoAppHost.Blocks.v1:` and is now `AppBlocks.Blocks.v1:`, so every derived key has changed: the package's definitions, the Data sources root (`OwnedNodeInstaller.DataSourcesRootKey`) and the block keys `AppBlockValues` writes.

- **A site's definitions** installed under the old seed keep their old keys. **Settings → AppBlocks → Definitions → Reinstall** matches definitions by key and leaves alone a type of the site's own that holds one of the package's aliases, which is what an old definition now looks like to it. Rebuild such a site from scratch, or, on a site whose content no longer uses the definitions, **Uninstall** them and then **Reinstall**.
- **Code that hard-codes a definition key**, such as block grid JSON with an element type's GUID written out, recomputes it with `AppBlockSchema.Key`, or builds the value with `AppBlockValues` so it names no key at all.

## Internal prefixes

These never reach stored content, but logs and diagnostics show them.

| Old | New | Where |
| --- | --- | --- |
| `apphost:data-source:` | `appblocks:data-source:` | The request-scoped bind tokens of a report over a data source |
| `apphost-preview` | `appblocks-preview` | The claim a previewed report source's context parameters read, and the `appblocks-preview-` names of previews |

## Settings

| Old key | New key |
| --- | --- |
| `UmbracoAppHost:DataRoot` | `AppBlocks:DataRoot` |
| `Pressroom:Url`, `Pressroom:ApiKey`, `Pressroom:TimeoutSeconds` | `AppBlocks:Documents:Pressroom:Url`, `ApiKey`, `TimeoutSeconds` |

The Documents package reads only the versioned collection in `App_Data/AppBlocks/documents.json`; a settings file from before named configurations is refused, and the configurations are added again on **Settings → AppBlocks → Documents**.

## Removed and changed members

| Member | Replacement |
| --- | --- |
| `AppBlocksAuthorization.AntiforgeryTokenPath`, a constant | `AppBlocksAuthorization.AntiforgeryTokenPath(IConfiguration?)`, which follows `AppBlocks:ApiRoot` |
| `AttachmentEndpoints.Route`, a constant | `AttachmentEndpoints.Route(IConfiguration?)`, likewise |
| `ApiRouteTemplate.MaxSegments` | `ApiRouteTemplate.DefaultMaxSegments`, and the `AppBlocks:ApiEndpoints:MaxRouteSegments` setting |
| `AppBlockSchema.PageAlias`, `AppBlockSchema.BodyAlias` | None; the package ships no page type |
| `AppBlocksValidator.ValidateWebsite`, `ValidateEmbedded` | `Validate(json)`, or `Validate(json, warnings)` for the warnings too |
| `AppPageAccess.MemberAsync` | `AppPageAccess.UserAsync` |
| `DatabaseKinds.FromSchemaFolder`, `ApplicationDatabase.DataFile(IWebHostEnvironment, string)` | `ApplicationDatabase.DataFile(IServiceProvider, string)` for the file |
| `InteractiveReportsSite.ReportsConnection` and the `ReportsDb` connection | None; saved views use `InteractiveReport:SavedReports`, `AppBlocksDb` by default |
| `InteractiveReportsSite.UmbracoConnection`, `UmbracoDatabase`, `DataFile` | `ConnectionCatalog.UmbracoConnection` (`UmbracoDb`), opened as AppBlocks opens it |
| `PublishedReportCatalog.ResolveSource` | `PublishedDataCatalog.ResolveSource` |
| `PaperReportDefinition.PickedKey` | `PaperReportLinks.PickedKey` |
| `PaperReportDefinition.ResolveCulture(variables)` | `ResolveCulture(variables, requestCulture)` |
| `AddPaperReports()` | `AddAppBlocksDocuments()`, which the Documents composer already calls |
| `DocumentSettingsInfo`, `SaveDocumentSettings`, `DocumentSettingsStore.Get` and `SaveAsync` | The named Pressroom configurations |
| `PaperReportDelivery.DefaultTimeout`, `ServiceAddress`, `ServiceTimeout` | The named Pressroom configurations |
