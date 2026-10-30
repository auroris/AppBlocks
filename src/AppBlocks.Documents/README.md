# AppBlocks.Documents

Generate templated documents from AppBlocks data sources. Documents open in a new window as self-contained HTML, or as PDF when an optional Pressroom service is configured.

Install `AppBlocks.Documents` alongside `AppBlocks`. The package depends on AppBlocks, but does not require AppHost or Interactive Reports. Its Umbraco composer registers the document engine, routes, settings page and schema automatically. It uses the existing `paperReport` content alias and `/api/paper-reports/{key}` route so existing content and links continue working. Existing Razor templates can keep `UmbracoAppHost.PaperReports.PaperReportModel`.

## Settings

Open **Settings → AppBlocks → Documents** as an administrator. Save an optional Pressroom URL, API key and timeout. Pressroom requires a key by default; the key may be omitted only when Pressroom permits anonymous PDF rendering. Leaving the URL empty selects HTML. Settings apply immediately, including on localhost. A Pressroom failure opens the same HTML document and records the exception through Umbraco's normal `ILogger` pipeline. Both formats are delivered inline for browser viewing and printing.

Settings are kept in `App_Data/AppBlocks/documents.json` beneath `AppBlocks:DataRoot`, or `UmbracoAppHost:DataRoot`, or the site's content root. API keys are protected with the site's Data Protection keys and never returned by the settings API. Back up those keys with the settings file. As with AppBlocks connections, this file store is intended for a single application instance.

Until settings are saved in the backoffice, configuration under `AppBlocks:Documents:Pressroom` (`Url`, `ApiKey`, `TimeoutSeconds`) supplies the defaults. Legacy `Pressroom:*` settings are also supported. A saved blank URL disables Pressroom even if configuration provides a URL. An empty API-key field keeps the existing key; the explicit removal checkbox clears it.

## Author a document

Create a **Document** (`paperReport`) content node, choose a published SQL data source, and select a Razor template that accepts `PaperReportModel`. Documents can be created at the root; to nest one under a site's page, allow Document beneath that site's document type. A site's own templates supply its content and layout. The package adds no sample content or branded templates.

Templates receive rows and columns, bound parameters, the signed-in person, culture, generation time and presentation name. Access is checked against both Umbraco Public Access and the data source's read permissions. The configured row limit still applies. Parameters, culture, filenames and Pressroom page options behave as in AppHost's original paper reports. `?format=html` explicitly requests HTML even with Pressroom configured.

Register `IDocumentPresentationProvider` to provide a theme name and optional print stylesheet. The default uses the package's neutral print stylesheet. AppHost supplies its own implementation for its two site shells.

## Companion integration

`IAppDocumentLinks` lives in the base AppBlocks assembly, in the `AppBlocks.Documents` namespace. Resolve it optionally with `GetService<IAppDocumentLinks>()`. If absent, omit document actions. If present, `Url(documentKey, request.PathBase)` supplies the endpoint. Append the record parameters and use `target="_blank" rel="noopener"` on links, or `window.open(url, "_blank", "noopener")` synchronously inside a user action. The endpoint rechecks authorization; knowing a document URL grants no access.

Interactive Reports' `app-print` action and report-state handling belong to that companion, not Documents. Documents never references Interactive Reports. The settings screen contributes a Documents view through `AppBlocksDashboardPage`; the base dashboard discovers installed companions.

## Build

Source builds use sibling `../Pressroom` when available, overridable with `PressroomRoot`. Set `UsePressroomSource=false` to resolve `Pressroom.Client` 0.1.0 from a package feed. No browser or PDF engine is installed in the Umbraco process.

```powershell
dotnet build src/AppBlocks.Documents
dotnet run --project tests/AppBlocks.Documents.Tests -- -noLogo
dotnet pack src/AppBlocks.Documents -c Release -o artifacts/packages
```
