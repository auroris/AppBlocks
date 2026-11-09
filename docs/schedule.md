# The Schedule block

A Schedule block draws resources against time: the rows of one data source down the side, the bars of another across a window of days or hours. It is a Gantt chart whose content comes entirely from two queries, so the block itself has few settings. Anything that varies per row or per bar is a column of a source, colours come from the site's theme or from the data, and a bar's behaviour is a link or a description. The block is in the base package and renders through the data source engine. Set each source's required Access value to Public to follow page access; a source that requires an account shows anonymous visitors a sign-in notice.

## Settings

| Setting | What it does |
| --- | --- |
| Heading, Help | The title above the schedule and a line under it. |
| Rows source | The alias of a published report definition that selects a data source, or of a data source. One schedule row per row. |
| Bars source | The alias of the source whose rows are the bars. |
| Days before today, Days after today | The window when the page's address carries no `from` and `to`. Empty is 7 and 14. |
| Columns | Days, or one column per hour of every day in the window. A window of hour columns is cut to 31 days, or `AppBlocks:Schedule:MaxHourDays`. |
| Link template | Where a bar without a `link` value leads, such as `/orders-form/?id={key}`. `{key}` and any other bar column in braces are replaced, URL-encoded. An address starting with `/` is from the site's root. |
| Hide empty rows | Leave out rows with no bar in the window, and say how many were left out. |

## The window

The window comes from the page's address, `?from=2026-11-01&to=2026-11-30`, or from the days before and after today. Readers move it with the Earlier, Today and Later links, which keep every other parameter of the address, and with the Jump to menu, which lists the next twelve months and six quarters as links. A window longer than 400 days, or `AppBlocks:Schedule:MaxDays`, is cut at its end. Saturday and Sunday are shaded as the weekend; a site whose weekend falls on other days sets `AppBlocks:Schedule:WeekendDays`, such as `["Friday", "Saturday"]`.

The dates shown reach both sources as `:PARAM.from` and `:PARAM.to`, so a bars query can select only the bars that touch the window:

```sql
SELECT id AS "key", room AS "row", arrives AS "start", leaves AS "end", guest AS "label", purpose AS "kind", NULL AS "link"
FROM stays
WHERE leaves >= :PARAM.from AND arrives <= :PARAM.to
```

## Columns of the sources

Column names are matched in any letter case, by the column's own name or by the field alias a data source maps it to. A rows source needs `key`, `label` and `group`; a bars source needs `key`, `row`, `start`, `end`, `label`, `kind` and `link`. Publishing refuses a block whose sources lack one of these.

| Column | Source | Meaning |
| --- | --- | --- |
| `key` | both | The row's or bar's identity. A row without a key, or with a key seen before, is left out. |
| `label` | both | The text shown; empty shows the key. |
| `group` | rows | Rows with the same group gather under a heading, in the order the groups first appear. Empty rows stand on their own. |
| `row` | bars | The key of the row the bar belongs to. A bar on an unknown row is left out. |
| `start`, `end` | bars | Dates or date-times. A bar with no end runs to the window's edge with an arrow; one that crosses an edge is clamped and dotted there. |
| `kind` | bars | Groups bars for the legend and gives them a colour, the first eight kinds in turn. |
| `link` | bars | Where the bar leads: a path from the site's root, starting with one `/`, or an `http`, `https`, `mailto` or `tel` address. Empty, or anything else, falls back to the link template. |

Rows keep the order the query returns them, so an `ORDER BY` is the ordering. Bars that overlap in time within one row are packed into lanes, so each stays visible. A schedule reads at most 2000 rows and 5000 bars, or `AppBlocks:Schedule:MaxRows` and `MaxBars`; beyond them it shows a problem in place of the table, and the query needs narrowing.

The same sources may carry four more columns. None is required.

| Column | Source | Meaning |
| --- | --- | --- |
| `color`, `textColor` | both | A background and a text colour, as a hex value such as `#b35c00` or a colour name. A row's colours go on its label and on its bars; a bar's own colours win over its row's. Anything that is not a hex value or a name is ignored, so a value from a row never writes CSS of its own. |
| `description` | both | Text a reader opens from the bar or from the row's label, with the bar's dates and its link. A bar with a description is a button rather than a link, so its link is offered inside the details. Line breaks are kept. |
| `orientation` | rows | `true`, `1` or `vertical` makes the row a band of markers. See below. |

A bar's `link` is held to a rule for the same reason as its colours, so a value from a row never runs script in a reader's browser. It is used only when it is a path from the site's root, which starts with one `/` (a value starting `//` or `/\` names another host), or an address whose scheme is `http`, `https`, `mailto` or `tel`. Every other value counts as no value, so the link template applies, or the bar has no link when the block has no template: other schemes, `javascript:` among them, relative paths such as `./`, `../` and bare names, a value starting with `?` or `#`, and any value holding a control character such as a tab or a line break. The link template is the editor's and is used as written.

## Vertical rows and markers

A row whose `orientation` is true is drawn across the top of the schedule instead of down the side. Each of its bars becomes a marker: its label is written top to bottom in a band as tall as its lanes, and its columns are shaded with its colour through every row below, behind the horizontal bars. That is what a statutory holiday, a shutdown or an exercise looks like on everyone's schedule at once. A marker is a bar in every other way. It can span several columns, it is packed into lanes when two markers share a day, and it carries the same link, colours and description. The one difference is that a marker without an end covers its start column only, since a holiday has no "onward".

A rows source of activity categories, with holidays as a vertical category, looks like this:

```sql
SELECT id AS "key", name AS "label", NULL AS "group", orientation, backgroundcolor AS "color", textcolor AS "textColor", description
FROM activity_category
ORDER BY ordering, name
```

## One schedule for every tenant

When each branch of an organisation gets its own copy of the schedule page under its own part of the site, as `/en/harbour/activity-schedule/` and `/en/clubs/northgate/activity-schedule/`, the data sources need no copy. The application decides what a tenant is, in one registered `IBindVariableProvider` that reads the page from the execution context, and a SELECT binds the answer as `:APP.tenant`. A bars source filtered with `WHERE tenant_id = :APP.tenant AND enddate >= :PARAM.from AND startdate <= :PARAM.to` serves every copy, and a new tenant is a page copy and nothing else. The [worked example](activity-schedule-example.md) builds the whole application around it, provider included.

## Colours and styling

Bars without a colour of their own take their kind's colour, `--ah-schedule-kind-1` to `--ah-schedule-kind-8`, which a site redefines on `:root` or on `.application-blocks` as it does the other `--app-` tokens. A marker band's lane height is `--ah-schedule-marker-height`, 7rem by default. The runtime stylesheet shades a marked column with 16% of the marker's colour over the surface.

Every bar renders `data-key` and `data-kind`, and a vertical row's bars carry `app-schedule-bar--vertical`, so a JavaScript block on the page can attach any behaviour the block itself does not offer.

## A schedule of your own

A page can lay out a schedule table itself, from rows it loads with the [data API client](api-endpoints.md#calling-endpoints-from-a-page) or from anywhere else, and still look and behave like the block: the same bars, tooltip and popovers. The page links `runtime.css` and imports `App_Plugins/ApplicationBlocks/schedule.js`, which exports three functions. Importing the module mounts nothing; the block's own pages load it through `runtime.js`.

```html
<link rel="stylesheet" href="/App_Plugins/ApplicationBlocks/runtime.css">
<div class="application-blocks"><section class="app-schedule" id="rooms"></section></div>
<script type="module">
    import { mountSchedule, scheduleBar, scheduleRowLabel } from "/App_Plugins/ApplicationBlocks/schedule.js";
    const root = document.getElementById("rooms");
    // ...build the table below, appending scheduleRowLabel(row) to each tr and scheduleBar(bar) to the cell where a bar starts...
    const dispose = mountSchedule(root);
</script>
```

`scheduleBar(bar)` returns a fragment to append to the cell of the column where the bar starts: the bar, and after it its popover when it has a description. Every property is optional except `label`.

| Property | What it does |
| --- | --- |
| `label` | The text on the bar. |
| `details` | Text after the label, read by screen readers and shown in the tooltip, such as `: Room 1, 3 Nov 2026 to 5 Nov 2026, Course`. The block writes the row, the dates and the kind. |
| `span`, `lane` | The number of columns the bar covers, 1 by default, and the lane of its row it sits in, from 0. The row's `--ah-lanes` says how many lanes it has. |
| `kindIndex` | 1 to 8 colours the bar with `--ah-schedule-kind-1` to `-8`, and higher numbers wrap around. |
| `color`, `textColor` | The bar's own background and text colour. Only a hex value or a colour name is used, as with the block's columns; a colour of its own replaces the kind's. |
| `openEnded`, `clippedStart`, `clippedEnd` | The bar runs on past the window's end with an arrow, or is cut and dotted at the window's start or end. |
| `vertical` | The bar is a marker in a vertical row. |
| `key`, `kind` | Rendered as `data-key` and `data-kind`. |
| `link` | Where the bar leads, under the same rule as the block's `link` column: a path from the site's root or an `http`, `https`, `mailto` or `tel` address, and anything else is ignored. A bar with neither a link nor a description is a focusable `span`. |
| `description` | Text, or nodes the page built, for the bar's popover. It makes the bar a button that opens the popover, which also shows `when` and the link. |
| `when`, `linkText`, `closeLabel` | The popover's line under its title, the text of its link, `Open link` by default, and the accessible name of its close button, `Close` by default. |

`scheduleRowLabel(row)` returns the `th` that starts a row, from `label`, `color`, `textColor`, `description` and `closeLabel`; a description makes the label a button that opens it.

`mountSchedule(root)` scrolls today's column into view, shows a bar's `textContent` as a tooltip on hover and keyboard focus, hides it on Escape and when anything scrolls, and places a popover under the bar or label that opened it, inside the viewport. It finds bars and popovers inside the root as they are used, so the page can draw the table again, for another window or new data, without mounting again. It returns a function that removes its listeners and tooltip. Mounting the same root again replaces the earlier mount, so mounting after every redraw is also fine.

The table follows the block's own markup, which `runtime.css` styles. Class names not listed here are the block's own and may change.

| Element | Class and attributes |
| --- | --- |
| The root | `section.app-schedule`, inside an element with `application-blocks` for the site's `--app-` tokens. The tooltip is placed inside it. |
| The scroller | `div.app-schedule-scroll`, the root's child that scrolls the table and keeps its header and labels in place. |
| The table | `table.app-schedule-table`, with `app-schedule-table--days` or `app-schedule-table--hours` for the column width. A `caption.app-schedule-caption` is read by screen readers only. |
| The header | A `thead` whose first cell is `th.app-schedule-corner` holding an empty `span.app-schedule-label`, then one `th.app-schedule-col` per column. A two-row header puts `th.app-schedule-period` cells with `colspan` in the first row and gives the corner `rowspan="2"`. A column's `span.app-schedule-day` and `span.app-schedule-weekday` stack its number and weekday. |
| Column states | `app-schedule-col--today` on today's header and body cells, which the mount scrolls to; `app-schedule-col--weekend` for shading; `app-schedule-col--marked` with `--ah-mark` or a `app-schedule-kind-N` class for a column under a marker. |
| A group | `tbody.app-schedule-group`, optionally starting with `tr.app-schedule-group-title` whose `th` spans the table and holds a `span.app-schedule-label`. |
| A row | `tr.app-schedule-row` with `style="--ah-lanes: N"`, `app-schedule-row--vertical` for a band of markers, then `scheduleRowLabel(row)` and one `td.app-schedule-cell` per column. |
| A bar | `scheduleBar(bar)` in the cell where it starts. Its parts are `.app-schedule-bar`, `.app-schedule-bar__label`, `.app-schedule-bar__details` and, for a description, `.app-schedule-popover` opened by `popovertarget`. |

The block also draws a toolbar, an empty message and a legend: `.app-schedule-toolbar`, `.app-schedule-empty`, and `ul.app-schedule-legend` whose items start with `span.app-schedule-swatch` and a kind class.

## Access

The sources are read through the data source engine, so each source's Access setting, read groups, row restriction and policies decide what each reader sees. On a page without Public Access an anonymous visitor can see a schedule when both sources are Public and neither query nor row restriction uses `:USER_*` binds. Otherwise they see a sign-in notice, as the [site integration guide](site-integration.md) describes. A source whose data cannot be read shows a short message in place of the table and never fails the page.
