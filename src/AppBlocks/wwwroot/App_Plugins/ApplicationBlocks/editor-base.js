import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import { html, css } from "@umbraco-cms/backoffice/external/lit";
import { UmbChangeEvent } from "@umbraco-cms/backoffice/event";
import { UMB_AUTH_CONTEXT } from "@umbraco-cms/backoffice/auth";
import { AppBlocksCatalogueRepository } from "./catalogue.repository.js";

export const pathBase = new URL(import.meta.url).pathname.split("/App_Plugins/")[0];
export class AppEditor extends UmbLitElement {
    static properties = { value: {}, readonly: { type: Boolean }, catalogue: { state: true }, error: { state: true } };
    static styles = css`
        :host { display:block; } select, textarea { width:100%; box-sizing:border-box; padding:10px; color:inherit; background:var(--uui-color-surface); border:1px solid var(--uui-color-border); border-radius:3px; }
        textarea { font:13px monospace; min-height:140px; } p { color:var(--uui-color-text-alt); } .error { color:var(--uui-color-danger); } button { margin:8px 8px 8px 0; padding:8px 12px; cursor:pointer; }
    `;
    constructor() {
        super(); this.value = ""; this.readonly = false;
        this.repository = new AppBlocksCatalogueRepository(this);
        this.consumeContext(UMB_AUTH_CONTEXT, async auth => {
            if (!auth) return;
            this.auth = auth;
            try {
                this.catalogue = await this.repository.requestCatalogue(auth);
            } catch (error) { this.error = error.message; }
        });
    }
    change(value) { if (this.readonly) return; this.value = value; this.dispatchEvent(new UmbChangeEvent()); }
    renderChoices(options, label) {
        return html`<select aria-label=${label} .value=${this.value ?? ""} ?disabled=${this.readonly} @change=${e => this.change(e.target.value)}>
            <option value="">Select ${label.toLowerCase()}</option>
            ${this.value && !options.some(o => o.value === this.value) ? html`<option value=${this.value}>${this.value} (unavailable)</option>` : ""}
            ${options.map(o => html`<option value=${o.value} ?selected=${o.value === this.value}>${o.label}</option>`)}</select>
            ${this.error ? html`<p class="error" role="alert">${this.error}</p>` : ""}`;
    }
}
