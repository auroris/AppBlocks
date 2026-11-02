# The Schedule block

A Schedule block draws resources against time: the rows of one data source down the side, the bars of another across a window of days or hours. It is a Gantt chart whose content comes entirely from two queries, so the block itself has few settings. Anything that varies per row or per bar is a column of a source, colours come from the site's theme or from the data, and a bar's behaviour is a link or a description. The block is in the base package and renders through the data source engine. Sources default to Public and follow page access; a source that requires an account shows anonymous visitors a sign-in notice.

## Settings

| Setting | What it does |
| --- | --- |
| Heading, Help | The title above the schedule and a line under it. |
| Rows source | The alias of a published report definition that selects a data source, or of a data source. One schedule row per row. |
| Bars source | The alias of the source whose rows are the bars. |
| Days before today, Days after today | The window when the page's address carries no `from` and `to`. Empty is 7 and 14. |
| Columns | Days, or one column per hour of every day in the window. A window of hour columns is cut to 31 days. |
| Link template | Where a bar without a `link` value leads, such as `/orders-form/?id={key}`. `{key}` and any other bar column in braces are replaced, URL-encoded. An address starting with `/` is from the site's root. |
| Hide empty rows | Leave out rows with no bar in the window, and say how many were left out. |

## The window

The window comes from the page's address, `?from=2026-11-01&to=2026-11-30`, or from the days before and after today. Readers move it with the Earlier, Today and Later links, which keep every other parameter of the address, and with the Jump to menu, which lists the next twelve months and six quarters as links. A window longer than 400 days is cut at its end.

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
| `link` | bars | Where the bar leads. Empty falls back to the link template. |

Rows keep the order the query returns them, so an `ORDER BY` is the ordering. Bars that overlap in time within one row are packed into lanes, so each stays visible.

The same sources may carry four more columns. None is required.

| Column | Source | Meaning |
| --- | --- | --- |
| `color`, `textColor` | both | A background and a text colour, as a hex value such as `#b35c00` or a colour name. A row's colours go on its label and on its bars; a bar's own colours win over its row's. Anything that is not a hex value or a name is ignored, so a value from a row never writes CSS of its own. |
| `description` | both | Text a reader opens from the bar or from the row's label, with the bar's dates and its link. A bar with a description is a button rather than a link, so its link is offered inside the details. Line breaks are kept. |
| `orientation` | rows | `true`, `1` or `vertical` makes the row a band of markers. See below. |

## Vertical rows and markers

A row whose `orientation` is true is drawn across the top of the schedule instead of down the side. Each of its bars becomes a marker: its label is written top to bottom in a band as tall as its lanes, and its columns are shaded with its colour through every row below, behind the horizontal bars. That is what a statutory holiday, a shutdown or an exercise looks like on everyone's schedule at once. A marker is a bar in every other way. It can span several columns, it is packed into lanes when two markers share a day, and it carries the same link, colours and description. The one difference is that a marker without an end covers its start column only, since a holiday has no "onward".

A rows source for categories like the ones in the original activity schedule, with holidays as a vertical category, looks like this:

```sql
SELECT id AS "key", name AS "label", NULL AS "group", orientation, backgroundcolor AS "color", textcolor AS "textColor", description
FROM wing_activity_category
ORDER BY ordering, name
```

## One schedule for every tenant

When each unit gets its own copy of the schedule page under its own part of the site, as `/en/4wing/activity-schedule/` and `/en/units/10ftts/activity-schedule/`, the data sources need no copy. The application decides what a tenant is, in one registered `IBindVariableProvider` that reads the page from the execution context, and a SELECT binds the answer as `:APP.tenant`. A bars source filtered with `WHERE tenant_id = :APP.tenant AND enddate >= :PARAM.from AND startdate <= :PARAM.to` serves every copy, and a new tenant is a page copy and nothing else. The [worked example](activity-schedule-example.md) builds the whole application around it, provider included.

## Colours and styling

Bars without a colour of their own take their kind's colour, `--ah-schedule-kind-1` to `--ah-schedule-kind-8`, which a site redefines on `:root` or on `.application-blocks` as it does the other `--app-` tokens. A marker band's lane height is `--ah-schedule-marker-height`, 7rem by default. The runtime stylesheet shades a marked column with 16% of the marker's colour over the surface.

Every bar renders `data-key` and `data-kind`, and a vertical row's bars carry `app-schedule-bar--vertical`, so a JavaScript block on the page can attach any behaviour the block itself does not offer.

## Access

The sources are read through the data source engine, so each source's Access setting, read groups, row restriction and policies decide what each reader sees. On a page without Public Access an anonymous visitor can see a schedule when both sources are Public and neither query nor row restriction uses `:USER_*` binds. Otherwise they see a sign-in notice, as the [site integration guide](site-integration.md) describes. A source whose data cannot be read shows a short message in place of the table and never fails the page.
