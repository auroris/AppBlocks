import { enableReportPrinting } from "./report-print.js";

const pathBase = new URL(import.meta.url).pathname.split("/App_Plugins/")[0];

async function mountReports() {
    const templates = [...document.querySelectorAll("template[data-app-report]")];
    if (!templates.length) return;
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
