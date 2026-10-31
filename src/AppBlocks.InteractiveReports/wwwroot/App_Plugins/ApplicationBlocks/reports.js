import { enableReportPrinting } from "./report-print.js";

const pathBase = new URL(import.meta.url).pathname.split("/App_Plugins/")[0];

// Whether a data source's rule refuses this anonymous visitor, which the page cannot tell as it can a report policy's refusal. Ask
// before building the widget: they get the notice the block shows when the page can tell, rather than the widget's error.
async function refusedAnonymously(template) {
    if (template.dataset.anonymous !== "true") return false;
    const report = template.content.querySelector("interactive-report");
    const url = new URL(report.getAttribute("api-base"), location.href);
    url.pathname += `/${encodeURIComponent(report.getAttribute("report"))}/schema`;
    url.searchParams.set("appPageQuery", location.search);
    try { return [401, 403].includes((await fetch(url)).status); }
    catch { return false; } // Not a refusal: the widget says what went wrong.
}

function signInNotice() {
    const notice = document.createElement("p");
    notice.className = "app-block-signin"; notice.setAttribute("role", "status"); notice.textContent = "Sign in to use this report.";
    return notice;
}

// Saving views is off on this site. Whoever may set it up is told so, by an answer only they get and no cache keeps.
async function showSetupNote(template) {
    if (!template.dataset.setupNote) return;
    const place = template.parentElement;
    try {
        const answer = await fetch(template.dataset.setupNote);
        if (answer.status !== 200) return;
        const link = document.createElement("a");
        link.href = (await answer.json()).setupUrl; link.textContent = "Open database setup";
        const note = document.createElement("p");
        note.className = "app-report-setup-note"; note.setAttribute("role", "status");
        note.append("Saving report views is off because its database tables aren't set up. ", link);
        place.prepend(note);
    } catch { /* Only ever a pointer for an administrator; the report works without it. */ }
}

async function mountReports() {
    const found = [...document.querySelectorAll("template[data-app-report]")];
    const refused = await Promise.all(found.map(refusedAnonymously));
    const templates = found.filter((template, index) => {
        if (refused[index]) template.replaceWith(signInNotice());
        return !refused[index];
    });
    if (!templates.length) return;
    templates.forEach(showSetupNote);
    await import(`${pathBase}/api/reports/ui/ir.js`);
    for (const template of templates) {
        const report = template.content.querySelector("interactive-report").cloneNode(true);
        for (const attribute of ["api-base", "download-base"]) {
            const base = report.getAttribute(attribute);
            if (!base) continue;
            const url = new URL(base, location.href);
            url.searchParams.set("appPageQuery", location.search);
            report.setAttribute(attribute, url.toString());
        }
        const stateKey = `app-report:${template.dataset.stateKey}`;
        try {
            const saved = sessionStorage.getItem(stateKey);
            if (saved) { report.initialDocument = JSON.parse(saved); sessionStorage.removeItem(stateKey); }
        } catch { /* Storage is optional; the published view remains usable. */ }
        const remember = () => {
            try { sessionStorage.setItem(stateKey, JSON.stringify(report.getReportDocument())); } catch { /* No persistence required. */ }
        };
        report.addEventListener("ir-edit", remember);
        report.addEventListener("ir-create", remember);
        if (template.dataset.printUrl) report.addEventListener("ir-action", event => {
            if (event.detail?.command !== "app-print") return;
            const key = Object.entries(event.detail.row ?? {}).find(([name]) => name.toLowerCase() === (template.dataset.printKeyColumn ?? "").toLowerCase())?.[1];
            if (key === null || key === undefined) return;
            const url = new URL(template.dataset.printUrl, location.origin);
            url.search = location.search;
            url.searchParams.set(template.dataset.printKeyParameter || "id", String(key));
            window.open(url, "_blank", "noopener");
        });
        // Printing renders and delivers documents for an account, so it is not offered to someone with none.
        if (template.dataset.anonymous !== "true") enableReportPrinting(report);
        template.replaceWith(report);
    }
}


mountReports();
