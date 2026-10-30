import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import { html, css } from "@umbraco-cms/backoffice/external/lit";
import { UmbChangeEvent } from "@umbraco-cms/backoffice/event";
import { UMB_AUTH_CONTEXT } from "@umbraco-cms/backoffice/auth";
import { request } from "./api.js";

export default class ConnectionPicker extends UmbLitElement {
    static properties = { value: {}, readonly: { type:Boolean }, items: {state:true}, error: {state:true} };
    static styles = css`select { width:100%; padding:10px; border:1px solid var(--uui-color-border); border-radius:4px; background:var(--uui-color-surface); color:inherit; } p { color:var(--uui-color-text-alt); }`;
    constructor() { super(); this.items = []; this.consumeContext(UMB_AUTH_CONTEXT, async auth => { if (!auth) return; try { this.items = await request(auth, "connections"); } catch(error) { this.error=error.message; } }); }
    render() { const selected = this.value || "UmbracoDb"; return html`<select aria-label="Database connection" .value=${selected} ?disabled=${this.readonly} @change=${event => { this.value=event.target.value; this.dispatchEvent(new UmbChangeEvent()); }}>${this.items.map(item => html`<option value=${item.name} ?selected=${selected===item.name}>${item.label}${item.isDefault ? " (default)" : ""}</option>`)}</select><p>${this.error || "Uses Umbraco’s database unless you choose another connection. Manage connections in Settings → AppBlocks."}</p>`; }
}
customElements.define("appblocks-connection-picker", ConnectionPicker);
