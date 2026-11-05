import { BlockPreview } from "../ApplicationBlocks/block-preview.js";
import { button, heading, note, table, json } from "../ApplicationBlocks/preview-model.js";

export function renderReport(content) {
        const state = json(content.initialState);
        const columns = Array.isArray(state?.columns) ? state.columns.map(column => typeof column === "string" ? column : column?.heading || column?.name || column?.key).filter(Boolean) : [];
        return heading(content.heading) + `<div>${button("Search")}${button("Actions")}${content.createLabel ? button(content.createLabel) : ""}</div>`
            + table(columns, content.emptyMessage || "Report rows appear for the permitted visitor.")
            + note(`Data source: ${content.dataSource || "not selected"} · ${content.pageSize || 20} rows per page · Authoring preview`);
}
export default class ReportPreview extends BlockPreview {
    renderContent(alias, content) { return renderReport(content); }
}
customElements.define("app-report-preview", ReportPreview);
