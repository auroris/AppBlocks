# AppBlocks.Documents

Generate templated documents from AppBlocks data sources and provide a shared Pressroom gateway for other companions. Browser documents open as self-contained HTML or PDF; callers can also explicitly submit documents to configured printers.

Install `AppBlocks.Documents` alongside `AppBlocks`. The package depends on AppBlocks and works with or without Interactive Reports. Its Umbraco composer registers the document engine, routes, settings page and schema automatically. Documents are `paperReport` content nodes, served at `/api/paper-reports/{key}`, and their Razor templates take `AppBlocks.Documents.PaperReports.PaperReportModel`.

## Settings

Open **Settings → AppBlocks → Documents** as an administrator. Add, edit or remove named Pressroom configurations. Each has a URL, API key, output and timeout. **PDF** is always available in the output dropdown; **Load printers** queries that server's `GET /v1/printers` and adds the returned printer identifiers. Printer discovery and printer output require a key. PDF can omit the key when Pressroom permits anonymous rendering. Saving a printer configuration checks that the server still exposes that printer.

Configurations have stable IDs and apply immediately. Several configurations can use the same server with different outputs or credentials. Saved keys are never sent to the browser; a blank key field preserves the existing key. Use **Remove the saved API key** to clear it. Changing the URL requires a new key or explicit key removal, so an existing credential is not silently sent to another server.

Existing template-document links use the first PDF configuration. With none, or with `?format=html`, they open self-contained HTML. These GET requests never submit to a printer. PDF failures fall back to HTML and are logged through Umbraco's `ILogger` pipeline. Printer failures are reported separately and are never retried or silently converted to a different output.

Settings are kept in `App_Data/AppBlocks/documents.json` beneath `AppBlocks:DataRoot`, or the site's content root when that is not set. API keys are protected with the site's Data Protection keys and never returned by the settings API. Back up those keys with the settings file. As with AppBlocks connections, this file store is intended for a single application instance.

Until settings are saved in the backoffice, configuration under `AppBlocks:Documents:Pressroom` (`Url`, `ApiKey`, `TimeoutSeconds`, 65 seconds unless set) supplies a **Pressroom PDF** configuration. The first edit on the screen saves it with the rest of the collection. Deleting all configurations saves an empty collection, and the configured one does not reappear.

## Author a document

Create a **Document** (`paperReport`) content node, choose a published SQL data source, and select a Razor template that accepts `PaperReportModel`. Documents can be created at the root; to nest one under a site's page, allow Document beneath that site's document type. A site's own templates supply its content and layout. The package adds no sample content or branded templates.

The Document type offers every installed template: at startup, and whenever a template or the type is saved, the package adds the templates the type lacks, and never removes one. A site that chooses the type's templates itself sets `AppBlocks:Documents:AllowAllTemplates` to `false`.

Templates receive rows and columns, bound parameters, the signed-in person, culture, generation time and presentation name. Access is checked against both Umbraco Public Access, which only a host's `IProtectedPageAdmission` can relax, and the data source's read permissions, and a paper report always needs a signed-in Member or backoffice User, even on a page without Public Access. The configured row limit still applies. `?format=html` explicitly requests HTML even with Pressroom configured.

A document's **Culture** is a culture name such as `en-GB` or `fr-FR`, or a bind variable such as `:PARAM.culture`; empty, it is the culture the site serves the download request in. Its **Page options** are Pressroom's render options as JSON. Without a `pageSize` the renderer's default paper applies, without a `timeZone` the web server's time zone, and without a `footerTemplate` the footer shows the page number and page count, with no words.

Register `IDocumentPresentationProvider` to provide a theme name and optional print stylesheet. The default places the package's neutral print stylesheet, `/App_Plugins/AppBlocks.Documents/print.css`, first in every document; `AppBlocks:Documents:PrintStylesheet` names another site path, or an empty value none. A template that styles itself entirely leaves the stylesheet out with `<meta name="appblocks-print-stylesheet" content="none">` in its head.

To make a document self-contained, the package fetches the images, stylesheets and fonts a template refers to and embeds them, without cookies or redirects. Because a template may write a data value into an asset's address, it never fetches from loopback, link-local or private-network addresses (10/8, 172.16/12, 192.168/16, 100.64/10, `fc00::/7` and `fec0::/10`), where the server's own services and its network's live; such a reference is kept as it is and logged. A template that embeds images from a server on the site's own network names it in `AppBlocks:Documents:AllowedAssetHosts`, as host names, addresses or CIDR ranges such as `["reports.intranet", "10.20.0.0/16"]`. A host name listed there is fetched whatever it resolves to.

## Companion integration

`IAppDocumentLinks` lives in the base AppBlocks assembly, in the `AppBlocks.DocumentOutput` namespace. Resolve it optionally with `GetService<IAppDocumentLinks>()`. If absent, omit document actions. If present, `Url(documentKey, request.PathBase)` supplies the endpoint. Append the record parameters and use `target="_blank" rel="noopener"` on links, or `window.open(url, "_blank", "noopener")` synchronously inside a user action. The endpoint rechecks authorization; knowing a document URL grants no access.

Interactive Reports' UI, authorization and report-state handling belong to that companion. Documents owns all Pressroom HTTP interactions, configuration and credentials. Documents never references Interactive Reports. The settings screen contributes a native `workspaceView` extension in its `umbraco-package.json`, conditioned on `AppBlocks.Workspace`. Umbraco owns its navigation and loading.

### Shared output service

The stable optional contract is [`IDocumentOutputService`](../AppBlocks/DocumentOutput/IDocumentOutputService.cs), in the **base AppBlocks assembly**, namespace `AppBlocks.DocumentOutput`. Consumers need no dependency on Pressroom or the Documents implementation assembly. Resolve it with `GetService<IDocumentOutputService>()`; omit the output/Print UI when it is absent or `GetDestinations()` returns no destinations.

- `GetDestinations()` returns `DocumentDestination(Id, Name, Output, Printer)`. `Id` is a GUID; `Output` is `DocumentOutputKind.Pdf` or `Printer`. These descriptors contain no service URLs or credentials.
- `SubmitAsync(configurationId, new DocumentRequest(html, fileName, options), cancellationToken)` sends the complete self-contained HTML to the selected configuration. `DocumentRenderOptions` and its page-size, margins and readiness records belong to AppBlocks. Its defaults name no paper size, so the renderer's default applies; a footer of page numbers alone; `Locale` null, which is the culture of the request that submits the document; and `TimeZone` null, which is the web server's time zone (`DocumentRenderOptions.ServerTimeZone`). A caller that submits later than the request it formats for, as a print preview does, sets both. Do not send client-supplied arbitrary HTML or rows without the consuming application's own validation and authorization.
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

Source builds use sibling `../Pressroom` when available, overridable with `PressroomRoot`. Set `UsePressroomSource=false` to resolve `AurorisNet.Pressroom.Client` 1.1.0 from a package feed; it bundles the Client and Contracts assemblies, so no separate Contracts package is needed. The C# namespaces remain `Pressroom.Client` and `Pressroom.Contracts`. A checkout that is simply not found falls back to the package with warning APPBLOCKS002. No browser or PDF engine is installed in the Umbraco process.

```powershell
dotnet build src/AppBlocks.Documents
dotnet run --project tests/AppBlocks.Documents.Tests -- -noLogo
dotnet pack src/AppBlocks.Documents -c Release -o artifacts/packages
```
