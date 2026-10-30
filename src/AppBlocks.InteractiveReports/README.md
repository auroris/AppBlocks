# AppBlocks.InteractiveReports

Optional Interactive Reports companion for **AppBlocks** on Umbraco 18.3 / .NET 10. This package requires AppBlocks; NuGet installs that dependency automatically.

```powershell
dotnet add package AppBlocks.InteractiveReports --version 0.1.0-alpha.1
```

This is an unpublished alpha. Use the locally built packages until a public release is available.

The Umbraco composer registers the companion automatically. On startup, AppBlocks installs its missing report element and data type definitions alongside the base schema. Add **Interactive Report** to an existing Block Grid or Block List, choose a published AppBlocks SQL data source, and configure the block's starting view. The site retains its page types, templates and layout.

The companion supplies the report block, native rendering partials, report editors, browser runtime, JSON and download endpoints, saved views, authorization and optional backoffice reports. AppBlocks supplies connections, data sources, forms and page access. Installing AppBlocks alone adds no Interactive Reports dependency, endpoints or report schema.

Report requests use AppBlocks page access and data-source restrictions. Existing block keys, editor aliases and `/api/reports`, `/api/download`, `/umbraco/reports` and `/umbraco/report-download` routes are preserved. Existing report-name bindings and published report definitions remain supported. Print actions use AppBlocks' optional document-link capability when installed.

Register shared business connections through AppBlocks:

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
