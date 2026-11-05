// Requests to AppBlocks' endpoints from a public page: /api/data and the blocks' own /api/forms, /api/attachments and
// /api/tabular. The runtime sends every request through here, and a site's own script may import it to read an API
// endpoint's rows. Nothing here uses Umbraco's backoffice client, so it works for an anonymous visitor as for anyone.

// The site's path base: where App_Plugins is served from, so a site under /intranet asks /intranet/api/data/...
export const pathBase = new URL(import.meta.url).pathname.split("/App_Plugins/")[0];

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
