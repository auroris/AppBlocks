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

Beside the stylesheet, two runtime modules are public for a site's own scripts. `App_Plugins/ApplicationBlocks/data-api.js` sends requests to AppBlocks' endpoints with the visitor's cookies and the site's path base, and turns a refusal into text to show; the [API endpoints guide](api-endpoints.md#calling-endpoints-from-a-page) has an example. `App_Plugins/ApplicationBlocks/schedule.js` builds a schedule's bars and row labels and mounts its tooltip and popovers on a table the page lays out itself; the [Schedule block guide](schedule.md#a-schedule-of-your-own) describes the markup. Neither mounts anything when imported. `runtime.js` itself is what the blocks load, and mounts every block on the page.

A form whose **Label position** is `left` puts its labels in a column beside the inputs. A field narrower than about 26rem, and every field on a phone, stacks its label above the input again.

## Previews in the backoffice

In a block grid, every AppBlocks block shows a preview in place of Umbraco's default card, except Full width and Three columns, which keep Umbraco's card and their areas. A preview keeps Umbraco's own header, with the block's icon, label, **Not published** tag and edit link, and draws the block's authoring content beneath it, then the block's areas. A Form's form interface shows the same preview inside its single-block property, under Umbraco's single-block header. Fields appear as disabled controls with their label, placeholder, default value and help text. Rich text and HTML appear as authored, CSS and JavaScript as code, and forms, tabular forms, schedules, attachments and the anti-spam check as representative markup that names the form class or data source they use. A Forms container shows the forms inside it. With Interactive Reports installed, a report shows its heading, buttons and the columns of its starting view.

Nothing in a preview executes. No form class, SQL, data source, report action or page script runs, and authored HTML is sanitized and drawn in a sandboxed frame that allows no scripts, forms or requests to other origins. The only requests a preview makes are for the stylesheets below, for images, and for the media items behind a picked image. Images and stylesheets on another origin, such as a CDN, do not load in a preview.

Each preview is a `blockEditorCustomView` extension with the alias `AppBlocks.Preview.<content type alias>`, for example `AppBlocks.Preview.appHtml`, registered for `block-grid` (the form interface's single-block preview is `AppBlocks.Preview.appFormInterface.Single`, and the report preview is `AppBlocks.Reports.Preview.appReport`). AppBlocks leaves their `weight` unset, which is 0. A block editor shows the one custom view with the highest weight for a block, so a site replaces a preview by registering its own view for the same `forContentTypeAlias` and `forBlockEditor` with a higher weight:

```json
{
  "type": "blockEditorCustomView",
  "alias": "MySite.Preview.appHtml",
  "name": "HTML preview",
  "element": "/App_Plugins/MySite/html-preview.js",
  "forContentTypeAlias": "appHtml",
  "forBlockEditor": "block-grid",
  "weight": 100
}
```

Naming `AppBlocks.Preview.appHtml` in the view's `overwrites` removes AppBlocks' view outright instead. To go back to Umbraco's default card for a block, an `appEntryPoint` excludes the preview: its `onInit(host, extensionRegistry)` calls `extensionRegistry.exclude("AppBlocks.Preview.appHtml")`.

A site makes previews look like its pages from an `appEntryPoint`. `App_Plugins/ApplicationBlocks/block-preview.js` exports two arrays that every preview reads when it draws. `previewStylesheets` holds stylesheet URLs, and starts with AppBlocks' runtime stylesheet; a site adds its own frontend stylesheet. `previewStyles` holds CSS text that comes after the stylesheets and the preview's own rules; a site adds preview-only overrides, such as showing every panel of a component its pages reveal with script, which never runs in a preview. Relative URLs resolve against the site root, path base included:

```js
// App_Plugins/MySite/preview-styles.js, registered as an appEntryPoint
import { previewStylesheets, previewStyles } from "../ApplicationBlocks/block-preview.js";

export function onInit() {
    previewStylesheets.push("css/site.css");
    previewStyles.push(".tab-panel{display:block}.carousel-controls{display:none}");
}
```

Another package previews its own blocks by extending `BlockPreview` from the same module, so they get the same header, frame, sanitizer and areas. A subclass overrides `renderContent(alias, content, settings)`, which receives the block's content and settings values by property alias and returns markup as a string, or an empty string to show the header and areas alone. `App_Plugins/ApplicationBlocks/preview-model.js` has the helpers the AppBlocks previews use, such as `escape`, `heading`, `note`, `button` and `table`, and `this.mediaImage(value)` draws a media picker's image. The package defines its element and registers manifests for its own content type aliases only, never for AppBlocks':

```js
// App_Plugins/MyPackage/block-preview.js
import { BlockPreview } from "../ApplicationBlocks/block-preview.js";
import { heading, note, escape } from "../ApplicationBlocks/preview-model.js";

export default class QuotePreview extends BlockPreview {
    renderContent(alias, content, settings) {
        return heading(content.title) + `<blockquote>${escape(content.quote)}</blockquote>` + note(content.author || "Add an author.");
    }
}
customElements.define("my-package-block-preview", QuotePreview);
```

```json
{
  "type": "blockEditorCustomView",
  "alias": "MyPackage.Preview.myQuote",
  "name": "Quote preview",
  "element": "/App_Plugins/MyPackage/block-preview.js",
  "forContentTypeAlias": "myQuote",
  "forBlockEditor": "block-grid"
}
```

The header follows the manifest's `forBlockEditor`: `block-list` and `block-single` get those editors' headers and no areas, and anything else gets the grid's. These identifiers are Umbraco's `UMB_BLOCK_GRID`, `UMB_BLOCK_LIST` and `UMB_BLOCK_SINGLE` values, distinct from the property-editor aliases such as `Umbraco.BlockGrid` used in stored block layouts. A package whose blocks can sit inside a Forms container also registers how to draw them there, from an `appEntryPoint`, by content type key: `nestedBlockPreviews.set("<key>", (content, settings) => markup)`, with `nestedBlockPreviews` imported from the same module.

## Access and caching

The native blocks include their required assets and inherit ordinary site typography. Public page content stays visible. On a page without Public Access an anonymous visitor reaches its interactive blocks, and whatever is behind each block decides: a C# form class with no check of its own is public. SQL data sources offer **Public**, **Not public**, and **Specific groups** access; a new one starts with Public, which follows the page, and an empty value is an error. Public sources allow anonymous reads through forms, lists of values, tabular forms, schedules and [API endpoints](api-endpoints.md); writes require sign-in unless that source explicitly enables **Allow anonymous writes**. A tabular form also checks its owning record, and a schedule needs both sources to be Public. Queries and row restrictions using `:USER_*` values still require sign-in. Paper report downloads require a signed-in account. Interactive blocks also work for an authenticated Umbraco Member or backoffice User. `IDataSourceUserResolver` exposes the account type and current groups; the application decides who may read or change its data. Backoffice administrators receive no automatic application permissions. Umbraco Public Access protects Member pages for everyone, a backoffice User without a Member session included. A host that wants to admit someone Umbraco refuses, such as the page's editors, registers an `IProtectedPageAdmission`, and the page and its block endpoints follow the same answer. Configured data-source Member Groups and application policies still apply to API requests. See the [identity and access examples](business-rules.md#group-membership) and the Public contact form recipe there.

The `AppBlocks:NoStorePages` setting controls AppBlocks' page cache headers:

| Value | Behavior |
| --- | --- |
| `AppBlocksPages` (default) | Send `no-store` and prevent output-cache storage for pages with a block whose output depends on who asks: a form, attachments, a schedule, a tabular form, or a plugin block that requires a user or varies by data. Their Public Access login/denied responses follow. A page of content blocks alone is cached as Umbraco and the host decide. |
| `AllPages` | Apply that behavior to every Umbraco-routed website page. |
| `Off` | Add no page cache restrictions from AppBlocks; the host and Umbraco control caching. |

```json
{ "AppBlocks": { "NoStorePages": "AppBlocksPages" } }
```

The blocks' `/api/` endpoints keep their own `no-store` headers in every mode, but do not opt out of output caching, so exclude them from any output cache policy you add. [API endpoints](api-endpoints.md) answer below `/api/data`, or `AppBlocks:ApiBasePath`, with `no-store` too; they have no page, so their data source's access decides alone, and AppBlocks adds no CORS policy for them. Antiforgery and other host middleware may also set their own cache headers, including when the setting is Off. A cookie for a disabled or locked-out account stays refused by AppBlocks on an open page; it is not treated as an anonymous visitor.

## Companion packages

A companion package can add blocks of its own and controllers of its own beside the ones AppBlocks provides.

### Blocks

A companion package adds a block by registering an `IAppBlockPlugin`, and two of its members decide how AppBlocks treats a page that has the block:

| Member | When true |
| --- | --- |
| `RequiresUser` | The block acts for the signed-in person, as a report does. Publishing refuses it inside a form, and its page is kept out of caches. |
| `VariesByData` | The block's output can change without a publish, such as a public list read from a database. Its page is rendered for each request and kept out of caches, so visitors see current data. |

A public block whose output comes from a database sets `VariesByData` and leaves `RequiresUser` false. A block that requires the user is kept out of caches whatever `VariesByData` answers. `VariesByData` has a default implementation that answers `RequiresUser`, so a plugin written before it compiles unchanged and keeps its behaviour.

```csharp
public sealed class NoticesBlock : IAppBlockPlugin
{
    public string Alias => "noticeBoard";
    public string ViewPath => "~/Views/Partials/Notices/Board.cshtml";
    public bool RequiresUser => false;
    // The notices come from the database, so a cached copy of the page would go stale.
    public bool VariesByData => true;
    public void Validate(JsonElement block, List<string> errors) { }
}
```

### Controllers for signed-in Members

A companion's own API controller for signed-in Members uses the `AppBlocks.Member` authorization policy, `AppBlocksAuthorization.MemberPolicy`. It reads Umbraco's Member cookie and admits a Member whose account is approved and not locked out, read afresh on every request as the blocks read it. Marking a controller `[Authorize(AuthenticationSchemes = IdentityConstants.ApplicationScheme)]` instead gets a caller with no session a 302 to the Member login page, which a script cannot use. The policy answers an API-style request with a status the script can act on:

| Caller | API-style request | Any other request |
| --- | --- | --- |
| No Member session | 401 | Umbraco's redirect to the login page |
| A Member who is locked out or unapproved | 403 | Umbraco's access-denied redirect |

A request is API-style when its path is under `/api/`, its controller is an `[ApiController]`, it accepts JSON, or it carries `X-Requested-With: XMLHttpRequest`. A backoffice User without a Member session counts as having no Member session.

A signed-in Member's script gets an antiforgery token from `GET /api/antiforgery/token`, which answers `{ "token": "...", "headerName": "RequestVerificationToken" }` with `Cache-Control: no-store`, and 401 to anyone else. The token is bound to the Member, as the blocks' own tokens are, so it validates on a controller under the policy. Send it in the named header with each change:

```csharp
using AppBlocks.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/notices")]
[Authorize(Policy = AppBlocksAuthorization.MemberPolicy)]
public sealed class NoticesController(NoticeStore notices) : ControllerBase
{
    [HttpGet]
    public IActionResult List() => Ok(notices.ForMember(User.FindFirstValue(ClaimTypes.NameIdentifier)));

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Post(NoticeInput input) => Ok(notices.Add(input));
}
```

```js
const { token, headerName } = await (await fetch("/api/antiforgery/token")).json();
await fetch("/api/notices", {
    method: "POST",
    headers: { "Content-Type": "application/json", [headerName]: token },
    body: JSON.stringify({ title: "Runway closed" }),
});
```

A mapped endpoint takes the same policy with `RequireAuthorization(AppBlocksAuthorization.MemberPolicy)`. The policy decides only that the caller is a current Member; the controller still decides which Members may do what.

### Backoffice controllers for administrators

A companion's management API controller that only administrators may use takes the `AppBlocks.Administrator` policy, `AppBlocksAuthorization.AdministratorPolicy`, as AppBlocks' own Settings screens do. It admits a backoffice User in Umbraco's Administrators group whose account is approved and not locked out, read afresh on every request, so a User taken out of the group or locked out is refused at once, and it answers anyone else with 403. It adds to the backoffice access every management controller already requires, and it can sit on the controller or on single actions beside other policies:

```csharp
using AppBlocks.Authentication;
using Microsoft.AspNetCore.Authorization;

[VersionedApiBackOfficeRoute("notices/settings")]
[Authorize(Policy = AppBlocksAuthorization.AdministratorPolicy)]
public sealed class NoticeSettingsController(NoticeSettings settings) : ManagementApiControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(settings.Read());
}
```

AppBlocks' connection list, database types, table list and guide stay open to every backoffice user, because the data source and report pickers read them.

### Names a companion owns

A companion registers the names it owns with AppBlocks from its own registration, through `builder.AddAppBlocks(appBlocks => ...)`:

- `AddKeySegmentContentType("alias")` gives the nodes of a definition document type a URL segment of their own key, as data source nodes have, so a definition named like a page never takes that page's URL. The reports companion registers its report definitions this way.
- `ReserveConnectionName("name")` keeps a connection the companion opens through its own factory from being saved on the Connections screen, where a saved connection would take the name over. The reports companion reserves `ReportsDb`.

### Nodes a package generates

API endpoints, lists of values and data sources are content nodes, which editors publish. A package that generates some of them from its own code, so that their SQL follows the package's rules, installs them with `OwnedNodeInstaller`. Each node is an `OwnedNode`: a fixed key, a name, the document type alias and the property values by alias, as the backoffice stores them.

```csharp
public sealed class NoticeDefinitions(IServiceScopeFactory scopes) : INotificationHandler<UmbracoApplicationStartedNotification>
{
    public void Handle(UmbracoApplicationStartedNotification notification)
    {
        // A scope of its own, as the publish checks the installer runs use scoped services.
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<OwnedNodeInstaller>().Install([
            new OwnedNode(NoticesEndpointKey, "Notices API", "apiEndpoint", new Dictionary<string, object?> {
                ["alias"] = "notices.current", ["route"] = "notices", ["connection"] = "NoticesDb", ["access"] = "Public",
                ["sql"] = NoticeRules.CurrentSql, ["shape"] = "Collection",
            }, Parent: DataSourcesRootKey),
        ]);
    }
}
```

The installer keeps a SHA-256 fingerprint of each node's definition in Umbraco's key-value table, under `AppBlocks:OwnedNode:<key>`. A start whose definition has the stored fingerprint changes nothing, so the node keeps its published version. A changed definition updates the node and publishes it, which runs the publish checks of its type: an endpoint's SQL is checked against its database at that start, and a definition the checks refuse throws an `OwnedNodeException` with their reasons, publishes nothing and records nothing, so the next start tries again. `Install` answers `Created`, `Updated`, `Unchanged` or `LeftDeleted` for each node.

The package owns the properties its definition names. An editor's change to one of them lasts until the package's definition next changes, and a property the definition leaves out keeps whatever an editor gives it. A node an editor deleted or put in the recycle bin comes back at the next start; pass `recreateDeleted: false` to leave it deleted. For a document type that varies by culture, give each culture's name and values in `Cultures`.

Call the installer once the things its nodes are checked against exist: a list or endpoint's database and tables, and the data source a node selects. Install a parent before its children, as `Install` does with a list in order.
