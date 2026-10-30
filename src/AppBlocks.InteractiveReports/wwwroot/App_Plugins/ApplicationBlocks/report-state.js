import { AppEditor, pathBase } from "./editor-base.js";
import { html } from "@umbraco-cms/backoffice/external/lit";
import { UMB_PROPERTY_DATASET_CONTEXT } from "@umbraco-cms/backoffice/property";
import { request } from "../AppBlocks/api.js";

export default class ReportStateEditor extends AppEditor {
    static properties = { ...AppEditor.properties, dataSource: {state:true}, loading: {state:true} };
    constructor() {
        super();
        this.consumeContext(UMB_PROPERTY_DATASET_CONTEXT, async context => {
            this.observe(await context?.propertyValueByAlias("pageSize"), value => { this.pageSize = value; }, "report-page-size");
            this.observe(await context?.propertyValueByAlias("dataSource"), value => {
                if (this.dataSource !== value) this.shadowRoot?.querySelector(".preview")?.replaceChildren();
                this.dataSource = value;
            }, "report-data-source");
        });
    }
    async preview() {
        this.loading = true;
        const selection = this.dataSource;
        try {
            const { name } = await request(this.auth, "report-preview", { dataSource: selection });
            if (selection !== this.dataSource) return;
            await import(`${pathBase}/api/reports/ui/ir.js`);
            const element = document.createElement("interactive-report");
            if (selection !== this.dataSource) return;
            element.setAttribute("report", name);
            element.setAttribute("api-base", `${pathBase}/umbraco/reports`);
            if (this.pageSize > 0) element.initialPageSize = this.pageSize;
            element.setAttribute("controls", "search columns rename columnSettings filter sort aggregate controlBreak highlight compute groupBy pivot chart pagination");
            element.editLink = null; element.createLink = null;
            if (this.value) element.initialDocument = JSON.parse(this.value);
            this.shadowRoot.querySelector(".preview").replaceChildren(element);
            this.error = "";
        } catch (error) { this.error = error.message; }
        finally { this.loading = false; }
    }
    capture() {
        try {
            const document = this.shadowRoot.querySelector("interactive-report").getReportDocument();
            // The schema is a transient response cache, not authored content.
            for (const table of Object.values(document.tables ?? {})) delete table.schema;
            if (document.page) document.page.index = 1;
            this.change(JSON.stringify(document)); this.error = "";
        } catch { this.error = "Wait for the preview to load successfully before using its view."; }
    }
    render() {
        return html`<p>Choose columns, sorting, filters and presentation in the preview, then use that view. The configuration is saved with this block.</p>
            <p>${this.value ? "This block has a saved starting view." : "Showing all columns with the default view."}</p>
            <button type="button" ?disabled=${this.readonly || this.loading || !this.dataSource || !this.catalogue?.canPreview} @click=${this.preview}>${this.loading ? "Loading preview…" : "Configure view"}</button>
            <button type="button" ?disabled=${this.readonly || this.loading || !this.catalogue?.canPreview} @click=${this.capture}>Use this view</button>
            <button type="button" ?disabled=${this.readonly} @click=${() => this.change("")}>Reset view</button>
            ${this.error ? html`<p class="error" role="alert">${this.error}</p>` : ""}<div class="preview"></div>`;
    }
}
customElements.define("app-report-state", ReportStateEditor);
