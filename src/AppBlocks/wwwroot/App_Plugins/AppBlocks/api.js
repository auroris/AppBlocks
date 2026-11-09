import { umbHttpClient } from "@umbraco-cms/backoffice/http-client";

import { pathBase } from "../ApplicationBlocks/data-api.js";

export { pathBase };
// The backoffice's own address, such as /intranet/umbraco: the path base and the first segment after it of the address
// the backoffice runs at, so a link to one of its sections follows the backoffice wherever the host serves it.
export const backofficePath = `${pathBase}/${location.pathname.slice(pathBase.length).split("/")[1] ?? ""}`;
// The Management API's address is left as Umbraco's own client writes it for every one of its requests: it goes to
// umbHttpClient, which adds the site's address, and Umbraco serves the API there whatever the backoffice's address.
export async function managementRequest(path, { method = "GET", body, text = false } = {}) {
    const { data, error, response } = await umbHttpClient.request({
        url: `/umbraco/management/api/v1/${path}`, method, body,
        security: [{ type: "http", scheme: "bearer" }],
        headers: body === undefined ? undefined : { "Content-Type": "application/json" },
        parseAs: text ? "text" : "json",
        throwOnError: false, responseStyle: "fields",
    });
    if (error || !response?.ok) throw new Error(error?.detail ?? error?.title ?? error?.message
        ?? (response?.status === 403 ? "You do not have permission to perform this action." : `The request could not be completed (${response?.status ?? "network error"}).`));
    return data;
}

export const request = (path, body, text = false) => managementRequest(`appblocks/${path}`, { method: body === undefined ? "GET" : "POST", body, text });

export function download(text, name, type = "text/plain") {
    const url = URL.createObjectURL(new Blob([text], { type }));
    const link = document.createElement("a"); link.href = url; link.download = name; link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}
