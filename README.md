<h1 align="center">AppBlocks</h1>

<p align="center">
  Forms, reports and database-backed blocks for Umbraco.<br>
  Add them to the Block Grid or Block List you already have, alongside your Rich Text, on the pages you already have.
</p>

<p align="center">
  <a href="LICENSE"><img alt="MIT license" src="docs/images/badge-license.svg"></a>
  <img alt="Umbraco 18.3" src="docs/images/badge-umbraco.svg">
  <img alt=".NET 10" src="docs/images/badge-dotnet-10.svg">
  <img alt="Status: alpha" src="docs/images/badge-status.svg">
  <img alt="SQLite, SQL Server, PostgreSQL and Oracle supported" src="docs/images/badge-databases.svg">
</p>

![An interactive report of sample orders on an ordinary Umbraco page, with search, view switching and paging](docs/images/hero.png)

## What it is

AppBlocks gives Umbraco editors blocks that work with real application data. The site keeps its page types, templates, layout and sign-in. AppBlocks adds no page type or template of its own.

- **Sets itself up.** Install the package and start the site. Its definitions install through Umbraco's own services, with no import step or configuration, and a new version updates them through Umbraco's package migrations. **Settings → AppBlocks → Definitions** can reset them to the package or remove them.
- **Forms backed by your C# or your SQL.** Add a Forms container to a page and bind it to a registered C# class, which owns persistence, validation and business rules, or to a published SQL data source, whose generated statements, row restriction and optional C# backing class do. The field pickers follow its fields. Attachments, editable tables and schedules are included, and a form on a SQL data source saves its record and the rows of its editable tables in one transaction.
- **Anti-spam without code.** Place a block in a public form to protect it with Turnstile, reCAPTCHA, hCaptcha or a honeypot, checked on the server before your class runs.
- **Interactive reports.** The optional **AppBlocks.InteractiveReports** package adds a block powered by [Interactive Reports](https://github.com/auroris/Interactive-Reports). Pick a table or write a SELECT, and readers can search, filter, sort, chart, save and download.
- **Documents and printing.** The optional **AppBlocks.Documents** package produces template-based HTML and PDF documents, with optional direct-to-printer output through [Pressroom](https://github.com/auroris/Pressroom).
- **Umbraco's access rules first.** Blocks follow their page's Public Access. Add group rules, row restrictions and C# policies only when a rule goes beyond the page.
- **Your own database.** Application data goes in a separate SQLite file by default, away from Umbraco's. Connect SQL Server, PostgreSQL or Oracle from the backoffice, with connection strings encrypted at rest. SQLite and SQL Server drivers come with Umbraco; a site installs `Npgsql` or `Oracle.ManagedDataAccess.Core` for the others. An application database of your own brings its schema up to date on first open, with ensure scripts at every start and run-once migrations recorded in its version table.
- **Room for companion packages.** A package can add blocks that stay out of caches when their data changes, API controllers for signed-in Members with 401 and 403 answers and an antiforgery token endpoint, backoffice controllers for administrators, data source policies scoped to its own aliases, backing classes that answer API endpoints from C#, content nodes it generates and keeps current from code, form pages built from block aliases with no GUIDs, and its blocks allowed in the site's Block Grids from its registration.
- **Keeps working when it can't create tables.** If the database account isn't allowed to, the backoffice shows the grant or script to run, and everything else carries on.

![A form on the same site: order fields in a responsive layout, file attachments, and editable order lines](docs/images/form.png)

This is an alpha targeting Umbraco **18.3** and **.NET 10**. No NuGet release has been created yet.

## Quick start

AppBlocks isn't on NuGet yet, so reference `src/AppBlocks/AppBlocks.csproj` or install a locally built package. It registers itself and installs its definitions on startup. Add `AppBlocks.InteractiveReports` for reports and `AppBlocks.Documents` for documents, each of which brings in AppBlocks.

1. In **Settings → Data Types**, add **Forms container** (and **Interactive Report** when its companion is installed) to the allowed blocks of your existing Block Grid or Block List. **HTML**, **CSS** and **JavaScript** are installed too, and offered nowhere until a site adds them to a grid of its own, because they run whatever an editor writes. A site or package can allow a block in a Block Grid from code instead, as [Adding AppBlocks to a site](docs/site-integration.md#pages-built-from-code) describes.
2. Configure application data sources in **Settings → AppBlocks → Connections** and create your application tables. **AppBlocks database** is available as `AppBlocksDb` by default.
3. Register a C# class implementing `IFormResource` with dependency injection (`AddScoped<IFormResource, YourFormClass>()`), or publish a SQL data source under **Data sources**. Add a **Forms container** to any page, add a Form, and select the class or the data source in Form Interface, whose picker lists them in separate groups. Field pickers use its fields. A class owns persistence, validation and authorization; a data source applies its access settings, row restriction, backing class and policies.
4. With `AppBlocks.InteractiveReports` installed, add an **Interactive Report** and choose **Table** or **SQL query**. Tables are listed as `DataSourceName.TableName` (including the schema where applicable). For SQL, select a configured data source and enter a SELECT query. Configure the starting view to save columns, sorting, filters, paging and presentation on that block. No data-source content nodes or report definition in `appsettings.json` are needed.

Sites using Umbraco's conventional `blockgrid/Components` or `blocklist/Components` partials pick up the packaged components automatically. [Adding AppBlocks to a site](docs/site-integration.md) covers other rendering conventions, what anonymous visitors can do, and caching.

**Check your form classes before putting them on a public page.** On a page without Public Access, an anonymous visitor reaches your class, so a class with no check of its own is public. The [business rules guide](docs/business-rules.md) has the checks, and a recipe for a public contact form.

## Documentation

| Guide | What it covers |
| --- | --- |
| [Adding AppBlocks to a site](docs/site-integration.md) | Installation, rendering, styling, backoffice previews, access and caching, every setting, and companion packages' blocks, Member controllers, generated nodes and pages built from code |
| [Business rules](docs/business-rules.md) | Identity, groups, ownership, validation, and authorization scenarios. Also under **Settings → AppBlocks → Business rules** |
| [Backing Classes](docs/csharp-editor.md) | Writing form classes and backing classes in the backoffice on a development site, compiled while it runs, and the wizard that writes them for a table |
| [Connections](docs/connections.md) | The default database, extra connections, application databases and their schema scripts, upload storage and Oracle modes |
| [API endpoints](docs/api-endpoints.md) | Rows of a data source as JSON over GET at a route you define: parameters, access without a page, shapes and limits |
| [Database setup](docs/database-setup.md) | The tables AppBlocks creates, and what to do when the database account may not |
| [Anti-spam](docs/anti-spam.md) | The Anti-spam block, its keys, privacy and rate limits |
| [Display value](docs/display-value.md) | Read-only values, and values shown as links to a page, a mail message or a phone number |
| [Shuttle field](docs/shuttle.md) | Two lists, ordered selections, and single-line CSV values |
| [Schedule block](docs/schedule.md) | Rows, bars, markers across the top, colours, descriptions and the window |
| [Color picker](docs/color-picker.md) | Hex color fields, defaults, clearing and validation |
| [Worked example: a multi-tenant activity schedule](docs/activity-schedule-example.md) | The page tree, tables, data sources, blocks and C# of one application, end to end |
| [Interactive Reports companion](src/AppBlocks.InteractiveReports/README.md) | Report blocks, saved views and report access |
| [Documents companion](src/AppBlocks.Documents/README.md) | HTML and PDF documents, and printing through Pressroom |
| [Localisation](docs/localisation.md) | The blocks' texts in the site's languages, from the Umbraco dictionary or a source of your own, and every text id |
| [Developing AppBlocks](docs/developing.md) | Building, testing, the demo and who owns the definitions |
| [Renamed identifiers](docs/renamed-identifiers.md) | Old and new namespaces, editor aliases, definition keys, settings and removed members |

**Settings → AppBlocks → Forms** also lists every saved form, each with a read-only C# sample to download. AppBlocks never compiles or runs that sample. On a development site with the `Umbraco.Cms.DevelopmentMode.Backoffice` package, **Settings → AppBlocks → Backing Classes** is where code is compiled into the running site: see [Backing Classes](docs/csharp-editor.md).

## License

[MIT](LICENSE)
