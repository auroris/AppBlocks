export const pathBase = new URL(import.meta.url).pathname.split("/App_Plugins/")[0];
export async function request(auth, path, body, text = false) {
    const token = await auth?.getLatestToken();
    const response = await fetch(`${pathBase}/umbraco/management/api/v1/appblocks/${path}`, {
        method: body === undefined ? "GET" : "POST", credentials: "same-origin",
        headers: { ...(token ? { Authorization: `Bearer ${token}` } : {}), ...(body === undefined ? {} : { "Content-Type": "application/json" }) },
        body: body === undefined ? undefined : JSON.stringify(body),
    });
    if (!response.ok) {
        if (response.status === 403) throw new Error("Only an Umbraco administrator can manage connections or inspect all site forms.");
        const problem = await response.json().catch(() => null);
        throw new Error(problem?.detail ?? `The request could not be completed (${response.status}).`);
    }
    return text ? response.text() : response.json();
}

export function download(text, name, type = "text/plain") {
    const url = URL.createObjectURL(new Blob([text], { type }));
    const link = document.createElement("a"); link.href = url; link.download = name; link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}
