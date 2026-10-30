# AppBlocks

Forms and database-backed blocks that fit into an existing Umbraco website.

AppBlocks supplies the **Forms container** element type for native Block Grid and Block List editors. The optional **AppBlocks.InteractiveReports** NuGet companion adds **Interactive Report** and requires AppBlocks. Put them alongside your site's other blocks, including Rich Text. The site keeps its page types, templates, layout and authentication. AppBlocks does not install a page template or require an AppBlocks page type.

This is an unpublished alpha targeting Umbraco **18.3** and **.NET 10**. The repository is local; no public repository or NuGet release has been created.

## Add it to a site

Install the locally built `AppBlocks` package, or reference `src/AppBlocks/AppBlocks.csproj` during development. Its Umbraco composer registers services, management screens and routes automatically. uSync installs missing element and data type definitions on startup, preserving existing site definitions. Install `AppBlocks.InteractiveReports` when the site needs interactive reports; NuGet brings in AppBlocks as its required dependency. Its composer adds report services and routes, and its schema migration preserves legacy report bindings. See the [companion guide](src/AppBlocks.InteractiveReports/README.md).

1. In **Settings → Data Types**, add **Forms container** (and **Interactive Report** when its companion is installed) to the allowed blocks of your existing Block Grid or Block List.
2. Create your application tables in the **AppBlocks database**, then in **Content** create a **Data sources** root and a **SQL data source**. Pick a table, give it an alias, select its key column, choose permitted operations and publish it. A blank connection uses **AppBlocks database** automatically.
3. Add a **Forms container** to any page. Inside it, add a Form, select its resource in Form Interface, and add fields and actions. Field pickers use the source's columns.
4. With `AppBlocks.InteractiveReports` installed, add an **Interactive Report**, choose its data source and configure its starting view. Columns, sorting, filters, paging and presentation are saved on that block. No report definition in `appsettings.json` is needed.

Sites using Umbraco's conventional `blockgrid/Components` or `blocklist/Components` partial rendering pick up the packaged components automatically. A site with its own rendering convention can invoke `AppBlocksBlock` with `block` (a `BlockGridItem`) or `listItem` (a `BlockListItem`). Pass `page` explicitly when rendering outside the current routed page.

```cshtml
@await Component.InvokeAsync("AppBlocksBlock", new { block = Model })
```

The native blocks include their required assets and inherit ordinary site typography. Public page content stays visible; interactive blocks show a sign-in notice until an authorized member or backoffice administrator signs in. Data-source read/write groups and application business rules still apply to API requests.

## Connections

**Settings → AppBlocks → Connections** supplies **AppBlocks database** (`AppBlocksDb`) as the default for forms and reports. On SQLite installations, its `AppBlocks.sqlite.db` file sits beside Umbraco's SQLite database. Application data, saved report views and uploaded file contents share this file by default. It is created on first use with foreign keys and write-ahead logging enabled. The site supplies its business tables; saved-report and upload tables are created by their stores.

Use this separate database for application work. SQLite permits only one writer per database, so keeping form submissions and uploads out of Umbraco's file avoids competing with CMS saves. `UmbracoDb` remains available for sources that specifically read CMS data. AppBlocks opens its own connections; it does not share Umbraco's scope or transaction.

If Umbraco has no file-backed SQLite database, the default file is under `App_Data` in `AppBlocks:DataRoot`, `UmbracoAppHost:DataRoot`, or the site's content root, in that order. A configured `ConnectionStrings:AppBlocksDb` and `ConnectionStrings:AppBlocksDb_ProviderName` override the default file and provider. Back up the application database as well as Umbraco's database, using a SQLite-aware backup when the application is running.

Uploads can use their own store by setting:

```json
{
  "Attachments": {
    "Connection": "AttachmentsDb"
  }
}
```

This selects the optional `Attachments.sqlite.db` beside Umbraco's SQLite file. Set `Attachments:Connection` to another registered SQLite or SQL Server connection to use another database, or configure `ConnectionStrings:AttachmentsDb` and its `_ProviderName` to override that optional store. Leave `Attachments:Connection` empty or use `AppBlocksDb` to share the default application database. Uploaded bytes are stored in database tables, not loose files. If the application database uses PostgreSQL or Oracle, select a separate supported upload store.

The reports companion defaults `InteractiveReport:SavedReports:Connection` to `AppBlocksDb`. An explicit `SavedReports:Connection` or `SavedReports:DataSource` takes precedence. See the [companion guide](src/AppBlocks.InteractiveReports/README.md).

Administrators can add, edit and test extra SQL Server, SQLite, PostgreSQL, **Oracle 11.2** or **Oracle 12+** connections. The screen links to [connection string examples](https://www.connectionstrings.com/). Table selection uses database metadata, with no SQL editor. Changing a connection selects a database; it does not copy existing tables or files.

Connection strings entered here are protected with ASP.NET Core Data Protection and stored in `App_Data/AppBlocks/connections.json`; they are never stored in blocks, returned by the listing API or included in uSync content exports. Keep that file and the site's Data Protection keys together when backing up or moving an installation. Multiple application instances need a shared, durable file/key store; concurrent administration across multiple instances is not yet coordinated.

Named connections already supplied by the site configuration are read-only in this screen. Register shared business connections with `AddAppBlocks(connections => connections.AddConnection(...))`. The companion also supports legacy Interactive Reports registrations through `AddAppBlocksInteractiveReports`.

**Oracle 11.2** and **Oracle 12+** are Oracle SQL compatibility modes shared with Interactive Reports. The choice controls the SQL generated for the connection. Both modes use the bundled `Oracle.ManagedDataAccess.Core` driver. For configured connections, set `ConnectionStrings:<name>_ProviderName` to `oracle11g` for Oracle 11.2 mode or `oracle` for Oracle 12+ mode; existing `Oracle.ManagedDataAccess.Client` and `Oracle.ManagedDataAccess.Core` provider names still work. Code registrations can pass `DatabaseDialect.Oracle11g` or `DatabaseDialect.Oracle` to `AddConnection`. Oracle 12+ mode also detects pre-12 server versions on first use and switches to Oracle 11.2 mode, as Interactive Reports does.

Oracle 11.2 mode uses 11.2-compatible metadata and row limits, with explicit key sequences for generated keys (`keySequences`, for example `{"ID":"ORDERS_SEQ"}`). Oracle 12+ mode additionally discovers identity columns. The reports companion preserves the selected compatibility mode. Upload storage still requires a separate SQLite or SQL Server connection.

The bundled driver matches Interactive Reports' live-test dependency. Oracle's [current driver certification](https://docs.oracle.com/en/database/oracle/oracle-database/26/odpnt/InstallSystemRequirements.html) lists Database 19c and later; connectivity to older servers requires verification independently of the selected SQL compatibility mode.

## Samples and business rules

**Settings → AppBlocks → Forms** discovers saved forms across native block properties, including nested containers, drafts and language variants. Each form links to a read-only C# sample and a `.cs` download. Samples reflect that form's writable fields and demonstrate parameterized reads and upserts with authorization and optimistic concurrency against a clearly named example SQLite table.

Shared recipes live in [business-rules.md](docs/business-rules.md), also downloadable from the dashboard: signed-in identity, groups, ownership, trusted defaults, validation, transactions and other common business rules. Samples and the guide are reference material; the backoffice does not execute them or provide a code editor.

## Development and verification

AppBlocks builds without Interactive Reports. Its optional companion references it; source development for the companion uses a sibling `../Interactive-Reports` checkout when present. Override `InteractiveReportsRoot` when needed. Set `UseInteractiveReportsSource=false` to use NuGet dependencies, including when checking the release package against a local package feed.

```powershell
dotnet build AppBlocks.slnx
dotnet run --project tests/AppBlocks.Tests -- -noLogo
dotnet run --project tests/AppBlocks.InteractiveReports.Tests -- -noLogo
dotnet run --project tests/AppBlocks.Documents.Tests -- -noLogo
dotnet run --project samples/AppBlocks.Demo --urls http://localhost:5079
dotnet pack src/AppBlocks -c Release -o artifacts/packages
dotnet pack src/AppBlocks.InteractiveReports -c Release -o artifacts/packages
dotnet pack src/AppBlocks.Documents -c Release -o artifacts/packages
```

The demo references the reports companion, which brings in AppBlocks. The optional demo seed is website code, outside the package. Enable `AppBlocksDemo:SeedContent` in the demo's ignored `appsettings.Development.local.json` on a local SQLite installation to create a sample table and an ordinary article with a Block List and a Block Grid. Its own template uses both native rendering helpers. The companion's HTTP acceptance test can verify that running demo:

```powershell
$env:APPBLOCKS_DEMO_SETTINGS = "$PWD/samples/AppBlocks.Demo/appsettings.Development.local.json"
dotnet run --project tests/AppBlocks.InteractiveReports.Tests -- -noLogo
```

The seed and HTTP test are intended only for this disposable local demo. The local settings file is excluded from publishing. The test uses the unattended demo account from that file.

## Documents companion

Install `AppBlocks.Documents` for template-based HTML/PDF documents. It depends on AppBlocks and
works without Interactive Reports. Administrators configure the optional Pressroom service at
**Settings → AppBlocks → Documents**. Without Pressroom, documents open as self-contained HTML;
service failures fall back to HTML and are logged through Umbraco. Document actions open a new
window/tab. See [Documents configuration, authoring and integration](src/AppBlocks.Documents/README.md).

## Extraction compatibility

UmbracoAppHost consumes AppBlocks and the optional companions it needs. Its SSO, site shells and sample content remain in AppHost. Existing content keys, routes, editor aliases and `UmbracoAppHost.*` C# namespaces are retained where needed for migration; new package APIs use `AppBlocks.*`. Legacy configured reports and report-definition content continue to run, while new reports are configured directly on blocks.

AppBlocks includes forms, attachments, tabular editing and the schedule runtime. Interactive reports live in `AppBlocks.InteractiveReports`, which depends on AppBlocks. Existing callbacks typed as `Action<InteractiveReportBuilder>` move from `AddAppBlocks` to `AddAppBlocksInteractiveReports`; report definitions move from `PublishedDataCatalog` to the companion's `PublishedReportCatalog`. Public publishing, broader provider acceptance testing and API stabilization remain release work.
