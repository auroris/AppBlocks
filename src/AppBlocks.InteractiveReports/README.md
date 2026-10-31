# AppBlocks.InteractiveReports

Optional Interactive Reports companion for **AppBlocks** on Umbraco 18.3 / .NET 10. This package requires AppBlocks; NuGet installs that dependency automatically.

```powershell
dotnet add package AppBlocks.InteractiveReports --version 0.1.0-alpha.1
```

This is an unpublished alpha. Use the locally built packages until a public release is available.

The Umbraco composer registers the companion automatically. On startup, AppBlocks installs its missing report element and data type definitions alongside the base schema, and the companion lets report definition nodes sit under the Data sources root. Add **Interactive Report** to an existing Block Grid or Block List, choose **Table** or **SQL query**, and configure the block's starting view. The site retains its page types, templates and layout.

The table dropdown lists tables and views from **Settings → AppBlocks → Connections** as `DataSourceName.TableName`, including schema names where needed. SQL mode selects a configured data source and accepts a SELECT query, with optional `:PARAM.name` and signed-in user bind variables such as `:USER_KEY`. Connection credentials stay in AppBlocks configuration. The source and starting view are saved directly on the block; no configuration content nodes are required. Administrators can preview an unpublished source to configure its view; previews are private to their creator and expire after ten minutes.

The companion supplies the report block, native rendering partials, report editors, browser runtime, JSON and download endpoints, saved views, authorization and optional backoffice reports. AppBlocks supplies connections, data sources, forms and page access. Installing AppBlocks alone adds no Interactive Reports dependency, endpoints or report schema.

A report on an unprotected page is public, including export. On a page with no Public Access of its own or inherited, an anonymous visitor can view, query and download a report block, with no site setting to turn this on, so protect the page if its rows are not for everyone. Saved views are hidden from such a visitor, because they have no owner, and printing through Documents is not offered. A custom `IReportAccessPolicy` or data source policy that refuses them gets the same sign-in notice in place of the report. A report whose SQL uses a `:USER_` bind variable is never run for them: the page shows a sign-in notice and the endpoints answer 401. Reports that are not blocks, such as report definition nodes and configured reports, keep requiring an account. A custom `IReportAccessPolicy` or `IDataSourcePolicy` can refuse anonymous visitors; a replacement policy that wants the standard rule calls `AnonymousReportAccess.Allows`.

Report requests use AppBlocks page access and registered data-source policies. The endpoints are `/api/reports`, `/api/download`, `/umbraco/reports` and `/umbraco/report-download`. Record-level print actions use AppBlocks' optional document-link capability when installed. Forms select registered C# classes; the table/SQL editor belongs to reports.

**Actions → Download → HTML** downloads the same self-contained HTML that the default print preview prepares for Documents/Pressroom, using all matching rows, A4 portrait, and the current report's filters, columns, labels, masks, sorting and totals. Documents and Pressroom configurations are not required: the original `InteractiveReport.Client.FileDownload` package generates the HTML, and the companion reuses its `HtmlReportPresentation` renderer for printing. Like CSV, HTML follows the `download` control and server export authorization. The file is served as an attachment by `POST /api/download/{name}/html` (also `/umbraco/report-download/{name}/html`), and never submits a print job. Truncated or oversized results return an error instead of an incomplete file. CSV remains available alongside HTML.

**Actions → Print** uses the optional `AppBlocks.DocumentOutput.IDocumentOutputService` contract in the base AppBlocks assembly. Install `AppBlocks.Documents` and add Pressroom configurations in its backoffice settings. The menu is absent when the service is missing or there are no configured PDF/printer destinations; it also follows the report's `download` control and export authorization. InteractiveReports has no package dependency on Documents or Pressroom and never handles Pressroom URLs, API keys, or printer discovery.

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

## Database setup

Saved views use `IR_SAVED_REPORTS` on the connection above. The saved report administration page at `/api/reports/admin` also reads `IR_ADMINISTRATORS`, although AppBlocks decides who is an administrator itself, so nothing a reader does needs that table. Where the account may, the report store creates them on first use, including on Oracle 11.2 a sequence and a trigger behind the key.

Where it may not, **Settings → AppBlocks → Database setup** shows both remedies for that database type: the permission to grant, or a script to run. The script comes from the report stores' own statements, and it works for an account with no DDL rights at all. It needs read and write on the tables, which it grants on SQL Server and PostgreSQL. On PostgreSQL and Oracle an account with no DDL rights is refused the store's DDL even when the objects already exist, so the store then checks that it can read the table and carries on. Setting `InteractiveReport:SavedReports:AutoCreate` to `false` stops it trying at all. Either remedy takes effect on the next request, with no restart. The [base guide](../../README.md#database-setup) has the permissions for each database type.

Until then, reports do not depend on it. They load, query and export on their own starting view, saving is out of their features, so the **Report** actions and the saved-view selector are absent, and readers see no error. The reason is logged once, with what to do, and a backoffice administrator also sees a line on the block linking to the setup screen. The administration page itself keeps reporting failures, for administrators alone, because it exists to manage those rows. A site that replaces `ISavedReportStore` with its own answers for that store's storage, and the screen says so.

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
    // Interactive Reports authorization, row restrictions, context parameters or logging.
});
```

Both registration calls are idempotent. Connections added to `AddAppBlocks` before or after the companion are available to reports, appear read-only in Settings → AppBlocks → Connections, and can be chosen by a report block. A connection added directly to the report builder is available to configured reports, report definition nodes and forms, but it is not listed and a report block refuses it, so register a site's connections with `AddAppBlocks`. Move existing `AddAppBlocks(Action<InteractiveReportBuilder>)` callbacks to `AddAppBlocksInteractiveReports` when upgrading; ordinary connection-only callbacks stay with `AddAppBlocks(Action<AppBlocksBuilder>)`. `ReportApplicationDatabases` is gone: `AddApplicationDatabase` is on `AppBlocksBuilder`. To hand a builder's connections to a report engine composed without the companion, call `reports.AddConnectionsFrom(connections)`.

The `AppBlocks:ReportAdministratorGroups` setting accepts backoffice User Group aliases and defaults to an empty list. A developer can explicitly grant report administration to groups such as `admin`, or replace `IReportAccessPolicy` with the application's rules. `AppBlocks:IncludeBackOfficeReports` enables the optional backoffice reports. Configured report definitions remain in the `InteractiveReport` configuration section. Report definition APIs live in `PublishedReportCatalog`; the base `PublishedDataCatalog` supplies sources and lists of values.

Build from the repository root:

```powershell
dotnet pack src/AppBlocks.InteractiveReports -c Release -o artifacts/packages
dotnet run --project tests/AppBlocks.InteractiveReports.Tests -- -noLogo
```

Source development uses a sibling `../Interactive-Reports` checkout when present. Override `InteractiveReportsRoot`, or set `UseInteractiveReportsSource=false` to consume its NuGet packages; a checkout that is simply not found falls back to them with warning APPBLOCKS001. The core AppBlocks project builds independently of that checkout.
