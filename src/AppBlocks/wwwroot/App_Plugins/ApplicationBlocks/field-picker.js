import { AppEditor } from "./editor-base.js";
import { UMB_BLOCK_MANAGER_CONTEXT, UMB_BLOCK_ENTRIES_CONTEXT, UMB_BLOCK_ELEMENT_PROPERTY_DATASET_CONTEXT } from "@umbraco-cms/backoffice/block";
import { html } from "@umbraco-cms/backoffice/external/lit";
import { enclosingFormResource } from "./form-context.js";
export default class FieldPicker extends AppEditor {
    static properties = { ...AppEditor.properties, resource: { state: true } };
    constructor() {
        super();
        this.consumeContext(UMB_BLOCK_MANAGER_CONTEXT, context => {
            this.observe(context?.contents, contents => { this.contents = contents; this.updateResource(); }, "form-content");
            this.observe(context?.layouts, layouts => { this.layouts = layouts; this.updateResource(); }, "form-layout");
        });
        this.consumeContext(UMB_BLOCK_ELEMENT_PROPERTY_DATASET_CONTEXT, context => {
            this.datasetContext = context;
            this.observe(context?.properties, () => this.updateResource(), "field-context");
            this.updateResource();
        });
        this.consumeContext(UMB_BLOCK_ENTRIES_CONTEXT, context => {
            this.entriesContext = context;
            this.updateResource();
        });
    }
    updateResource() {
        this.resource = enclosingFormResource(this.contents, this.layouts, this.datasetContext?.getUnique(),
            this.entriesContext?.getParentUnique?.());
    }
    render() {
        const resource = this.catalogue?.formClasses?.find(r => r.alias === this.resource);
        return html`${this.renderChoices((resource?.fields ?? []).map(field => ({
                value: field.key, label: `${field.label} (${field.kind}) — ${field.key}`,
            })), "Field binding")}
            ${!resource ? html`<p>Select the form class in its Form interface first.</p>` : ""}`;
    }
}
customElements.define("app-field-picker", FieldPicker);
