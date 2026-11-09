import { AppEditor } from "./editor-base.js";
import { html } from "@umbraco-cms/backoffice/external/lit";

// A form binds to a registered C# class or a published SQL data source, which may share a name, so each kind is listed
// under its own heading, classes first. An entry without a kind comes from a server that lists only classes.
const groups = { class: "Form classes", dataSource: "Data sources" };
const kindOf = resource => resource.kind ?? "class";
export default class FormClassPicker extends AppEditor {
    render() {
        const options = [...this.catalogue?.formClasses ?? []]
            .sort((a, b) => Number(kindOf(a) !== "class") - Number(kindOf(b) !== "class"))
            .map(resource => ({ value: resource.alias, label: resource.alias, group: groups[kindOf(resource)] ?? kindOf(resource) }));
        return html`${this.renderChoices(options, "Form class")}
            <p>Select a registered C# form class or a published SQL data source. Its fields, and a class's business rules, define this form.</p>`;
    }
}
customElements.define("appblocks-form-class-picker", FormClassPicker);
