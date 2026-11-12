import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import { html, css } from "@umbraco-cms/backoffice/external/lit";
import { UMB_AUTH_CONTEXT } from "@umbraco-cms/backoffice/auth";
import { UMB_DOCUMENT_WORKSPACE_CONTEXT } from "@umbraco-cms/backoffice/document";
import { UMB_PARENT_ENTITY_CONTEXT } from "@umbraco-cms/backoffice/entity";

import { isAdministrator, managementRequest, testNeedsAdministrator } from "../AppBlocks/api.js";
export default class DataSourceTest extends UmbLitElement {
    static properties = { busy: { state: true }, error: { state: true }, result: { state: true }, allowed: { state: true } };
    static styles = css`
        :host { display:block; } textarea { width:100%; box-sizing:border-box; min-height:100px; padding:10px; font:13px monospace; }
        button { margin:12px 0; padding:8px 16px; cursor:pointer; } .error { color:var(--uui-color-danger); } .warning { color:var(--uui-color-warning-emphasis, var(--uui-color-text)); }
        table { border-collapse:collapse; width:100%; } th,td { padding:8px; border-bottom:1px solid var(--uui-color-border); text-align:left; overflow-wrap:anywhere; }
        pre { white-space:pre-wrap; overflow-wrap:anywhere; } p { color:var(--uui-color-text-alt); }
    `;
    constructor() {
        super(); this.samples = "{}";
        this.consumeContext(UMB_AUTH_CONTEXT, context => {
            this.auth = context;
            // Testing is for AppBlocks administrators; anyone else is told so in place of the button.
            if (context) isAdministrator().then(allowed => this.allowed = allowed);
        });
        this.consumeContext(UMB_DOCUMENT_WORKSPACE_CONTEXT, context => this.workspace = context);
        this.consumeContext(UMB_PARENT_ENTITY_CONTEXT, context => this.parent = context);
    }
    async test() {
        this.busy = true; this.error = undefined; this.result = undefined;
        try {
            const samples = JSON.parse(this.samples);
            if (!samples || Array.isArray(samples) || typeof samples !== "object") throw new Error("Enter sample bind values as a JSON object.");
            const data = this.workspace?.getData();
            if (!data) throw new Error("The data source is still loading.");
            const values = Object.fromEntries((data.values ?? []).map(property => [property.alias, property.value]));
            this.result = await managementRequest("data-sources/test", {
                method: "POST", body: { key: data.unique, parentKey: this.parent?.getParent()?.unique, values, samples },
            });
        } catch (error) { this.error = error.message; }
        finally { this.busy = false; }
    }
    render() {
        if (this.allowed === false) return html`<p data-testid="test-needs-administrator">${testNeedsAdministrator}</p>`;
        return html`
            <label for="samples">Sample bind values (JSON)</label>
            <p>For example: {"PARAM.id":"1", "USER_SID":"S-1-5-21-123"}. Unspecified user values come from your account.</p>
            <textarea id="samples" .value=${this.samples} @input=${event => this.samples = event.target.value} spellcheck="false"></textarea>
            <button type="button" ?disabled=${this.busy} @click=${this.test}>${this.busy ? "Testing…" : "Test data source"}</button>
            <p>Tests your current edits and shows the columns and one matching row. Nothing is saved or published.</p>
            <div aria-live="polite">
                ${this.error ? html`<p class="error" role="alert">${this.error}</p>` : ""}
                ${this.result?.errors?.map(error => html`<p class="error" role="alert">${error}</p>`)}
                ${this.result?.warnings?.map(warning => html`<p class="warning" role="status">${warning}</p>`)}
                ${this.result?.applicationNames?.length ? html`<p>Application values the bind variable providers declare: ${this.result.applicationNames.map(name => `:APP.${name}`).join(", ")}. Give one a sample value as {"APP.${this.result.applicationNames[0]}": "…"}.</p>` : ""}
                ${this.result?.columns?.length ? html`<table>
                    <thead><tr><th>Field</th><th>Column</th><th>Type</th><th>Rules</th><th>Sample value</th></tr></thead>
                    <tbody>${this.result.columns.map(column => html`<tr>
                        <td>${column.field}</td><td>${column.column}</td><td>${column.kind}</td>
                        <td>${[column.readOnly && "Read only", column.required && "Required"].filter(Boolean).join(", ")}</td>
                        <td>${JSON.stringify(this.result.row?.[column.column] ?? null)}</td>
                    </tr>`)}</tbody></table>` : ""}
                ${this.result && !this.result.errors.length && !this.result.row ? html`<p>The definition is valid. No rows matched these sample values.</p>` : ""}
            </div>`;
    }
}
customElements.define("app-data-source-test", DataSourceTest);
