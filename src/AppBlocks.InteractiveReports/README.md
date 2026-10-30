# AppBlocks.InteractiveReports

Optional Interactive Reports companion for **AppBlocks** on Umbraco 18.3 / .NET 10. This package requires AppBlocks; NuGet installs that dependency automatically.

```powershell
dotnet add package AppBlocks.InteractiveReports --version 0.1.0-alpha.1
```

This is an unpublished alpha. Use the locally built packages until a public release is available.

The Umbraco composer registers the companion automatically. On startup, AppBlocks installs its missing report element and data type definitions alongside the base schema. Add **Interactive Report** to an existing Block Grid or Block List, choose a published AppBlocks SQL data source, and configure the block's starting view. The site retains its page types, templates and layout.

The companion supplies the report block, native rendering partials, report editors, browser runtime, JSON and download endpoints, saved views, authorization and optional backoffice reports. AppBlocks supplies connections, data sources, forms and page access. Installing AppBlocks alone adds no Interactive Reports dependency, endpoints or report schema.

Report requests use AppBlocks page access and data-source restrictions. Existing block keys, editor aliases and `/api/reports`, `/api/download`, `/umbraco/reports` and `/umbraco/report-download` routes are preserved. Existing report-name bindings and published report definitions remain supported. Record-level print actions use AppBlocks' optional document-link capability when installed.

**Actions → Download → HTML** downloads the same self-contained HTML that the default print preview prepares for Documents/Pressroom, using all matching rows, A4 portrait, and the current report's filters, columns, labels, masks, sorting and totals. Documents and Pressroom configurations are not required: the original `InteractiveReport.Client.FileDownload` package generates the HTML, and the companion reuses its `HtmlReportPresentation` renderer for printing. Like CSV, HTML follows the `download` control and server export authorization. The file is served as an attachment by `POST /api/download/{name}/html` (also `/umbraco/report-download/{name}/html`), and never submits a print job. Truncated or oversized results return an error instead of an incomplete file. CSV remains available alongside HTML.

**Actions → Print** uses the optional `AppBlocks.Documents.IDocumentOutputService` contract in the base AppBlocks assembly. Install `AppBlocks.Documents` and add Pressroom configurations in its backoffice settings. The menu is absent when the service is missing or there are no configured PDF/printer destinations; it also follows the report's `download` control and export authorization. InteractiveReports has no package dependency on Documents or Pressroom and never handles Pressroom URLs, API keys, or printer discovery.

The dialog offers the configured destinations, all matching rows (default) or the current page, paper size, orientation, and a preview. It prints the authorized server query's columns, labels, masks, filtering, sorting, grouped/pivot rows, breaks and totals. Charts print their underlying data table; interactive renderers become text. The current-page footer identifies aggregates that cover all matching rows. Export limits produce an error rather than silently printing an incomplete report.

The host endpoints are `GET /api/reports/{name}/print/options`, `POST …/preview`, and `POST …/submit`; the POSTs require the antiforgery token returned by options. Preview stores a bounded, five-minute HTML snapshot bound to the user, report, and page query. Submit rechecks export access and data before consuming the token once and calling Documents. Changed data or destinations require a new preview. Tokens live in a bounded memory cache, so multi-instance sites need session affinity for this flow; eviction or restart requires another preview. No client-supplied HTML or report rows are submitted to Documents.

PDF destinations download a file; Documents' HTML fallback is clearly identified. Printer destinations return a submission receipt, which does not confirm physical printing. Neither browser nor server automatically retries printing, including uncertain failures. Existing record-level paper-document links remain separate from printing the current interactive report.

Print validation:

```powershell
dotnet run --project tests/AppBlocks.InteractiveReports.Tests -c Release -- -noLogo -class '*ReportPrintTests'
node --test tests/ui/report-print.browser.test.mjs
```

Browser tests use the sibling Interactive-Reports checkout's Playwright dependency and built client. Set `INTERACTIVE_REPORTS_ROOT` for another checkout and `BROWSER_PATH` for an installed Chromium browser when Playwright's browser is unavailable.

The companion uses the same business connections as AppBlocks.

**Oracle 11.2** and **Oracle 12+** select Interactive Reports' Oracle SQL compatibility modes. AppBlocks shares this selection across forms and report blocks: Oracle 11.2 mode maps to the `Oracle11g` dialect (including legacy paging), and Oracle 12+ mode maps to the `Oracle` dialect with automatic detection of pre-12 servers. Both modes use the Oracle driver bundled by AppBlocks; see the [base guide](../../README.md#connections) for configuration and driver compatibility.

The built-in `AppBlocksDb` connection is the default for application data and saved report views. It opens `AppBlocks.sqlite.db` beside Umbraco's SQLite database and also stores uploads by default. Prefer it for application forms and reports, so their writes do not compete with CMS saves in Umbraco's SQLite file. Business tables are supplied by the site; the report store creates its own tables on first use. `UmbracoDb` remains available for reports that specifically need CMS data.

To store saved views elsewhere, set `InteractiveReport:SavedReports:Connection` to a registered connection, or `InteractiveReport:SavedReports:DataSource` to a configured connection-string name. These explicit settings take precedence over `AppBlocksDb`. To override the shared default database itself, configure `ConnectionStrings:AppBlocksDb` and `ConnectionStrings:AppBlocksDb_ProviderName`. See the [base guide](../../README.md#connections) for file locations and separate upload storage.

For an additional business connection:

```csharp
using AppBlocks;

umbraco.AddAppBlocks(connections =>
    connections.AddConnection("Business", services => new SqlConnection(connectionString)));
```

For report-specific customization:

```csharp
using AppBlocks.InteractiveReports;

umbraco.AddAppBlocksInteractiveReports(reports =>
{
    // Interactive Reports authorization, context parameters, or legacy connections.
});
```

Both registration calls are idempotent. Shared connections added before or after the companion are available to reports, and legacy connections registered through the report builder remain available to forms. Move existing `AddAppBlocks(Action<InteractiveReportBuilder>)` callbacks to `AddAppBlocksInteractiveReports` when upgrading; ordinary connection-only callbacks can stay with `AddAppBlocks`.

The existing `AppBlocks:ReportAdministratorGroups` and `AppBlocks:IncludeBackOfficeReports` settings remain supported. Configured report definitions remain in the `InteractiveReport` configuration section. Report definition APIs now live in `PublishedReportCatalog`; the base `PublishedDataCatalog` supplies sources and lists of values.

Build from the repository root:

```powershell
dotnet pack src/AppBlocks.InteractiveReports -c Release -o artifacts/packages
dotnet run --project tests/AppBlocks.InteractiveReports.Tests -- -noLogo
```

Source development uses a sibling `../Interactive-Reports` checkout when present. Override `InteractiveReportsRoot`, or set `UseInteractiveReportsSource=false` to consume its NuGet packages. The core AppBlocks project builds independently of that checkout.
