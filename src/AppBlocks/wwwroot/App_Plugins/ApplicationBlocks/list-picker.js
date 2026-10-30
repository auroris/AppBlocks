import { AppEditor } from "./editor-base.js";
export default class ListPicker extends AppEditor {
    render() { return this.renderChoices((this.catalogue?.lists ?? []).map(list => ({ value: list.key, label: `${list.label} (${list.alias})` })), "List of values"); }
}
customElements.define("app-list-picker", ListPicker);
