# API endpoints

An **API endpoint** serves a SQL data source's rows as JSON over GET, at a route the editor defines. The route is a URL template whose parameters bind into the query. Endpoints read; writes stay with forms, tabular forms and their data sources.

## Creating one

Create an **API endpoint** and publish it. Where it sits in the content tree is up to you: under a **Data sources** root beside the sources it reads, or under an **API endpoints** root at the top of the tree that keeps them together; a site can also allow the type under document types of its own. AppBlocks serves every published endpoint whose ancestors are published, wherever it is, and its position never changes its URL, which comes from its **Route**. Its properties:

| Property | What it does |
| --- | --- |
| **Alias** | The endpoint's name in logs and diagnostics, such as `orders.lines`. Unique among published endpoints. |
| **Route** | The path below the base path, such as `orders/:id/lines`. The editor shows the full URL it will have. |
| **SQL data source** | A published SQL data source to read, whose access, row restriction, policies and backing class apply; a backing class can answer with rows of its own, as [Answering from code](#answering-from-code) describes. Leave it empty to give the endpoint SQL of its own. |
| **Connection** and **SQL** | Only without a data source: the database, and a table name or SELECT, with the same rules and bind variables as a data source's row source. |
| **Query parameters** | The `:PARAM.` names the query string may fill, separated by commas. |
| **Shape** | **Collection**, the default, or **Single**. |
| **Order by** | Optional columns to sort by, separated by commas, each optionally followed by `DESC`. |
| **Maximum rows** | The most rows a collection returns; 1000 by default. |
| **Access**, **Read groups**, **Row restriction** | Only with the endpoint's own SQL, on the Access tab, with the same choices and checks as a SQL data source. |

**Test** calls the endpoint as you, signed in to the backoffice, with sample values for its route and query parameters, and shows the status and JSON it answers. It uses your current edits; nothing is saved or published.

Publishing checks the endpoint as a request would read it, against its source's schema: the route must be well formed and answer no request another published endpoint answers, Order by must name columns of the source, and every `:PARAM.` the SQL uses must be a route parameter or listed under **Query parameters**, so a typo is refused rather than bound as NULL. With a data source selected, the endpoint's own connection, SQL, access, read groups and row restriction must stay empty.

## Routes and parameters

A route has up to eight segments separated by `/`, or as many as `AppBlocks:ApiEndpoints:MaxRouteSegments` allows. Each is a literal, of letters, digits, `-`, `.`, `_` and `~`, or a parameter, a colon and a name: `:id`. There are no empty segments, no `.` or `..`, and no query string. Literals match ignoring case. Two routes that would match the same requests, such as `orders/:id` and `orders/:key`, cannot both be published.

When several routes match a request, the one with more literal segments wins, and then the one whose first literal comes earlier: `orders/latest` answers before `orders/:id`, and `orders/:id` before `:kind/latest`.

A `:name` segment binds as `:PARAM.name` in the SQL. The query string fills any other `:PARAM.` listed under **Query parameters**, and a route segment wins over a query parameter of the same name. Nothing is read from request headers. `:USER_*`, `:NOW`, `:NOW_UTC` and `:APP.` values work as they do everywhere else. A segment value that is empty after decoding is a 404.

```sql
SELECT LINE_ID, ITEM, QUANTITY, UNIT_PRICE
FROM ORDER_LINES
WHERE ORDER_ID = :PARAM.id AND (:PARAM.item IS NULL OR ITEM = :PARAM.item)
```

With the route `orders/:id/lines` and `item` under Query parameters, `GET /api/data/orders/4/lines?item=Bearing` binds `id` from the path and `item` from the query string.

## Access without a page

An endpoint has no page, so Umbraco Public Access does not apply to it. The read goes through the same path as lists of values and paper reports, and the source decides:

- **Public**: anyone with the URL, including a visitor without an account, unless the SQL or row restriction uses a `:USER_*` value, which needs a signed-in account.
- **Not public**: a signed-in Member or backoffice User.
- **Specific groups**: Members of the read or write groups.
- The row restriction, every registered `IDataSourcePolicy` and the backing class's `QueryAsync` apply. A policy sees `DataSourceAction.Query`, and a context whose page and form keys are `Guid.Empty`, with the bound parameters.

The caller is the signed-in Member and backoffice User, as for the blocks' endpoints; a cookie for a disabled or locked-out account stays refused. A refused request is 401 for a caller without an account and 403 for a signed-in one. Public on an endpoint means public to the internet: choose it only for data you would put on a public page.

## Answering from code

An endpoint can answer with rows that C# decides, so an application whose rules live in code keeps one copy of them. Select a published SQL data source whose **Backing class** implements `ReadAsync` of `IDataSourceHandler`. The data source's row source still gives the endpoint its columns, field aliases and kinds, and its access, every policy and the class's `QueryAsync` still decide who may read. The rows come from the class:

```csharp
public sealed class NoticeRules(NoticeQueries notices) : IDataSourceHandler
{
    public string Alias => "notices.rules";

    // Keyed by the row source's column names, in any letter case.
    public async ValueTask<IReadOnlyList<IReadOnlyDictionary<string, object?>>?> ReadAsync(DataSourceOperation operation, CancellationToken ct)
        => (await notices.CurrentAsync(operation.User, operation.Context.Parameters, ct))
            .Select(notice => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> {
                ["NOTICE_ID"] = notice.Id, ["TITLE"] = notice.Title, ["POSTED_AT"] = notice.PostedAt })
            .ToArray();
}
```

AppBlocks shapes the rows as it shapes the row source's: a column the class leaves out is `null`, a key that is not a column of the row source is dropped, **Order by** sorts them, **Maximum rows** holds a collection to its limit, and **Single** answers with the first. Returning `null` reads the row source as usual, so a class can answer some requests and leave others to SQL. `operation.Context.Parameters` holds the route and query parameters, and `operation.Sql` runs the class's own SQL on the endpoint's connection with the same bind variables.

The row restriction is SQL, so it cannot filter rows from code; the class applies the same rule itself. Lists of values, schedules and paper reports read through the same path and get the class's rows too. An Interactive Report runs the row source's SQL itself and does not ask the class.

## Responses

Every answer is `application/json; charset=utf-8` with `Cache-Control: no-store`.

- **Collection**: `{ "items": [ { ... }, ... ] }`, one object per row, keyed by field alias: the data source's mapped alias, or the column's name as a field (`ORDER_DATE` becomes `orderDate`). Numbers and booleans are JSON numbers and booleans, dates and times ISO text, and NULL is `null`.
- **Single**: the first row as one object, in Order by's order when it is set, and 404 when there is none. A second row is no error.
- **Errors**: `{ "status": 404, "title": "Not Found" }` and the like, with no SQL, column names or connection details; those go to the log.

There is no paging. A collection returns every row up to **Maximum rows**, and a larger result is a 500 logged with its reason, never a silently shorter list: narrow the query or raise the limit. An unknown route is 404, and any method but GET is 405 with `Allow: GET`.

## Base path

Endpoints answer below `/api/data` unless `AppBlocks:ApiBasePath` says otherwise:

```json
{ "AppBlocks": { "ApiBasePath": "/api/v1/data" } }
```

The catalogue of endpoints is read from Umbraco's published content on each request, so publishing, unpublishing or deleting an endpoint takes effect on the next request.

## Calling endpoints from a page

`App_Plugins/ApplicationBlocks/data-api.js` is a small ES module for a site's own pages, public or not, that read endpoints or send a signed-in Member's changes. It is the transport the blocks' runtime uses for its own requests, so it needs no backoffice session and imports nothing else.

| Export | What it does |
| --- | --- |
| `request(url, { method, body, headers, signal })` | Sends one request with the visitor's cookies (`credentials: "same-origin"`) and `cache: "no-store"`, and resolves with the answer's JSON, or `null` for an answer without a body such as 204. A body that is `FormData`, `URLSearchParams`, a `Blob` or a string is sent as it is; any other body is sent as JSON. `method` is GET by default. |
| `ApiError` | What `request` rejects with when the endpoint refuses: `status` is the HTTP status, and `problem` the endpoint's JSON body, such as `{ status, title }`, or `{}` when it had none. A request that gets no answer rejects as `fetch` does, with a `TypeError`, and an aborted one with an `AbortError`. |
| `errorText(error, fallback)` | The text to show a reader: the problem's `detail`, `title` or first field error, or the fallback, `The request could not be completed.` unless given, when there is none or the request got no answer. |
| `withQuery(url, query)` | The address with each of the query's values set, leaving out `null` and `undefined` and repeating the name for an array. |
| `siteUrl(url)`, `pathBase` | A path starting with `/` is from the site's root and takes the site's path base, read from where the module is served, so a site at `/intranet` asks `/intranet/api/data/...`. A `URL` object or an absolute address is used as it is. |
| `apiRoot` | Where the blocks' own endpoints answer, path base included, such as `/intranet/api`: the page's `appblocks-api-root` meta tag, which follows `AppBlocks:ApiRoot`, or `/api` under the path base. It already holds the path base, so pass `new URL(apiRoot + "/...", location.origin)` to `request`. |
| `antiforgeryToken()` | The signed-in Member's antiforgery token, `{ token, headerName }`, from the token endpoint below `apiRoot`, fetched once per page. A refusal, such as 401 for a visitor without a Member session, is not kept, so asking again after signing in works. |
| `memberRequest(url, options)` | `request` with the antiforgery token in its header for every method but GET and HEAD, for a controller under the `AppBlocks.Member` policy; [Adding AppBlocks to a site](site-integration.md#controllers-for-signed-in-members) has an example. |

With an endpoint whose route is `orders` and `status` under Query parameters, a page lists the open orders like this:

```html
<ul id="orders" aria-live="polite"></ul>
<script type="module">
    import { request, withQuery, errorText } from "/App_Plugins/ApplicationBlocks/data-api.js";
    const list = document.getElementById("orders");
    try {
        const { items } = await request(withQuery("/api/data/orders", { status: "open" }));
        list.replaceChildren(...items.map(order => Object.assign(document.createElement("li"), { textContent: `${order.orderId}: ${order.customer}` })));
    } catch (error) {
        list.replaceChildren(Object.assign(document.createElement("li"), { textContent: errorText(error, "The orders could not be loaded.") }));
    }
</script>
```

On a site with a path base the script's own `src` and `import` carry it, as every other address on the page does; the addresses passed to `request` do not. When `AppBlocks:ApiBasePath` is set, use that base path in place of `/api/data`. A refusal of a visitor without an account is a 401, which a page can answer with a sign-in link.

## Caching and CORS

AppBlocks sends `no-store` and adds no CORS policy. Caching the answers, or calling them from a page on another origin, is the site's decision: a site that does configures ASP.NET Core's output caching or CORS middleware for the base path, keeping in mind that the answer depends on who asks.
