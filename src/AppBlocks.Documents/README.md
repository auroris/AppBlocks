# AppBlocks.Documents

Generate templated documents from AppBlocks data sources and provide a shared Pressroom gateway for other companions. Browser documents open as self-contained HTML or PDF; callers can also explicitly submit documents to configured printers.

Install `AppBlocks.Documents` alongside `AppBlocks`. The package depends on AppBlocks, but does not require AppHost or Interactive Reports. Its Umbraco composer registers the document engine, routes, settings page and schema automatically. It uses the existing `paperReport` content alias and `/api/paper-reports/{key}` route so existing content and links continue working. Existing Razor templates can keep `UmbracoAppHost.PaperReports.PaperReportModel`.

## Settings

Open **Settings → AppBlocks → Documents** as an administrator. Add, edit or remove named Pressroom configurations. Each has a URL, API key, output and timeout. **PDF** is always available in the output dropdown; **Load printers** queries that server's `GET /v1/printers` and adds the returned printer identifiers. Printer discovery and printer output require a key. PDF can omit the key when Pressroom permits anonymous rendering. Saving a printer configuration checks that the server still exposes that printer.

Configurations have stable IDs and apply immediately. Several configurations can use the same server with different outputs or credentials. Saved keys are never sent to the browser; a blank key field preserves the existing key. Use **Remove the saved API key** to clear it. Changing the URL requires a new key or explicit key removal, so an existing credential is not silently sent to another server.

Existing template-document links use the first PDF configuration. With none, or with `?format=html`, they open self-contained HTML. These GET requests never submit to a printer. PDF failures fall back to HTML and are logged through Umbraco's `ILogger` pipeline. Printer failures are reported separately and are never retried or silently converted to a different output.

Settings are kept in `App_Data/AppBlocks/documents.json` beneath `AppBlocks:DataRoot`, or `UmbracoAppHost:DataRoot`, or the site's content root. API keys are protected with the site's Data Protection keys and never returned by the settings API. Back up those keys with the settings file. As with AppBlocks connections, this file store is intended for a single application instance.

Until settings are saved in the backoffice, configuration under `AppBlocks:Documents:Pressroom` (`Url`, `ApiKey`, `TimeoutSeconds`) supplies a **Pressroom PDF** configuration. Legacy `Pressroom:*` settings and the previous single-configuration JSON file are supported, preserving their protected key. The first edit writes the versioned collection. Deleting all configurations persists an empty collection; configuration-file defaults do not reappear. An old saved blank URL remains disabled.

## Author a document

Create a **Document** (`paperReport`) content node, choose a published SQL data source, and select a Razor template that accepts `PaperReportModel`. Documents can be created at the root; to nest one under a site's page, allow Document beneath that site's document type. A site's own templates supply its content and layout. The package adds no sample content or branded templates.

Templates receive rows and columns, bound parameters, the signed-in person, culture, generation time and presentation name. Access is checked against both Umbraco Public Access and the data source's read permissions. The configured row limit still applies. Parameters, culture, filenames and Pressroom page options behave as in AppHost's original paper reports. `?format=html` explicitly requests HTML even with Pressroom configured.

Register `IDocumentPresentationProvider` to provide a theme name and optional print stylesheet. The default uses the package's neutral print stylesheet. AppHost supplies its own implementation for its two site shells.

## Companion integration

`IAppDocumentLinks` lives in the base AppBlocks assembly, in the `AppBlocks.Documents` namespace. Resolve it optionally with `GetService<IAppDocumentLinks>()`. If absent, omit document actions. If present, `Url(documentKey, request.PathBase)` supplies the endpoint. Append the record parameters and use `target="_blank" rel="noopener"` on links, or `window.open(url, "_blank", "noopener")` synchronously inside a user action. The endpoint rechecks authorization; knowing a document URL grants no access.

Interactive Reports' UI, authorization and report-state handling belong to that companion. Documents owns all Pressroom HTTP interactions, configuration and credentials. Documents never references Interactive Reports. The settings screen contributes a Documents view through `AppBlocksDashboardPage`; the base dashboard discovers installed companions.

### Shared output service

The stable optional contract is [`IDocumentOutputService`](../AppBlocks/Documents/IDocumentOutputService.cs), in the **base AppBlocks assembly**, namespace `AppBlocks.Documents`. Consumers need no dependency on Pressroom or the Documents implementation assembly. Resolve it with `GetService<IDocumentOutputService>()`; omit the output/Print UI when it is absent or `GetDestinations()` returns no destinations.

- `GetDestinations()` returns `DocumentDestination(Id, Name, Output, Printer)`. `Id` is a GUID; `Output` is `DocumentOutputKind.Pdf` or `Printer`. These descriptors contain no service URLs or credentials.
- `SubmitAsync(configurationId, new DocumentRequest(html, fileName, options), cancellationToken)` sends the complete self-contained HTML to the selected configuration. `DocumentRenderOptions` and its page-size, margins and readiness records belong to AppBlocks. Do not send client-supplied arbitrary HTML or rows without the consuming application's own validation and authorization.
- A `DocumentFileResult` owns `Content` (a stream), `ContentType` and `FileName`. `IsHtmlFallback` distinguishes PDF-service failure from successful PDF output. Keep the result alive until copying/streaming finishes, then dispose it. ASP.NET Core handlers can register it with `http.Response.RegisterForDispose(result)` and return `Results.Stream(file.Content, file.ContentType)` with an inline Content-Disposition header.
- A `DocumentPrintResult` contains `JobId`, `Printer` and `PageCount`. This confirms **submission**, not physical completion or a Windows spooler job ID.
- A `DocumentPrintException` carries `MayHavePrinted`. An uncertain response, timeout or network failure may occur after submission; tell the user to check the queue before offering a new submission. Caller cancellation can also leave the outcome unknown. Never automatically retry a print request.

The service performs no caller authorization. The consumer must authorize the source data and selected destination, render trusted HTML, and invoke printing only from an explicit user action through a protected POST. It must not invoke printing during a GET, preview or background retry. Removed configuration IDs throw `KeyNotFoundException`; they never switch to another configuration. `DocumentOutputResult` implements both `IDisposable` and `IAsyncDisposable`.

```csharp
var outputs = services.GetService<IDocumentOutputService>();
var destinations = outputs?.GetDestinations() ?? [];
// After authorization and the user's explicit selection/submission:
await using var result = await outputs!.SubmitAsync(selectedId,
    new DocumentRequest(trustedHtml, "report.pdf", new() {
        PageSize = new DocumentPageSize("A4"), Landscape = true,
    }), cancellationToken);
switch (result)
{
    case DocumentFileResult file:
        await file.Content.CopyToAsync(destinationStream, cancellationToken);
        break;
    case DocumentPrintResult receipt:
        // Display a submission receipt; do not claim physical printing is complete.
        break;
}
```

The administrator-only management API is under `/umbraco/management/api/v1/appblocks/documents`: `GET settings`, `POST configurations`, `DELETE configurations/{id}`, and `POST printers`. Save/discovery request types are in `PressroomConfigurations.cs`. The stored output is `pdf` or `printer:<identifier>`. Printer discovery is read-only, and the dropdown always retains PDF if discovery is unavailable. No public arbitrary-HTML or printing HTTP endpoint is installed by Documents itself.

## Build

Source builds use sibling `../Pressroom` when available, overridable with `PressroomRoot`. Set `UsePressroomSource=false` to resolve `Pressroom.Client` 0.1.0 from a package feed. No browser or PDF engine is installed in the Umbraco process.

```powershell
dotnet build src/AppBlocks.Documents
dotnet run --project tests/AppBlocks.Documents.Tests -- -noLogo
dotnet pack src/AppBlocks.Documents -c Release -o artifacts/packages
```
