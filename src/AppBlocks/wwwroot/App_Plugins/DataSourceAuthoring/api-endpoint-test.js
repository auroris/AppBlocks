import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import { html, css } from "@umbraco-cms/backoffice/external/lit";
import { UMB_DOCUMENT_WORKSPACE_CONTEXT } from "@umbraco-cms/backoffice/document";
import { UMB_PARENT_ENTITY_CONTEXT } from "@umbraco-cms/backoffice/entity";
import { managementRequest } from "../AppBlocks/api.js";
import { routeParameters } from "./api-route.js";

const queryParameters = text => (text ?? "").split(/[\s,]+/).map(name => name.replace(/^:PARAM\./i, "")).filter(name => /^[A-Za-z_][A-Za-z0-9_]*$/.test(name));

/** Calls the endpoint being edited as the signed-in User, with sample parameter values, and shows its JSON. Nothing is saved. */
export default class ApiEndpointTest extends UmbLitElement {
    static properties = { busy: { state: true }, error: { state: true }, result: { state: true }, values: { state: true } };
    static styles = css`
        :host { display:block; } label { display:block; margin:8px 0 4px; font-weight:600; }
        input { box-sizing:border-box; width:100%; max-width:420px; padding:8px; font:13px monospace; color:inherit; background:var(--uui-color-surface); border:1px solid var(--uui-color-border); border-radius:3px; }
        button { margin:12px 0; padding:8px 16px; cursor:pointer; } .error { color:var(--uui-color-danger); } p { color:var(--uui-color-text-alt); }
        pre { white-space:pre-wrap; overflow-wrap:anywhere; max-height:420px; overflow:auto; padding:10px; background:var(--uui-color-surface-alt); border:1px solid var(--uui-color-border); }
    `;
    constructor() {
        super(); this.samples = {}; this.values = {};
        this.consumeContext(UMB_DOCUMENT_WORKSPACE_CONTEXT, workspace => {
            this.workspace = workspace;
            if (workspace?.data) this.observe(workspace.data, data => this.values = Object.fromEntries((data?.values ?? []).map(property => [property.alias, property.value])));
        });
        this.consumeContext(UMB_PARENT_ENTITY_CONTEXT, context => this.parent = context);
    }
    sample(kind, name) { return this.samples[`${kind}:${name}`] ?? ""; }
    async test() {
        this.busy = true; this.error = undefined; this.result = undefined;
        try {
            const data = this.workspace?.getData();
            if (!data) throw new Error("The endpoint is still loading.");
            const values = Object.fromEntries((data.values ?? []).map(property => [property.alias, property.value]));
            const pick = (kind, names) => Object.fromEntries(names.map(name => [name, this.sample(kind, name)]).filter(([, value]) => value !== ""));
            this.result = await managementRequest("api-endpoints/test", {
                method: "POST", body: {
                    key: data.unique, parentKey: this.parent?.getParent()?.unique, values,
                    route: pick("route", routeParameters(values.route)), query: pick("query", queryParameters(values.queryParameters)),
                },
            });
        } catch (error) { this.error = error.message; }
        finally { this.busy = false; }
    }
    input(kind, name) {
        const id = `${kind}-${name}`;
        return html`<label for=${id}>${kind === "route" ? `:${name}` : `?${name}=`}</label>
            <input id=${id} spellcheck="false" .value=${this.sample(kind, name)} @input=${event => { this.samples = { ...this.samples, [`${kind}:${name}`]: event.target.value }; }}>`;
    }
    render() {
        const route = routeParameters(this.values.route);
        const query = queryParameters(this.values.queryParameters);
        return html`
            ${route.length || query.length ? html`<p>Sample values. Every route parameter needs one; query parameters may stay empty.</p>` : ""}
            ${route.map(name => this.input("route", name))}
            ${query.map(name => this.input("query", name))}
            <button type="button" ?disabled=${this.busy} @click=${this.test}>${this.busy ? "Testing…" : "Test endpoint"}</button>
            <p>Tests your current edits as you, signed in to the backoffice, and shows the JSON the endpoint would answer. Nothing is saved or published.</p>
            <div aria-live="polite">
                ${this.error ? html`<p class="error" role="alert">${this.error}</p>` : ""}
                ${this.result?.errors?.map(error => html`<p class="error" role="alert">${error}</p>`)}
                ${this.result?.status ? html`<p>GET <code>${this.result.path}</code> answered <strong data-testid="api-status">${this.result.status}</strong></p>
                    <pre data-testid="api-json">${JSON.stringify(this.result.body, null, 2)}</pre>` : ""}
            </div>`;
    }
}
customElements.define("appblocks-api-endpoint-test", ApiEndpointTest);
