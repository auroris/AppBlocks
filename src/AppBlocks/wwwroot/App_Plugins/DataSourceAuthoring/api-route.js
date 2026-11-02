import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import { html, css } from "@umbraco-cms/backoffice/external/lit";
import { UmbChangeEvent } from "@umbraco-cms/backoffice/event";
import { UMB_AUTH_CONTEXT } from "@umbraco-cms/backoffice/auth";
import { managementRequest } from "../AppBlocks/api.js";

/** The parameters a route template names, in order: the :name segments. */
export const routeParameters = route => (route ?? "").replace(/^\//, "").split("/").filter(s => /^:[A-Za-z_][A-Za-z0-9_]*$/.test(s)).map(s => s.slice(1));

/** The API base path the site is configured with, asked once per backoffice session. */
let settings;
export const apiSettings = () => settings ??= managementRequest("api-endpoints/settings").catch(error => { settings = undefined; throw error; });

/** An API endpoint's route, with the full URL it will have below the site's API base path. */
export default class ApiRoute extends UmbLitElement {
    static properties = { value: {}, readonly: { type: Boolean }, basePath: { state: true }, error: { state: true } };
    static styles = css`
        :host { display:block; } input { box-sizing:border-box; width:100%; padding:10px; font:13px monospace; color:inherit; background:var(--uui-color-surface); border:1px solid var(--uui-color-border); border-radius:3px; }
        p { color:var(--uui-color-text-alt); overflow-wrap:anywhere; } code { font-size:13px; } .error { color:var(--uui-color-danger); }
    `;
    constructor() {
        super(); this.value = "";
        this.consumeContext(UMB_AUTH_CONTEXT, async auth => {
            if (!auth) return;
            try { this.basePath = (await apiSettings()).basePath; } catch (error) { this.error = error.message; }
        });
    }
    change(value) { if (this.readonly) return; this.value = value; this.dispatchEvent(new UmbChangeEvent()); }
    render() {
        const route = (this.value ?? "").trim().replace(/^\//, "");
        const parameters = routeParameters(route);
        return html`<input aria-label="Route" placeholder="orders/:id/lines" maxlength="400" spellcheck="false" .value=${this.value ?? ""} ?disabled=${this.readonly}
                @input=${event => this.change(event.target.value)}>
            ${this.basePath ? html`<p>Full URL: <code data-testid="api-url">${location.origin}${this.basePath}/${route}</code></p>` : ""}
            ${parameters.length ? html`<p>Binds ${parameters.map(name => `:PARAM.${name}`).join(", ")} from the path.</p>` : ""}
            ${this.error ? html`<p class="error" role="alert">${this.error}</p>` : ""}`;
    }
}
customElements.define("appblocks-api-route", ApiRoute);
