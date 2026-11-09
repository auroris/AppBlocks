import { BlockPreview } from "../ApplicationBlocks/block-preview.js";
import { button, heading, note, table, json } from "../ApplicationBlocks/preview-model.js";

// The columns a starting view shows: the last column selection of its active table, or of the table that one is built from
// when it selects none. A report document keeps them in each table's "select" composables.
export function startingColumns(state) {
        const tables = state?.tables ?? {};
        const seen = new Set();
        for (let name = state?.activeTable ?? Object.keys(tables)[0]; name && tables[name] && !seen.has(name); name = tables[name].from) {
                seen.add(name);
                const selection = (tables[name].composables ?? []).filter(c => c?.kind === "select" && Array.isArray(c.columns)).at(-1);
                if (selection) return selection.columns.filter(column => typeof column === "string" && column.length > 0);
        }
        return [];
}

export function renderReport(content) {
        const columns = startingColumns(json(content.initialState));
        return heading(content.heading) + `<div>${button("Search")}${button("Actions")}${content.createLabel ? button(content.createLabel) : ""}</div>`
            + table(columns, content.emptyMessage || "Report rows appear for the permitted visitor.")
            + note(`Data source: ${content.dataSource || "not selected"} · ${content.pageSize || 20} rows per page · Authoring preview`);
}
export default class ReportPreview extends BlockPreview {
    renderContent(alias, content) { return renderReport(content); }
}
customElements.define("app-report-preview", ReportPreview);
