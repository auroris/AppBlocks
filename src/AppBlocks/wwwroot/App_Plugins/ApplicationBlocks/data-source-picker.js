import { AppEditor } from "./editor-base.js";
export default class DataSourcePicker extends AppEditor {
    render() { return this.renderChoices((this.catalogue?.resources ?? []).filter(source => source.dataSource).map(source => ({ value: source.dataSource.key, label: `${source.dataSource.name} (${source.alias})` })), "SQL data source"); }
}
customElements.define("app-data-source-picker", DataSourcePicker);
