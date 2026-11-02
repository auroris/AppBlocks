import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import { html, css } from "@umbraco-cms/backoffice/external/lit";
import { UMB_AUTH_CONTEXT } from "@umbraco-cms/backoffice/auth";

import { managementRequest } from "../AppBlocks/api.js";

export default class DocumentsSettings extends UmbLitElement {
    static properties = {
        settings: { state: true }, editing: { state: true }, output: { state: true }, printers: { state: true },
        error: { state: true }, message: { state: true }, busy: { state: true }, finding: { state: true },
        printerNotice: { state: true }, removing: { state: true },
    };
    static styles = css`
        :host { display:block; } section { border:1px solid var(--uui-color-border); border-radius:10px; padding:24px; background:var(--uui-color-surface); }
        h2 { margin:0 0 12px; } h3 { margin:0 0 18px; } p { line-height:1.6; color:var(--uui-color-text-alt); }
        form { max-width:680px; margin-top:24px; border-top:1px solid var(--uui-color-border); padding-top:24px; }
        fieldset { border:0; padding:0; margin:0; min-width:0; } label { display:block; font-weight:600; margin:18px 0 7px; }
        input:not([type=checkbox]), select { box-sizing:border-box; width:100%; padding:10px; font:inherit; color:inherit; background:var(--uui-color-surface); border:1px solid var(--uui-color-border); border-radius:6px; }
        .hint, small { font-size:13px; color:var(--uui-color-text-alt); } small { display:block; margin-top:5px; }
        .actions { display:flex; flex-wrap:wrap; align-items:center; gap:10px; margin:18px 0; }
        button { padding:9px 15px; color:inherit; background:var(--uui-color-surface); border:1px solid var(--uui-color-border); border-radius:6px; font:inherit; cursor:pointer; }
        .primary { color:var(--uui-color-selected-contrast); background:var(--uui-color-selected); border-color:transparent; }
        .danger { color:var(--uui-color-danger); } button:disabled { opacity:.6; cursor:wait; } :focus-visible { outline:3px solid var(--uui-color-focus); outline-offset:3px; }
        .error { color:var(--uui-color-danger); } .message { color:var(--uui-color-positive); } .scroll { overflow-x:auto; }
        table { width:100%; border-collapse:collapse; text-align:left; } th, td { border-bottom:1px solid var(--uui-color-border); padding:14px 10px; vertical-align:top; }
        th { font-size:13px; } td .actions { margin:0; } .confirm { border-left:3px solid var(--uui-color-danger); padding:12px 18px; margin-top:18px; }
    `;
    constructor() {
        super(); this.printers = []; this.output = "pdf"; this.lookupVersion = 0;
        this.consumeContext(UMB_AUTH_CONTEXT, auth => { if (auth) { this.auth = auth; this.load(); } });
    }
    async request(path, method = "GET", body) {
        return managementRequest(`appblocks/documents/${path}`, { method, body });
    }
    async load() {
        try { this.settings = await this.request("settings"); this.error = undefined; }
        catch (error) { this.error = error.message; }
    }
    async edit(item) {
        this.lookupVersion++; this.finding = false;
        this.editing = item ?? { id: null, name: "", url: "", hasApiKey: false, timeoutSeconds: 65 };
        this.output = item?.output ?? "pdf"; this.printers = []; this.printerNotice = undefined;
        this.error = undefined; this.message = undefined; this.removing = undefined;
        const editing = this.editing;
        await this.updateComplete;
        if (this.editing !== editing) return;
        const form = this.renderRoot.querySelector("form");
        for (const [name, value] of Object.entries({ name: editing.name, url: editing.url, timeout: editing.timeoutSeconds, key: "" }))
            form.elements.namedItem(name).value = value;
        const clear = form.elements.namedItem("clearKey");
        if (clear) clear.checked = false;
    }
    credentialsChanged() {
        this.lookupVersion++; this.printers = []; this.printerNotice = "Load printers using the current URL and API key.";
    }
    values(form) {
        const values = new FormData(form);
        return { id: this.editing.id, name: values.get("name"), url: values.get("url"), output: values.get("output"),
            apiKey: values.get("key"), clearApiKey: values.has("clearKey"), timeoutSeconds: Number(values.get("timeout")) };
    }
    async findPrinters() {
        const form = this.renderRoot.querySelector("form");
        const url = form.querySelector("[name=url]");
        if (!url.reportValidity()) return;
        const values = this.values(form);
        const version = ++this.lookupVersion;
        this.finding = true; this.printerNotice = undefined; this.error = undefined;
        try {
            const result = await this.request("printers", "POST", values);
            if (version !== this.lookupVersion) return;
            this.printers = result.printers;
            if (this.output !== "pdf" && !this.printers.includes(this.output.slice(8))) this.output = "pdf";
            this.printerNotice = this.printers.length ? "Printers loaded. Choose an output below." : "This Pressroom server exposes no printers. PDF is available.";
        } catch (error) { if (version === this.lookupVersion) this.printerNotice = error.message; }
        finally { if (version === this.lookupVersion) this.finding = false; }
    }
    async save(event) {
        event.preventDefault();
        const form = event.currentTarget;
        if (!form.reportValidity()) return;
        const values = this.values(form);
        this.busy = true; this.message = undefined; this.error = undefined;
        try {
            await this.request("configurations", "POST", values);
            this.settings = await this.request("settings");
            this.editing = undefined; this.message = "Configuration saved. It is available to documents and reports immediately.";
        } catch (error) { this.error = error.message; }
        finally { this.busy = false; }
    }
    async remove() {
        this.busy = true; this.error = undefined; this.message = undefined;
        try {
            this.settings = await this.request(`configurations/${this.removing.id}`, "DELETE");
            if (this.editing?.id === this.removing.id) this.editing = undefined;
            this.removing = undefined; this.message = "Configuration removed.";
        } catch (error) { this.error = error.message; }
        finally { this.busy = false; }
    }
    renderEditor() {
        if (!this.editing) return "";
        const savedPrinter = this.output.startsWith("printer:") ? this.output.slice(8) : null;
        return html`<form @submit=${this.save}><fieldset ?disabled=${this.busy || this.finding}>
            <h3>${this.editing.id ? "Edit configuration" : "Add Pressroom configuration"}</h3>
            <label for="pressroom-name">Name</label><input id="pressroom-name" name="name" required maxlength="100" placeholder="Office PDF or Reception printer" .value=${this.editing.name}>
            <label for="pressroom-url">Pressroom URL</label><input id="pressroom-url" name="url" type="url" required placeholder="https://pressroom.example/" .value=${this.editing.url} @input=${this.credentialsChanged}>
            <label for="pressroom-key">API key</label><input id="pressroom-key" name="key" type="password" autocomplete="new-password" maxlength="4096" @input=${this.credentialsChanged}
                placeholder=${this.editing.hasApiKey ? "Leave blank to keep the existing key" : "Optional for anonymous PDF rendering"}>
            <p class="hint">PDF can use anonymous rendering when Pressroom permits it. Listing printers and printing always require an API key.</p>
            ${this.editing.hasApiKey ? html`<label><input name="clearKey" type="checkbox" @change=${this.credentialsChanged}> Remove the saved API key</label>` : ""}
            <div class="actions"><button type="button" @click=${this.findPrinters}>${this.finding ? "Loading printers…" : "Load printers"}</button></div>
            <p class="hint" role="status">${this.printerNotice ?? "PDF is always available. Load printers to discover the server’s other outputs."}</p>
            <label for="pressroom-output">Output</label><select id="pressroom-output" name="output" @change=${event => { this.output = event.target.value; }}>
                <option value="pdf" .selected=${this.output === "pdf"}>PDF</option>
                ${savedPrinter && !this.printers.includes(savedPrinter) ? html`<option value=${this.output} .selected=${true}>${savedPrinter} (saved printer; refresh to check)</option>` : ""}
                ${this.printers.map(printer => html`<option value=${`printer:${printer}`} .selected=${this.output === `printer:${printer}`}>${printer}</option>`)}
            </select>
            <label for="pressroom-timeout">Timeout in seconds</label><input id="pressroom-timeout" name="timeout" type="number" min="0.001" max="2147483" step="any" required .value=${String(this.editing.timeoutSeconds)}>
            <p class="hint">API keys are stored encrypted and are never included in content exports.</p>
            <div class="actions"><button class="primary" type="submit">${this.busy ? "Saving…" : "Save configuration"}</button><button type="button" @click=${() => { this.editing = undefined; }}>Cancel</button></div>
        </fieldset></form>`;
    }
    render() {
        return html`<section><h2>Documents</h2><p>Add Pressroom configurations for documents and reports. Each configuration returns a PDF or submits to a printer exposed by that server.</p>
            <div aria-live="polite">${this.error ? html`<p class="error" role="alert">${this.error}</p>` : ""}${this.message ? html`<p class="message">${this.message}</p>` : ""}</div>
            ${this.settings ? html`
                ${this.settings.configurations.length ? html`<div class="scroll"><table><thead><tr><th>Name</th><th>Pressroom URL</th><th>Output</th><th>Actions</th></tr></thead><tbody>
                    ${this.settings.configurations.map(item => html`<tr><td>${item.name}</td><td>${item.url}<small>${item.hasApiKey ? "API key saved" : "No API key"}</small></td><td>${item.output === "pdf" ? "PDF" : item.output.slice(8)}</td>
                        <td><div class="actions"><button ?disabled=${this.busy || this.finding} @click=${() => this.edit(item)}>Edit</button><button class="danger" ?disabled=${this.busy || this.finding} @click=${() => { this.removing = item; }}>Remove</button></div></td></tr>`)}
                </tbody></table></div>` : html`<p>No Pressroom configurations. Template documents open as printable HTML.</p>`}
                <div class="actions"><button class="primary" ?disabled=${this.busy || this.finding} @click=${() => this.edit()}>Add configuration</button></div>
                ${this.removing ? html`<div class="confirm"><p>Remove “${this.removing.name}”? It will no longer be offered as a document output.</p><div class="actions"><button class="danger" ?disabled=${this.busy} @click=${this.remove}>Remove configuration</button><button ?disabled=${this.busy} @click=${() => { this.removing = undefined; }}>Keep configuration</button></div></div>` : ""}
                ${this.renderEditor()}
                <p class="hint">Template document links use the first PDF configuration, or open as HTML if none is configured. PDF failures fall back to HTML and are logged in Umbraco. Printer submissions are never retried automatically.</p>
            ` : this.error ? "" : html`<p role="status">Loading document settings…</p>`}
        </section>`;
    }
}
customElements.define("appblocks-documents-settings", DocumentsSettings);
