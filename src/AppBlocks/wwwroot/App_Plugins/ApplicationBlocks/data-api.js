// Requests to AppBlocks' endpoints from a public page: the API endpoints and the blocks' own forms, attachments and
// tabular endpoints. Uppy sends file uploads directly with the same antiforgery header; other runtime requests use this
// module. A site's own script may import it to read an API
// endpoint's rows or to send a signed-in Member's changes. Nothing here uses Umbraco's backoffice client, so it works for
// an anonymous visitor as for anyone. The backoffice modules import pathBase from here too, so it is worked out once.

// The site's path base: where App_Plugins is served from, so a site under /intranet asks /intranet/api/data/...
export const pathBase = new URL(import.meta.url).pathname.split("/App_Plugins/")[0];

// Where the blocks' endpoints live, path base included, such as /intranet/api: the page's appblocks-api-root meta tag,
// which the server writes when a site moves them, or /api under the path base. Since it already holds the path base,
// it goes to request as a URL, as antiforgeryToken below does, and not as a path from the site's root.
export const apiRoot = (globalThis.document?.querySelector('meta[name="appblocks-api-root"]')?.content.trim() || `${pathBase}/api`).replace(/\/+$/, "");

// A refused request: the HTTP status and the endpoint's body, a problem such as { status, title, detail } or a form's
// { errors }, or {} when the answer had no JSON body.
export class ApiError extends Error {
    constructor(status, problem) {
        super(problem.detail || problem.title || `The request failed with status ${status}.`);
        this.name = "ApiError";
        this.status = status;
        this.problem = problem;
    }
}

// A path starting with one "/" is from the site's root and takes the path base; a URL object or an absolute address
// is used as it is.
export function siteUrl(url) {
    return typeof url === "string" && url.startsWith("/") && !url.startsWith("//") ? new URL(pathBase + url, location.origin) : new URL(url, location.href);
}

// The address with the query's values set on it. A null or undefined value is left out, and an array repeats its name.
export function withQuery(url, query = {}) {
    const address = siteUrl(url);
    for (const [name, value] of Object.entries(query)) {
        if (value === null || value === undefined) continue;
        address.searchParams.delete(name);
        for (const item of Array.isArray(value) ? value : [value]) address.searchParams.append(name, String(item));
    }
    return address;
}

// Sends one request with the visitor's cookies and nothing cached, and resolves with the answer's JSON, or null for an
// answer without a body such as 204. A body that is FormData, URLSearchParams, a Blob or a string goes as it is; any
// other body goes as JSON. A refusal rejects with an ApiError; a failed connection or an abort rejects as fetch does.
export async function request(url, { method = "GET", body, headers, signal } = {}) {
    const raw = body === undefined || body instanceof FormData || body instanceof URLSearchParams || body instanceof Blob || typeof body === "string";
    const response = await fetch(siteUrl(url), {
        method, signal, credentials: "same-origin", cache: "no-store",
        headers: { Accept: "application/json", ...(raw ? {} : { "Content-Type": "application/json" }), ...headers },
        body: raw ? body : JSON.stringify(body),
    });
    const text = await response.text();
    let json = null;
    if (text) { try { json = JSON.parse(text); } catch { if (response.ok) throw new SyntaxError("The answer was not JSON."); } }
    if (!response.ok) throw new ApiError(response.status, json !== null && typeof json === "object" ? json : {});
    return json;
}

// What to tell a reader about a failed request: the endpoint's own words, its detail, title or first field error, or
// the fallback when it gave none or the request never got an answer.
export function errorText(error, fallback = "The request could not be completed.") {
    const problem = error instanceof ApiError ? error.problem : {};
    const errors = problem.errors !== null && typeof problem.errors === "object" ? Object.values(problem.errors).flat() : [];
    return problem.detail || problem.title || errors.find(entry => typeof entry === "string" && entry) || fallback;
}

let antiforgery = null;
// The signed-in Member's antiforgery token, { token, headerName }, asked of the antiforgery endpoint once per page and
// kept, since it stays valid for the Member's session. The endpoint answers 401 to anyone else; a refusal is not kept,
// so asking again after signing in works.
export function antiforgeryToken() {
    antiforgery ??= request(new URL(`${apiRoot}/antiforgery/token`, location.origin)).catch(error => { antiforgery = null; throw error; });
    return antiforgery;
}

// request, for a signed-in Member's changes: anything but GET and HEAD carries the antiforgery token in its header, as
// a controller under the Member policy requires.
export async function memberRequest(url, options = {}) {
    const method = (options.method ?? "GET").toUpperCase();
    if (method === "GET" || method === "HEAD") return request(url, options);
    const { token, headerName } = await antiforgeryToken();
    return request(url, { ...options, headers: { ...options.headers, [headerName]: token } });
}
