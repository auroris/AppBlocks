import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import { html, css } from "@umbraco-cms/backoffice/external/lit";
import { UmbChangeEvent } from "@umbraco-cms/backoffice/event";
import { UMB_AUTH_CONTEXT } from "@umbraco-cms/backoffice/auth";
import { request } from "../AppBlocks/api.js";

/**
 * Picks backoffice user groups. The value is their aliases separated by commas, which ReportLinkRule.UserGroupAliases reads.
 * A picked group that no longer exists stays listed, marked, until it is cleared.
 */
export default class UserGroupPicker extends UmbLitElement {
    static properties = { value: {}, readonly: { type: Boolean }, groups: { state: true }, error: { state: true } };
    static styles = css`
        :host { display:block; } label { display:flex; gap:8px; align-items:center; margin:4px 0; }
        p { color:var(--uui-color-text-alt); margin:4px 0; } [role=alert] { color:var(--uui-color-danger); }
    `;
    constructor() {
        super(); this.value = ""; this.groups = [];
        this.consumeContext(UMB_AUTH_CONTEXT, async auth => {
            if (!auth) return;
            try { this.groups = await request("report-user-groups"); this.error = ""; }
            catch (error) { this.error = error.message; }
        });
    }
    get selected() { return (this.value ?? "").split(",").map(alias => alias.trim()).filter(Boolean); }
    toggle(alias, checked) {
        if (this.readonly) return;
        const rest = this.selected.filter(item => item.toLowerCase() !== alias.toLowerCase());
        this.value = (checked ? [...rest, alias] : rest).join(",");
        this.dispatchEvent(new UmbChangeEvent());
    }
    render() {
        const selected = this.selected.map(alias => alias.toLowerCase());
        const missing = this.selected.filter(alias => !this.groups.some(group => group.alias.toLowerCase() === alias.toLowerCase()));
        const options = [...this.groups.map(group => ({ alias: group.alias, label: group.name })), ...missing.map(alias => ({ alias, label: `${alias} (no longer exists)` }))];
        return html`${options.map(option => html`<label><input type="checkbox" .checked=${selected.includes(option.alias.toLowerCase())}
                ?disabled=${this.readonly} @change=${e => this.toggle(option.alias, e.target.checked)}>${option.label}</label>`)}
            ${options.length === 0 && !this.error ? html`<p>No backoffice user groups.</p>` : ""}
            ${this.error ? html`<p role="alert">${this.error}</p>` : ""}`;
    }
}
customElements.define("appblocks-user-group-picker", UserGroupPicker);
