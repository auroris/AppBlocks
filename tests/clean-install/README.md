# Clean Umbraco package acceptance test

From the repository root on Windows with PowerShell 7, .NET 10 and Node installed:

```powershell
pwsh -NoProfile -File tests/clean-install/Invoke-CleanInstall.ps1
```

The script builds local NuGet packages and creates two new sites using the official Umbraco 18.3.0 template: AppBlocks alone, then AppBlocks with its Interactive Reports and Documents companions. Each run has new SQLite databases and a separate NuGet cache under an ignored `artifacts/clean-install-*` directory. The sites use package references and the template's unchanged startup code. No uSync, AppHost or starter kit is installed.

Source checkouts of `../Interactive-Reports` and `../Pressroom` supply the companion dependencies. Override their paths with `-InteractiveReportsRoot` and `-PressroomRoot`. The browser test uses Playwright from Interactive Reports' `node_modules` (`npm ci` in that checkout), with its installed Chromium or Microsoft Edge. Use `-Port` if 5189 is occupied. Test hosts run only while checks execute and are stopped afterward.

The base-package site starts with no connection string and no unattended installation. A real browser completes the account, telemetry and SQLite steps of Umbraco's installation wizard, and the runner rejects fatal startup errors. The companions site also covers unattended installation.

The initial checks verify every embedded schema definition and its AppBlocks folder in the backoffice tree, packaged JavaScript/CSS/manifest, backoffice sign-in, the Connections screen, the Business rules help page and its section navigation, database metadata, connection testing and anonymous access rejection. Only after installation passes does the script add website-owned sample content. It then checks native Block List and Block Grid forms, required-field validation, optimistic concurrency, antiforgery protection, report queries and saved views, attachment upload/download/removal, document HTML output, and browser edits surviving a reload. A fresh browser session verifies that the page establishes its antiforgery cookie before multiple blocks load in parallel. The full functional checks run again after restarting the host.

`SchemaFolderChecks` verifies fresh folder placement and repeated installation without duplicate folders. `WizardSiteTests` additionally covers the pre-install runtime without a database, while `SchemaInstallationChecks` verifies direct installation outside `RuntimeLevel.Run` and saving/publishing table names and 20,000-character SQL sources on a fresh site. `tests/ui/table-picker.browser.test.mjs` checks the table dropdown and plain-string SQL editing.

Both sites first verify that AppBlocks adds no content files or PostgreSQL/Oracle driver dependencies, and that saving or testing a connection without its driver returns a validation error naming the package to install. The companions site then installs `Npgsql` and `Oracle.ManagedDataAccess.Core` itself, using the versions in `AppBlocks.Tests`, and adds the [Oracle assembly-scan exclusion](../../docs/connections.md#database-drivers). The seeded site and its restart verify that both drivers load and attempt connections, including both Oracle compatibility modes. Build and publish outputs contain the drivers only when the site installs them; the old bundled `oracle/` path is absent. No PostgreSQL or Oracle server is involved.

Logs, JSON results and screenshots remain in that run's directory. Generated administrator credentials are test-only and stay inside the ignored site configuration. These checks cover SQLite and HTML document output; live SQL Server/PostgreSQL/Oracle servers and a live Pressroom printer/PDF service are not part of this local test.
