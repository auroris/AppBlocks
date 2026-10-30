import { AppEditor } from "./editor-base.js";
import { html } from "@umbraco-cms/backoffice/external/lit";

export default class FormClassPicker extends AppEditor {
    render() {
        const classes = this.catalogue?.formClasses ?? [];
        const options = classes.map(resource => ({ value: resource.alias, label: resource.alias }));
        return html`${this.renderChoices(options, "Form class")}
            <p>Select a registered C# form resource. Its fields and business rules define this form.</p>`;
    }
}
customElements.define("appblocks-form-class-picker", FormClassPicker);
