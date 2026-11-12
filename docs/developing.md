# Developing AppBlocks

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
pwsh -NoProfile -File tests/clean-install/Invoke-CleanInstall.ps1
```

The SQL Server live tests reuse the LocalDB scenarios for form data sources, application schema installation and attachment storage. Supply an empty, disposable SQL Server database with `db_owner` access:

```powershell
$env:APPBLOCKS_SQLSERVER_CONNECTION = "Server=HOST,1433;Database=TEST_DATABASE;User ID=TEST_USER;Password=TEST_PASSWORD;Encrypt=True;TrustServerCertificate=True;"
dotnet run --project tests/AppBlocks.Tests -- -noLogo -explicit only -class AppBlocks.Tests.SqlServerLiveTests
```

These tests refuse to run if any of their object names already exist and drop their own objects in cleanup. They do not need server administration rights. `TrustServerCertificate=True` is for a test server using a self-signed certificate. Credentials stay in the environment; ordinary runs do not contact the remote server.

The Oracle 11.2 live tests are opt-in. Supply a test-schema connection through the environment, keeping credentials out of source control:

```powershell
$env:APPBLOCKS_ORACLE_CONNECTION = "User Id=TEST_USER;Password=TEST_PASSWORD;Data Source=HOST:1521/SERVICE_NAME;"
dotnet run --project tests/AppBlocks.Tests -- -noLogo -explicit only -class AppBlocks.Tests.OracleLiveTests
```

The account needs `CREATE SESSION`, `CREATE TABLE`, `CREATE VIEW`, `CREATE SEQUENCE` and a tablespace quota. The tests create uniquely named tables, views and sequences and drop them in cleanup. They exercise the `Oracle.ManagedDataAccess.Core` driver the test projects reference (AppBlocks itself references no driver) in both `oracle11g` and automatic `oracle` modes, including metadata discovery, generated keys, form CRUD, concurrency, row restrictions, queries and custom-statement validation. Ordinary test runs do not contact Oracle.

The demo references the reports companion, which brings in AppBlocks. The optional demo seed is website code, outside the package. Enable `AppBlocksDemo:SeedContent` in the demo's ignored `appsettings.Development.local.json` on a local SQLite installation to create a sample table and an ordinary article with a Block List and a Block Grid. Its own template uses both native rendering helpers. The companion's HTTP acceptance test can verify that running demo:

```powershell
$env:APPBLOCKS_DEMO_SETTINGS = "$PWD/samples/AppBlocks.Demo/appsettings.Development.local.json"
dotnet run --project tests/AppBlocks.InteractiveReports.Tests -- -noLogo
```

The seed and HTTP test are intended only for this disposable local demo. The local settings file is excluded from publishing. The test uses the unattended demo account from that file.

The demo also references `Umbraco.Cms.DevelopmentMode.Backoffice`, so **Settings → AppBlocks → Backing Classes** edits and compiles its `AppBlocks/Code` folder, which git ignores; remove the package to see [Backing Classes](csharp-editor.md) read-only. The browser tests in `tests/ui` run with `node --test tests/ui/`, using Playwright from the Interactive Reports checkout (`INTERACTIVE_REPORTS_ROOT` when it is not `../Interactive-Reports`).

The [clean-install acceptance test](../tests/clean-install/README.md) builds actual NuGet packages, installs them into fresh official Umbraco sites without uSync or other CMS plugins, and verifies installation, browser editing, data operations and restart behavior. Each run uses empty databases and its own package cache.

## Uppy assets

The attachment uploader's pinned dependencies and bundler are in `tools/uppy`. After updating them, run `npm ci` and `npm run build` in that directory. Commit its lockfile and the generated `uppy.js`, `uppy.css` and `uppy-LICENSE.txt` under `src/AppBlocks/wwwroot/App_Plugins/ApplicationBlocks`. The bundle contains Core, Dashboard and XHR Upload; it loads when someone opens **Upload**. Normal .NET builds and consuming sites use the checked-in assets and need neither Node.js nor a CDN connection. The bundle includes its dependencies' license notices alongside the assets.

## Who owns the definitions

The `Schema/` folders of AppBlocks and its companions are the only source of their element and data types, the data source types and the report definition type. Change them there. Fresh sites install the complete current definitions through Umbraco’s package importer, invoked by a tracked package migration. Installation imports only missing definitions, so a change reaches existing sites in one of two ways:

- **A migration step**, for a change a released version carries to every site. Each package has an Umbraco package migration plan (`AppBlocksMigrationPlan`, `InteractiveReportsMigrationPlan`, `DocumentsMigrationPlan`), and Umbraco runs the steps a site has not reached during startup, recording the last one in its key-value store under the plan's name. A step is a class deriving from `AsyncMigrationBase` that takes the Umbraco services it needs in its constructor and changes the definition, or rewrites block values, through them. Add it after the last step in `DefinePlan` as `To<TheStep>("yyyy-MM-dd-what-it-does")`, and never reorder, rename or remove a step once a site may have run it. A fresh site runs every step. Steps before `InstallPackageDefinitions` must tolerate absent definitions; later steps run after missing definitions have been imported. All schema steps respect `Umbraco:CMS:PackageMigration:RunSchemaAndContentMigrations=false`, including explicit calls to `EnsureAsync`. No application-started handler bypasses that setting. `Reinstall` is an explicit administrator operation and can restore definitions even when automatic schema migrations are disabled. The first step, `AddDataSourceBackingClass`, adds the SQL data source's **Backing class** property and the data type its picker needs to a site that has the type without them, reading both from the schema files, and is the example to follow. Schema steps take Umbraco’s key-value write lock before reading, because even a step with no schema changes writes its migration state on completion. This avoids upgrading a SQLite read transaction while other startup activity is writing.
- **Reinstall**, from **Settings → AppBlocks → Definitions**, for a change no step carries, and during development after editing a schema file. It resets every definition to the package in place, by key: names, properties, allowed children, data type settings and folders, in one transaction. Content keeps its values, since each property is matched by alias and kept, except the values of a property the package no longer has; a type of the site's own that took one of the aliases is left alone, and the screen says so. **Uninstall** on the same screen removes every definition and the folders they leave empty, refuses while documents, library elements, block configurations, compositions or saved block values still reference the definitions, or a host-owned property uses one of the data types, naming what to remove first, and stops startup from installing them again until Reinstall.

Definitions keep their keys across versions because the keys are derived, by `AppBlockSchema.Key`, from names and a fixed seed. Changing the seed changes every key, which no step carries; [Renamed identifiers](renamed-identifiers.md#definition-keys) describes what that means for an existing site.

When a release does change a definition, write the step and check it the way the clean-install test will: install the previous package, start the new one, and compare the result with a fresh install of the new one, definition by definition.

A site importing definitions before Umbraco’s package migration runner may call `SchemaInstaller.EnsureAsync` from a service scope before that import. It is safe to repeat and respects the schema migration setting. Native package import handles creation and the package keeps custom reset logic because a reset removes extra properties and restores current settings, whereas normal package import merges types and preserves existing data-type configuration. A host that imports these definitions through a synchronization tool of its own, and changes some of them on purpose, gets them reset to the package's by a Reinstall until its next import.

## Backoffice and published lookups

Settings tabs are `workspaceView` manifests conditioned on `AppBlocks.Workspace`; companions register their own views. Management requests use `umbHttpClient`, so authentication refresh, interceptors and the application base URL remain Umbraco’s responsibility. A link into a backoffice section starts from `backofficePath`, exported by `App_Plugins/AppBlocks/api.js`: the path base and the backoffice's own first segment, read from the address it runs at. Picker catalogues are shared through a repository for 30 seconds per auth context, and failed loads can be retried.

`AppBlockContent.PageAsync`, `ReportAsync` and `FormDefinition.ResolveAsync` await published-content lookups. Call `LoadAsync` on a catalogue, resource registry, schedule source collection or `AppBlocksValidator` before using its synchronous snapshot or validation methods. Normal controllers, publishing notifications and rendering paths do this themselves; code-only resource lookups need no catalogue. Providers may implement `IFormResourceProvider.GetResourcesAsync` while existing in-memory providers keep their enumerable contract.

Saved block envelopes and layouts are read through Umbraco’s Grid, List and Single Block converters. AppBlocks retains its own rules for bindings, ownership and placement. Form Interface uses the native Single Block editor and resolves to one `BlockListItem`; the form’s fields remain in their Block Grid areas. The `UseSingleBlockFormInterface` package migration updates only the package’s Form Interface data type, keeping its key and property references. This is a breaking change for saved development content: recreate existing Form Interface values and republish their pages, or regenerate the disposable sample pages. Old Block List values, including historical versions, are not converted or supported by a fallback reader. Restoring an old version therefore requires recreating its Form Interface too. Host-owned Block List data types are unchanged.
