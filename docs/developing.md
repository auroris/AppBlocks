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

The account needs `CREATE SESSION`, `CREATE TABLE`, `CREATE VIEW`, `CREATE SEQUENCE` and a tablespace quota. The tests create uniquely named tables, views and sequences and drop them in cleanup. They exercise the bundled driver in both `oracle11g` and automatic `oracle` modes, including metadata discovery, generated keys, form CRUD, concurrency, row restrictions, queries and custom-statement validation. Ordinary test runs do not contact Oracle.

The demo references the reports companion, which brings in AppBlocks. The optional demo seed is website code, outside the package. Enable `AppBlocksDemo:SeedContent` in the demo's ignored `appsettings.Development.local.json` on a local SQLite installation to create a sample table and an ordinary article with a Block List and a Block Grid. Its own template uses both native rendering helpers. The companion's HTTP acceptance test can verify that running demo:

```powershell
$env:APPBLOCKS_DEMO_SETTINGS = "$PWD/samples/AppBlocks.Demo/appsettings.Development.local.json"
dotnet run --project tests/AppBlocks.InteractiveReports.Tests -- -noLogo
```

The seed and HTTP test are intended only for this disposable local demo. The local settings file is excluded from publishing. The test uses the unattended demo account from that file.

The [clean-install acceptance test](../tests/clean-install/README.md) builds actual NuGet packages, installs them into fresh official Umbraco sites without uSync or other CMS plugins, and verifies installation, browser editing, data operations and restart behavior. Each run uses empty databases and its own package cache.

## Who owns the definitions

The `Schema/` folders of AppBlocks and its companions are the only source of their element and data types, the data source types and the report definition type. Change them there. Fresh sites install the complete current definitions. After a schema change, recreate the development or test site to use it; startup does not modify existing definitions. Optional child types are declared in the schema and included during installation when their companion package is present.

A site that imports definitions with a synchronization tool should call `SchemaInstaller.EnsureAsync`, which is resolvable from dependency injection and safe to run repeatedly, before its import starts: the installer's own startup notification can come after the import. UmbracoAppHost does that for uSync, and refuses to import copies of these definitions except the two it widens on purpose, listed in its `AppBlocksDefinitions.HostOwned`.
