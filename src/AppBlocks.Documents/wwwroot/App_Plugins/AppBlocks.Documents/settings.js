import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import { html, css } from "@umbraco-cms/backoffice/external/lit";
import { UMB_AUTH_CONTEXT } from "@umbraco-cms/backoffice/auth";

const pathBase = new URL(import.meta.url).pathname.split("/App_Plugins/")[0];

export default class DocumentsSettings extends UmbLitElement {
    static properties = { settings: { state: true }, error: { state: true }, message: { state: true }, busy: { state: true } };
    static styles = css`
        :host { display:block; } section { border:1px solid var(--uui-color-border); border-radius:10px; padding:24px; background:var(--uui-color-surface); }
        h2 { margin:0 0 12px; } p { line-height:1.6; color:var(--uui-color-text-alt); }
        form { max-width:680px; } label { display:block; font-weight:600; margin:20px 0 7px; }
        input:not([type=checkbox]) { box-sizing:border-box; width:100%; padding:10px; font:inherit; color:inherit; background:var(--uui-color-surface); border:1px solid var(--uui-color-border); border-radius:6px; }
        .hint { font-size:13px; margin-top:7px; } button { margin-top:22px; padding:10px 18px; color:var(--uui-color-selected-contrast); background:var(--uui-color-selected); border:0; border-radius:6px; font:inherit; cursor:pointer; }
        button:disabled { opacity:.6; cursor:wait; } :focus-visible { outline:3px solid var(--uui-color-focus); outline-offset:3px; }
        .error { color:var(--uui-color-danger); } .message { color:var(--uui-color-positive); }
    `;
    constructor() {
        super();
        this.consumeContext(UMB_AUTH_CONTEXT, auth => { if (auth) { this.auth = auth; this.load(); } });
    }
    async request(body) {
        const token = await this.auth.getLatestToken();
        const response = await fetch(`${pathBase}/umbraco/management/api/v1/appblocks/documents/settings`, {
            method: body === undefined ? "GET" : "POST", credentials: "same-origin",
            headers: { Authorization: `Bearer ${token}`, ...(body === undefined ? {} : { "Content-Type": "application/json" }) },
            body: body === undefined ? undefined : JSON.stringify(body),
        });
        if (!response.ok) {
            if (response.status === 403) throw new Error("Only an Umbraco administrator can manage document settings.");
            const problem = await response.json().catch(() => null);
            throw new Error(problem?.detail ?? `Document settings could not be ${body === undefined ? "loaded" : "saved"} (${response.status}).`);
        }
        return response.json();
    }
    async load() {
        try { this.settings = await this.request(); this.error = undefined; }
        catch (error) { this.error = error.message; }
    }
    async save(event) {
        event.preventDefault();
        const form = event.currentTarget;
        if (!form.reportValidity()) return;
        const values = new FormData(form);
        this.busy = true; this.message = undefined; this.error = undefined;
        try {
            this.settings = await this.request({ pressroomUrl: values.get("url"), apiKey: values.get("key"),
                clearApiKey: values.has("clearKey"), timeoutSeconds: Number(values.get("timeout")) });
            form.querySelector("[name=key]").value = "";
            form.querySelector("[name=clearKey]").checked = false;
            this.message = this.settings.pressroomUrl ? "Saved. Documents will use Pressroom for PDF conversion." : "Saved. Documents will open as HTML in a new window.";
        } catch (error) { this.error = error.message; }
        finally { this.busy = false; }
    }
    render() {
        return html`<section><h2>Documents</h2><p>Documents open in a new window. Leave the Pressroom URL empty to open a printable HTML document. Configure Pressroom to generate PDF instead.</p>
            <div aria-live="polite">${this.error ? html`<p class="error" role="alert">${this.error}</p>` : ""}${this.message ? html`<p class="message">${this.message}</p>` : ""}</div>
            ${this.settings ? html`<form @submit=${this.save}>
                <label for="pressroom-url">Pressroom URL (optional)</label><input id="pressroom-url" name="url" type="url" placeholder="https://pressroom.example/" .value=${this.settings.pressroomUrl ?? ""}>
                <p class="hint">If PDF conversion fails, the document opens as HTML and the error is recorded in Umbraco’s log.</p>
                <label for="pressroom-key">Pressroom API key</label><input id="pressroom-key" name="key" type="password" autocomplete="new-password" placeholder=${this.settings.hasApiKey ? "Leave blank to keep the existing key" : "Enter your Pressroom API key"}>
                <p class="hint">Pressroom requires a key by default. A key may be omitted only when the service allows anonymous PDF rendering.</p>
                <label><input name="clearKey" type="checkbox"> Remove the existing API key</label>
                <label for="pressroom-timeout">Timeout in seconds</label><input id="pressroom-timeout" name="timeout" type="number" min="0.001" max="2147483" step="any" required .value=${String(this.settings.timeoutSeconds)}>
                <p class="hint">Changes apply immediately to this installation. The API key is stored encrypted and is excluded from content exports.</p>
                <button type="submit" ?disabled=${this.busy}>${this.busy ? "Saving…" : "Save settings"}</button>
            </form>` : this.error ? "" : html`<p role="status">Loading document settings…</p>`}
        </section>`;
    }
}
customElements.define("appblocks-documents-settings", DocumentsSettings);
