import { AppEditor } from "./editor-base.js";
export default class ReportPicker extends AppEditor {
    render() { return this.renderChoices((this.catalogue?.reports ?? []).map(r => ({ value:r.alias, label:`${r.label} (${r.alias})` })), "Report source"); }
}
customElements.define("app-report-picker", ReportPicker);
