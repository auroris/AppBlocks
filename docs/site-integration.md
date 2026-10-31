# Adding AppBlocks to a site

## Installation

Install the locally built `AppBlocks` package, or reference `src/AppBlocks/AppBlocks.csproj` during development. Its Umbraco composer registers services, management screens and routes automatically. AppBlocks installs its embedded element and data type definitions directly through Umbraco's services on startup, preserving existing site definitions and their stable keys. Installation needs no synchronization package or import configuration. Missing definitions and their folders are installed in one transaction, so a failed installation can be retried on the next startup. Only fresh installs are supported; there is no schema upgrade or migration path. Data types and document/element types are grouped under **AppBlocks** in their respective backoffice trees, with subfolders for Forms, Data sources and Interactive Reports. These folders are created during installation on a fresh site. Install `AppBlocks.InteractiveReports` when the site needs interactive reports; NuGet brings in AppBlocks as its required dependency. Its composer adds report services and routes. See the [companion guide](../src/AppBlocks.InteractiveReports/README.md).

## Rendering

Sites using Umbraco's conventional `blockgrid/Components` or `blocklist/Components` partial rendering pick up the packaged components automatically. A site with its own rendering convention can invoke `AppBlocksBlock` with `block` (a `BlockGridItem`) or `listItem` (a `BlockListItem`). Pass `page` explicitly when rendering outside the current routed page.

```cshtml
@await Component.InvokeAsync("AppBlocksBlock", new { block = Model })
```

## Access and caching

The native blocks include their required assets and inherit ordinary site typography. Public page content stays visible. On a page without Public Access an anonymous visitor reaches its interactive blocks, and whatever is behind each block decides: a C# form class with no check of its own is public, while the data source engine, tabular forms, schedules and paper reports show a sign-in notice until someone signs in. Interactive blocks also work for an authenticated Umbraco Member or backoffice User. `IDataSourceUserResolver` exposes the account type and current groups; the application decides who may read or change its data. Backoffice administrators receive no automatic application permissions. Umbraco Public Access still protects Member pages, and configured data-source Member Groups and application policies also apply to API requests. See the [identity and access examples](business-rules.md#group-membership) and the Public contact form recipe there.

A page with an interactive block, and any response Public Access protects, depends on who asks, so it is sent `no-store` and stays out of ASP.NET Core's output cache if the site uses one. The blocks' `/api/` endpoints send `no-store` too but do not opt out, so keep them out of any output cache policy you add.
