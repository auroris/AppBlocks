// The host supplies this optional capability; the report widget knows nothing about Documents.
export function enableReportPrinting(report) {
    const configure = async () => {
        try {
            const options = await request(endpoint(report, "options"));
            report.printHandler = options.destinations?.length ? () => openPrintDialog(report) : null;
        } catch { report.printHandler = null; }
    };
    report.addEventListener("ir-query-complete", configure, { once: true });
}

function endpoint(report, action) {
    const url = new URL(report.apiBase, location.href);
    url.pathname = `${url.pathname.replace(/\/$/, "")}/${encodeURIComponent(report.definitionName)}/print/${action}`;
    return url;
}

async function request(url, options) {
    const response = await fetch(url, { credentials: "same-origin", ...options });
    if (!response.ok) {
        const problem = await response.json().catch(() => ({}));
        throw new Error(problem.title || problem.description || `Print request failed (${response.status}).`);
    }
    return response.headers.get("content-type")?.includes("json") ? response.json() : response;
}

function element(tag, text, attributes = {}) {
    const node = document.createElement(tag);
    if (text) node.textContent = text;
    for (const [key, value] of Object.entries(attributes)) node.setAttribute(key, value);
    return node;
}

export async function openPrintDialog(report) {
    if (document.querySelector("dialog.app-report-print")) return;
    const dialog = element("dialog", null, { class: "app-report-print", "aria-label": "Print report" });
    const heading = element("h2", "Print report");
    const settings = element("fieldset");
    settings.append(element("legend", "Print settings"));
    function select(label, choices) {
        const wrapper = element("label", label);
        const control = element("select", null, { "aria-label": label });
        for (const [value, text] of choices) control.append(element("option", text, { value }));
        wrapper.append(control); settings.append(wrapper); return control;
    }
    const destination = select("Destination", []);
    const scope = select("Rows", [["all", "All matching rows"], ["page", "Current page"]]);
    const paper = select("Paper size", [["A4", "A4"], ["Letter", "Letter"], ["Legal", "Legal"]]);
    const orientation = select("Orientation", [["portrait", "Portrait"], ["landscape", "Landscape"]]);
    const status = element("p", "Loading print destinations…", { role: "status", "aria-live": "polite" });
    const frame = element("iframe", null, { title: "Print document preview", sandbox: "" });
    const note = element("p", "Preview is a snapshot, valid for five minutes. Printer receipts confirm submission to the printer service.", { class: "app-report-print-note" });
    const footer = element("footer");
    const refresh = element("button", "Refresh preview", { type: "button" });
    const close = element("button", "Close", { type: "button" });
    const submit = element("button", "Print", { type: "button" });
    footer.append(refresh, close, submit);
    dialog.append(heading, settings, status, frame, note, footer);
    document.body.append(dialog);
    dialog.showModal();
    const documentState = report.getReportDocument();
    scope.options[1].disabled = !(documentState?.page?.size > 0);
    let token = null, csrf = null, busy = false, generation = 0, previewAbort = null;
    settings.disabled = refresh.disabled = submit.disabled = true;
    close.addEventListener("click", () => dialog.close());
    dialog.addEventListener("cancel", event => { if (busy) event.preventDefault(); });
    dialog.addEventListener("close", () => { generation++; previewAbort?.abort(); dialog.remove(); report.focus(); });
    const post = (action, body, signal) => request(endpoint(report, action), {
        method: "POST", headers: { "Content-Type": "application/json", RequestVerificationToken: csrf },
        body: JSON.stringify(body), signal,
    });
    async function preview() {
        const current = ++generation;
        previewAbort?.abort(); previewAbort = new AbortController();
        token = null; submit.disabled = true; frame.removeAttribute("srcdoc");
        status.textContent = "Preparing preview…";
        submit.textContent = destination.selectedOptions[0]?.dataset.output === "printer" ? "Print" : "Download PDF";
        try {
            const result = await post("preview", { document: documentState, destinationId: destination.value,
                scope: scope.value, pageSize: paper.value, landscape: orientation.value === "landscape" }, previewAbort.signal);
            if (current !== generation || !dialog.isConnected) return;
            token = result.token;
            // srcdoc inherits the page CSP. Carry its style nonce into the script-free preview.
            const nonce = document.querySelector('script[src$="/ApplicationBlocks/reports.js"]')?.nonce;
            const previewDocument = new DOMParser().parseFromString(result.html, "text/html");
            if (nonce) for (const style of previewDocument.querySelectorAll("style")) style.setAttribute("nonce", nonce);
            frame.srcdoc = "<!doctype html>" + previewDocument.documentElement.outerHTML;
            status.textContent = `${result.rows} of ${result.totalRows} rows ready.`;
            submit.disabled = false;
        } catch (error) {
            if (current === generation && error.name !== "AbortError") status.textContent = error.message;
        }
    }
    settings.addEventListener("change", preview);
    refresh.addEventListener("click", preview);
    submit.addEventListener("click", async () => {
        if (!token || busy) return;
        const submitted = token; token = null; busy = true;
        submit.disabled = settings.disabled = close.disabled = refresh.disabled = true;
        status.textContent = "Submitting document…";
        try {
            const result = await post("submit", { token: submitted });
            if (result instanceof Response) {
                const blob = await result.blob();
                const fallback = result.headers.get("X-AppBlocks-Html-Fallback") === "true";
                const url = URL.createObjectURL(blob);
                const link = element("a", null, { href: url, download: `report.${fallback ? "html" : "pdf"}` });
                document.body.append(link); link.click(); link.remove();
                setTimeout(() => URL.revokeObjectURL(url), 60_000);
                status.textContent = fallback ? "PDF generation was unavailable. Downloaded printable HTML instead." : "PDF downloaded.";
            } else status.textContent = `Submitted to ${result.printer}. Job ${result.jobId} · ${result.pageCount} pages.`;
        } catch (error) {
            status.textContent = destination.selectedOptions[0]?.dataset.output === "printer"
                ? `${error.message} Check the printer or queue before submitting again.` : error.message;
        } finally {
            busy = false; close.disabled = false;
            // A new explicit preview is required even after failure; never replay a printer token.
            refresh.disabled = settings.disabled = false;
        }
    });
    try {
        const options = await request(endpoint(report, "options"));
        if (!dialog.isConnected) return;
        if (!options.destinations?.length) {
            report.printHandler = null; status.textContent = "No print destinations are available."; return;
        }
        csrf = options.antiforgeryToken;
        for (const item of options.destinations) {
            const option = element("option", `${item.name} — ${item.output === "printer" ? item.printer : "PDF"}`, { value: item.id });
            option.dataset.output = item.output; destination.append(option);
        }
        settings.disabled = refresh.disabled = false;
        await preview();
    } catch (error) { status.textContent = error.message; }
}
