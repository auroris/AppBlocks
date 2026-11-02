# Adding AppBlocks to a site

## Installation

Install the locally built `AppBlocks` package, or reference `src/AppBlocks/AppBlocks.csproj` during development. Its Umbraco composer registers services, management screens and routes automatically. AppBlocks installs its embedded element and data type definitions through Umbraco’s package importer during tracked package migrations, preserving existing site definitions and their stable keys. Installation needs no synchronization package or import configuration. Missing definitions and their folders are installed in one transaction, so a failed installation can be retried on the next startup. Automatic installation respects `Umbraco:CMS:PackageMigration:RunSchemaAndContentMigrations` and preserves definitions that already exist: a new version of the package carries its changes to existing sites through Umbraco's package migrations, and **Settings → AppBlocks → Definitions** shows where each package's definitions and migrations stand, with **Reinstall** to reset every definition to the installed package and **Uninstall** to remove them all, as [Developing AppBlocks](developing.md#who-owns-the-definitions) describes. Data types and document/element types are grouped under **AppBlocks** in their respective backoffice trees, with subfolders for Forms, Data sources and Interactive Reports. These folders are created during installation on a fresh site. Install `AppBlocks.InteractiveReports` when the site needs interactive reports; NuGet brings in AppBlocks as its required dependency. Its composer adds report services and routes. See the [companion guide](../src/AppBlocks.InteractiveReports/README.md).

## Rendering

Sites using Umbraco's conventional `blockgrid/Components` or `blocklist/Components` partial rendering pick up the packaged components automatically. A site with its own rendering convention can invoke `AppBlocksBlock` with `block` (a `BlockGridItem`) or `listItem` (a `BlockListItem`). Pass `page` explicitly when rendering outside the current routed page.

```cshtml
@await Component.InvokeAsync("AppBlocksBlock", new { block = Model })
```

## Styling

The runtime stylesheet, `App_Plugins/ApplicationBlocks/runtime.css`, gives forms, attachments, tabular forms and schedules a complete look that inherits the site's font and base size. Its colours, radii and shadows are custom properties with an `--app-` prefix, declared on `:root`, so a site restyles the blocks by redefining them, on `:root` or on `.application-blocks`, without overriding a rule:

```css
.application-blocks {
    --app-accent: #5143a3;        /* buttons, links, focus rings and today's column */
    --app-accent-strong: #3f3482; /* the primary button on hover */
    --app-accent-soft: #eeebf8;   /* tinted backgrounds: new rows, today's column */
    --app-radius: .5rem;          /* the form card and regions */
    --app-radius-sm: .25rem;      /* controls, buttons and tables */
}
```

The full list is at the top of the stylesheet: the surfaces (`--app-surface`, `--app-surface-2`, `--app-surface-3`), borders, text (`--app-text`, `--app-muted`), the danger colours for errors and the Delete button, `--app-shadow` and `--app-focus`. A schedule's bar colours are `--ah-schedule-kind-1` to `--ah-schedule-kind-8`, and `--ah-schedule-marker-height` is the height of its marker bands; the [Schedule block guide](schedule.md) has the columns that colour rows and bars from the data. The `ah-*` hooks (`ah-grid`, `ah-layout`, `ah-region`, and each block's title and help) are left to the site's theme; the native rendering styles them itself inside `.appblocks-native`, so a site without a theme still gets a grid, column layouts and framed regions.

A form whose **Label position** is `left` puts its labels in a column beside the inputs. A field narrower than about 26rem, and every field on a phone, stacks its label above the input again.

## Access and caching

The native blocks include their required assets and inherit ordinary site typography. Public page content stays visible. On a page without Public Access an anonymous visitor reaches its interactive blocks, and whatever is behind each block decides: a C# form class with no check of its own is public. SQL data sources offer **Public**, **Not public**, and **Specific groups** access; a new one starts with Public, which follows the page, and an empty value is an error. Public sources allow anonymous reads through forms, lists of values, tabular forms, schedules and [API endpoints](api-endpoints.md); writes require sign-in unless that source explicitly enables **Allow anonymous writes**. A tabular form also checks its owning record, and a schedule needs both sources to be Public. Queries and row restrictions using `:USER_*` values still require sign-in. Paper report downloads require a signed-in account. Interactive blocks also work for an authenticated Umbraco Member or backoffice User. `IDataSourceUserResolver` exposes the account type and current groups; the application decides who may read or change its data. Backoffice administrators receive no automatic application permissions. Umbraco Public Access protects Member pages for everyone, a backoffice User without a Member session included. A host that wants to admit someone Umbraco refuses, such as the page's editors, registers an `IProtectedPageAdmission`, and the page and its block endpoints follow the same answer. Configured data-source Member Groups and application policies still apply to API requests. See the [identity and access examples](business-rules.md#group-membership) and the Public contact form recipe there.

The `AppBlocks:NoStorePages` setting controls AppBlocks' page cache headers:

| Value | Behavior |
| --- | --- |
| `AppBlocksPages` (default) | Send `no-store` and prevent output-cache storage for pages with a block whose output depends on who asks: a form, attachments, a schedule, a tabular form or a plugin block that requires a user. Their Public Access login/denied responses follow. A page of content blocks alone is cached as Umbraco and the host decide. |
| `AllPages` | Apply that behavior to every Umbraco-routed website page. |
| `Off` | Add no page cache restrictions from AppBlocks; the host and Umbraco control caching. |

```json
{ "AppBlocks": { "NoStorePages": "AppBlocksPages" } }
```

The blocks' `/api/` endpoints keep their own `no-store` headers in every mode, but do not opt out of output caching, so exclude them from any output cache policy you add. [API endpoints](api-endpoints.md) answer below `/api/data`, or `AppBlocks:ApiBasePath`, with `no-store` too; they have no page, so their data source's access decides alone, and AppBlocks adds no CORS policy for them. Antiforgery and other host middleware may also set their own cache headers, including when the setting is Off. A cookie for a disabled or locked-out account stays refused by AppBlocks on an open page; it is not treated as an anonymous visitor.
