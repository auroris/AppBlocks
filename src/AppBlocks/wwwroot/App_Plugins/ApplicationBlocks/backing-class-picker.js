import { AppEditor } from "./editor-base.js";
import { html } from "@umbraco-cms/backoffice/external/lit";

export default class BackingClassPicker extends AppEditor {
    render() {
        const handlers = this.catalogue?.dataSourceHandlers ?? [];
        const options = handlers.map(handler => ({ value: handler.alias, label: handler.alias }));
        return html`${this.renderChoices(options, "Backing class")}
            <p>Optional. Select a registered C# backing class to run around this data source's reads and writes, or leave it empty for none.</p>`;
    }
}
customElements.define("appblocks-backing-class-picker", BackingClassPicker);
