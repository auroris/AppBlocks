import { AppEditor } from "./editor-base.js";
import { html } from "@umbraco-cms/backoffice/external/lit";

export default class AntiSpamServicePicker extends AppEditor {
    render() {
        const services = this.catalogue?.antiSpamServices ?? [];
        const selected = services.find(service => service.alias === this.value);
        return html`${this.renderChoices(services.map(service => ({ value: service.alias, label: service.name })), "Anti-spam service")}
            ${selected && !selected.ready ? html`<p class="error" role="alert">This service has no secret key yet, so the form will refuse every submission. An administrator adds it in Settings, AppBlocks, Anti-spam.</p>` : ""}
            ${selected?.external ? html`<p>This service loads a third-party script in the visitor's browser and may need cookie consent on some sites. Consent handling stays with the site.</p>` : ""}`;
    }
}
customElements.define("appblocks-anti-spam-picker", AntiSpamServicePicker);
