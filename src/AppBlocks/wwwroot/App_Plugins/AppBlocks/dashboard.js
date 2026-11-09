import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import { html, css } from "@umbraco-cms/backoffice/external/lit";
import { UMB_AUTH_CONTEXT } from "@umbraco-cms/backoffice/auth";
import "@umbraco-cms/backoffice/components";
import { request, download, backofficePath } from "./api.js";
import "./business-rules.js";

const featureStates = { ready: "Ready", automatic: "Created on first use", action: "Needs setup", unsupported: "Not supported here", unreachable: "Cannot connect", siteManaged: "Managed by the site" };
const tableStates = { ready: "In place", missing: "Not found", noAccess: "No read and write access" };
const secretStates = { none: "Not set", configuration: "Set in site configuration", saved: "Set here, encrypted", unreadable: "Saved, but cannot be read" };

export default class AppBlocksDashboard extends UmbLitElement {
    static properties = {
        tab: { state: true }, forms: { state: true }, connections: { state: true }, kinds: { state: true }, issues: { state: true },
        error: { state: true }, message: { state: true }, busy: { state: true }, loading: { state: true },
        editing: { state: true }, sample: { state: true }, selectedForm: { state: true }, search: { state: true },
        setup: { state: true }, setupLoading: { state: true }, script: { state: true }, antiSpam: { state: true }, antiSpamEditing: { state: true },
        definitions: { state: true }, definitionsLoading: { state: true }, notes: { state: true },
    };
    static styles = css`
        :host { display:block; height:100%; min-height:0; color:var(--uui-color-text); container-type:inline-size; }
        * { box-sizing:border-box; }
        h2 { margin:0 0 10px; font-size:18px; } h3 { margin:0 0 8px; }
        p { margin:4px 0 16px; line-height:1.6; color:var(--uui-color-text-alt); } .eyebrow { font-size:12px; font-weight:700; color:var(--uui-color-text-alt); margin:0 0 10px; }
        .intro { display:flex; align-items:center; justify-content:space-between; gap:16px; margin-bottom:var(--uui-size-space-5); }
        .intro p { margin:0; } .intro uui-button { flex-shrink:0; }
        button { border:1px solid var(--uui-color-border); border-radius:var(--uui-border-radius); padding:9px 15px; background:var(--uui-color-surface); color:inherit; cursor:pointer; font:inherit; }
        button:hover { border-color:var(--uui-color-interactive); background:var(--uui-color-surface-emphasis); }
        button.primary { background:var(--uui-color-interactive); border-color:var(--uui-color-interactive); color:var(--uui-color-interactive-contrast); }
        button.primary:hover { background:var(--uui-color-interactive-emphasis); }
        button:disabled { opacity:.6; cursor:wait; } :focus-visible { outline:2px solid var(--uui-color-focus); outline-offset:3px; }
        a { color:var(--uui-color-interactive); } .toolbar { display:flex; align-items:center; justify-content:space-between; flex-wrap:wrap; gap:16px; margin-bottom:20px; }
        input, select, textarea { width:100%; border:1px solid var(--uui-color-border); border-radius:var(--uui-border-radius); background:var(--uui-color-surface); color:inherit; padding:10px; font:inherit; }
        .search { max-width:300px; } table { width:100%; border-collapse:collapse; } th { text-align:left; font-size:12px; color:var(--uui-color-text-alt); font-weight:600; }
        th, td { padding:14px 10px; border-bottom:1px solid var(--uui-color-border); } td:first-child { padding-left:0; } .table { overflow:auto; }
        td small { display:block; color:var(--uui-color-text-alt); margin-top:5px; } .badge { font-size:11px; border-radius:var(--uui-border-radius); padding:4px 8px; background:var(--uui-color-surface-alt); color:var(--uui-color-text); white-space:nowrap; }
        .empty { text-align:center; padding:48px 20px; max-width:600px; margin:auto; } .empty p { margin-bottom:24px; }
        .error, .notice { padding:14px 18px; border-left:3px solid; background:var(--uui-color-surface); margin-bottom:18px; line-height:1.5; } .error { color:var(--uui-color-danger); } .notice { color:var(--uui-color-positive); }
        .form { max-width:680px; } label { display:block; margin:18px 0 6px; font-weight:600; } .hint { margin-top:6px; font-size:12px; }
        .actions { display:flex; flex-wrap:wrap; gap:10px; margin-top:22px; } pre { overflow:auto; padding:24px; background:#202331; color:#e6e8f0; border-radius:8px; font:12px/1.65 ui-monospace,monospace; tab-size:4; max-height:65vh; }
        .setup { display:grid; gap:var(--uui-size-space-5); } .setup-facts { display:grid; grid-template-columns:max-content 1fr; gap:6px 16px; margin:0 0 16px; } .setup-facts dt { font-weight:600; } .setup-facts dd { margin:0; }
        .setup .table { margin-bottom:16px; } .setup summary { cursor:pointer; font-weight:600; margin-bottom:12px; } .setup pre { margin:8px 0 0; padding:16px; } .setup h3 { margin-top:20px; } .setup .actions { margin-top:10px; }
        .badge.ready { background:var(--uui-color-positive); color:var(--uui-color-positive-contrast); } .badge.action, .badge.unsupported, .badge.unreachable { background:var(--uui-color-warning); color:var(--uui-color-warning-contrast); }
        .steps { display:grid; gap:20px; max-width:800px; } .steps article { display:grid; grid-template-columns:30px 1fr; gap:15px; } .step { width:28px; height:28px; display:grid; place-items:center; border-radius:50%; background:var(--uui-color-surface-alt); color:var(--uui-color-text); }
    `;
    constructor() {
        super(); this.tab = "forms"; this.setup = []; this.antiSpam = []; this.forms = []; this.connections = []; this.kinds = []; this.issues = []; this.search = ""; this.loading = true;
        this.consumeContext(UMB_AUTH_CONTEXT, auth => { if (auth) { this.auth = auth; this.load(); } });
    }
    set manifest(value) { this.tab = value.meta.pathname; if (this.auth) this.load(); }
    viewUrl(tab) { return `${backofficePath}/section/settings/workspace/appblocks/view/${tab}`; }
    async load() {
        this.error = undefined; this.loading = true;
        try {
            if (this.tab === "forms") {
                const result = await request("forms"); this.forms = result.forms; this.issues = result.problems;
            }
            if (this.tab === "connections") [this.connections, this.kinds] = await Promise.all([request("connections"), request("database-kinds")]);
            if (this.tab === "setup") await this.loadSetup();
            if (this.tab === "definitions") await this.loadDefinitions();
            if (this.tab === "anti-spam") await this.loadAntiSpam();
        } catch (error) { this.error = error.message; }
        finally { this.loading = false; }
    }
    async loadDefinitions() {
        this.definitionsLoading = true;
        try { this.definitions = await request("definitions"); }
        catch (error) { this.error = error.message; }
        finally { this.definitionsLoading = false; }
    }
    async changeDefinitions(action, question, done) {
        if (!confirm(question)) return;
        this.busy = true; this.error = undefined; this.message = undefined; this.notes = [];
        try {
            const change = await request(`definitions/${action}`, {});
            this.message = done(change); this.notes = change.notes;
            await this.loadDefinitions();
        } catch (error) { this.error = error.message; }
        finally { this.busy = false; }
    }
    // Only when the tab is open: looking is read-only, but it does connect to each feature's database.
    async loadSetup() {
        this.setupLoading = true;
        try { this.setup = await request("database-setup"); }
        catch (error) { this.error = error.message; }
        finally { this.setupLoading = false; }
    }
    async loadAntiSpam() {
        try { this.antiSpam = await request("anti-spam"); }
        catch (error) { this.error = error.message; }
    }
    async saveAntiSpam(service, changes) {
        this.busy = true; this.error = undefined; this.message = undefined;
        try {
            this.antiSpam = await request("anti-spam", { service: service.alias, ...changes });
            this.antiSpamEditing = undefined; this.message = `${service.name} keys saved.`;
        } catch (error) { this.error = error.message; }
        finally { this.busy = false; }
    }
    async scriptOf(feature) {
        if (this.script?.alias === feature.alias) return this.script.text;
        return request(`database-setup/${feature.alias}/script`, undefined, true);
    }
    async showScript(feature) {
        this.busy = true; this.error = undefined;
        try { this.script = { alias: feature.alias, text: await this.scriptOf(feature) }; }
        catch (error) { this.error = error.message; }
        finally { this.busy = false; }
    }
    async downloadScript(feature) {
        this.busy = true; this.error = undefined;
        try { download(await this.scriptOf(feature), `appblocks-${feature.alias}-${feature.provider}.sql`, "application/sql"); }
        catch (error) { this.error = error.message; }
        finally { this.busy = false; }
    }
    async showSample(form) {
        this.busy = true; this.error = undefined;
        try {
            this.sample = await request(`forms/${form.pageKey}/${form.formKey}/sample${form.culture ? `?culture=${encodeURIComponent(form.culture)}` : ""}`, undefined, true);
            this.selectedForm = form;
        } catch (error) { this.error = error.message; }
        finally { this.busy = false; }
    }
    edit(connection) {
        this.editing = { name: connection?.name ?? "", provider: connection?.provider ?? this.kinds[0]?.provider, connectionString: "", existing: !!connection };
        this.message = undefined; this.error = undefined;
    }
    async saveConnection(test) {
        const form = this.renderRoot.querySelector("form");
        if (!form?.reportValidity()) return;
        const data = new FormData(form); const body = Object.fromEntries(data.entries());
        this.busy = true; this.error = undefined; this.message = undefined;
        try {
            const result = await request(test ? "connections/test" : "connections", body);
            if (test) this.message = result.message;
            else { this.connections = result; this.editing = undefined; this.message = "Connection saved. It is available to forms and reports now."; }
        } catch (error) { this.error = error.message; }
        finally { this.busy = false; }
    }
    renderForms() {
        if (this.sample) return html`<uui-box class="panel" aria-label="Sample C#"><div class="toolbar"><div><p class="eyebrow">Sample code</p><h2>${this.selectedForm?.name}</h2></div><button @click=${() => this.sample = undefined}>Back to forms</button></div>
            <p>A SQLite example using this form’s writable fields. It includes a sample table, a parameterized read and a save that compares a checksum of the row's values to refuse a stale change. AppBlocks does not execute this code. To have a working form class written for one of your own tables, use the wizard in <a href=${`${backofficePath}/section/settings/workspace/appblocks-code`}>Settings → AppBlocks → Backing Classes</a>, on a development site.</p>
            <div class="actions"><button class="primary" @click=${() => download(this.sample, "SampleFormRepository.cs")}>Download sample C#</button><a href=${this.viewUrl("business-rules")}>Read business rules</a></div><pre tabindex="0"><code>${this.sample}</code></pre></uui-box>`;
        const found = this.forms.filter(f => `${f.name} ${f.pageName} ${f.resource}`.toLowerCase().includes(this.search.toLowerCase()));
        return html`<uui-box class="panel"><div class="toolbar"><div><h2>Forms on this site</h2><p>Saved forms, including drafts and language variants.</p></div><input class="search" type="search" aria-label="Find a form" placeholder="Find a form…" .value=${this.search} @input=${event => this.search = event.target.value}></div>
            ${this.issues.map(issue => html`<div class="error">${issue}</div>`)}
            ${this.forms.length ? html`<div class="table"><table><thead><tr><th>Form / page</th><th>Resource</th><th>Fields</th><th>State</th><th>Developer example</th></tr></thead><tbody>${found.map(form => html`<tr><td><strong>${form.name}</strong><small>${form.pageName}${form.culture ? ` · ${form.culture}` : ""}</small></td><td>${form.resource || "Not connected"}</td><td>${form.fields.length}</td><td><span class="badge">${form.published ? "Published" : "Draft"}</span></td><td><button ?disabled=${this.busy} @click=${() => this.showSample(form)}>View sample C#</button></td></tr>`)}</tbody></table>${found.length ? "" : html`<p>No forms match your search.</p>`}</div>` : html`<div class="empty"><p class="eyebrow">Your first form</p><h2>Register a form class. Build with blocks.</h2><p>A registered C# form class defines your fields, permissions and save behaviour. Add a Forms container to a page, then select that class in the Form Interface.</p><a href=${this.viewUrl("help")}>Show me how</a></div>`}</uui-box>`;
    }
    renderConnections() {
        if (this.editing) return html`<uui-box class="panel"><h2>${this.editing.existing ? "Edit" : "Add"} database connection</h2><p>Use a memorable name so editors can choose the right database.</p><form class="form" @submit=${event => { event.preventDefault(); this.saveConnection(false); }}>
            <label for="name">Connection name</label><input id="name" name="name" required pattern="[A-Za-z][A-Za-z0-9_-]{0,63}" maxlength="64" .value=${this.editing.name} ?readonly=${this.editing.existing} autocomplete="off">
            <label for="provider">Database type</label><select id="provider" name="provider" aria-describedby="oracle-compatibility" .value=${this.editing.provider}>${this.kinds.map(kind => html`<option value=${kind.provider} ?selected=${this.editing.provider === kind.provider}>${kind.name}</option>`)}</select>
            <p id="oracle-compatibility" class="hint">Oracle 11.2 and Oracle 12+ select the Oracle SQL compatibility mode used by Interactive Reports and AppBlocks forms.</p>
            <label for="connection">Connection string</label><input id="connection" name="connectionString" type="password" autocomplete="new-password" ?required=${!this.editing.existing} placeholder=${this.editing.existing ? "Leave blank to keep the saved connection string" : "Enter the database connection string"}>
            <p class="hint">Stored encrypted for this installation. Excluded from content exports. <a href="https://www.connectionstrings.com/" target="_blank" rel="noopener noreferrer">Connection string examples ↗</a></p>
            <div class="actions"><button type="submit" class="primary" ?disabled=${this.busy}>Save connection</button><button type="button" ?disabled=${this.busy} @click=${() => this.saveConnection(true)}>Test connection</button><button type="button" ?disabled=${this.busy} @click=${() => this.editing = undefined}>Cancel</button></div></form></uui-box>`;
        return html`<uui-box class="panel"><div class="toolbar"><div><h2>Database connections</h2><p>Use the AppBlocks database for forms, reports and uploads. It keeps application work separate from Umbraco’s SQLite database.</p></div><button class="primary" @click=${() => this.edit()}>Add connection</button></div><div class="table"><table><thead><tr><th>Connection</th><th>Database type</th><th>Managed by</th><th></th></tr></thead><tbody>${this.connections.map(connection => html`<tr><td><strong>${connection.label}</strong> ${connection.isDefault ? html`<span class="badge">Default</span>` : ""}</td><td>${connection.databaseType}</td><td>${connection.readOnly ? "Site configuration" : "AppBlocks"}</td><td>${connection.readOnly ? "" : html`<button @click=${() => this.edit(connection)}>Edit connection</button>`}</td></tr>`)}</tbody></table></div></uui-box>`;
    }
    renderSetup() {
        return html`<div class="setup"><p>AppBlocks keeps a few tables of its own. It creates them on first use when the database account may. If it may not, grant the permission or run the script: either works without a restart, and everything else keeps working in the meantime.</p>
            ${this.setupLoading && !this.setup.length ? html`<p role="status">Checking the databases…</p>` : this.setup.map(feature => this.renderFeature(feature))}</div>`;
    }
    renderFeature(feature) {
        const needed = feature.state !== "ready" && feature.state !== "automatic";
        const remedies = feature.grantText || feature.hasScript ? html`<details ?open=${needed}><summary>${needed ? "How to set this up" : "Setting this up by hand"}</summary>
            ${feature.grantText ? html`<h3>Option 1: let AppBlocks create the tables</h3><p>${feature.grantText}</p>${feature.grantSql ? html`<pre tabindex="0"><code>${feature.grantSql}</code></pre>` : ""}` : ""}
            ${feature.hasScript ? html`<h3>Option 2: create the tables yourself</h3><p>Run this script, made from what AppBlocks itself would run. The account then needs no right to create anything.</p>
                <div class="actions"><button ?disabled=${this.busy} @click=${() => this.showScript(feature)}>View script</button><button ?disabled=${this.busy} @click=${() => this.downloadScript(feature)}>Download script</button></div>
                ${this.script?.alias === feature.alias ? html`<pre tabindex="0" aria-label="Script"><code>${this.script.text}</code></pre>` : ""}` : ""}
            </details>` : "";
        return html`<uui-box class="panel" data-feature=${feature.alias}><div class="toolbar"><div><h2>${feature.title}</h2><p>${feature.summary}</p></div><span class="badge ${feature.state}">${featureStates[feature.state] ?? feature.state}</span></div>
            <dl class="setup-facts"><dt>Connection</dt><dd>${feature.connection}</dd><dt>Database type</dt><dd>${feature.databaseType}</dd></dl>
            ${feature.tables.length ? html`<div class="table"><table><thead><tr><th>Table</th><th>State</th></tr></thead><tbody>${feature.tables.map(table => html`<tr><td><strong>${table.name}</strong>${table.required ? "" : html`<small>Used only by the saved report administration page.</small>`}</td><td>${tableStates[table.state] ?? table.state}</td></tr>`)}</tbody></table></div>` : ""}
            <p>${feature.lost}</p>${remedies}</uui-box>`;
    }
    renderDefinitions() {
        const status = this.definitions;
        if (!status) return html`<p role="status">${this.definitionsLoading ? "Checking the definitions…" : ""}</p>`;
        return html`<uui-box class="panel"><div class="toolbar"><div><h2>Package definitions</h2><p>The element types, document types and data types each AppBlocks package installs in Umbraco, and the package migration plan that updates them when a new version needs to.</p></div></div>
            ${status.uninstalled ? html`<div class="error" role="alert">The definitions were uninstalled from this site. Nothing installs on startup until they are reinstalled.</div>` : ""}
            <div class="table"><table><thead><tr><th>Package</th><th>Version</th><th>Definitions</th><th>Migrations</th></tr></thead><tbody>${status.packages.map(p => html`<tr data-package=${p.id}><td><strong>${p.name}</strong></td><td>${p.version}</td><td>${p.installed === p.declared ? html`<span class="badge ready">All ${p.declared} installed</span>` : html`<span class="badge action">${p.installed} of ${p.declared} installed</span>`}</td><td>${p.pending ? html`<span class="badge action">Pending</span>` : html`<span class="badge ready">Up to date</span>`}<small>${p.state ? `At ${p.state}` : "Not run yet"}</small></td></tr>`)}</tbody></table></div>
            ${this.notes?.length ? html`<ul>${this.notes.map(note => html`<li>${note}</li>`)}</ul>` : ""}
            <h3>Reinstall</h3><p>Resets every definition above to the installed package's version, in place: names, properties, allowed blocks, data type settings and folders. Pages, data sources and the site's own types keep their values; a property the package no longer has loses its values. Use it after updating the package when no migration covers the change, or to undo edits made to these definitions in the backoffice.</p>
            <div class="actions"><button class="primary" ?disabled=${this.busy} @click=${() => this.changeDefinitions("reinstall", "Reset every AppBlocks definition to the package's version? Edits made to them in the backoffice are lost.", change => `Reinstalled the definitions: ${change.created} created, ${change.updated} reset.`)}>Reinstall definitions</button></div>
            <h3>Uninstall</h3><p>Removes every definition above from Umbraco. It refuses while content of these types exists or a type of the site's own uses one of these data types, and names what to remove first. Blocks already on pages stay in the pages, hidden, until the definitions are installed again. Nothing installs on later starts until Reinstall, so the package can be removed from the site afterwards.</p>
            <div class="actions"><button ?disabled=${this.busy} @click=${() => this.changeDefinitions("uninstall", "Remove every AppBlocks definition from Umbraco?", change => `Uninstalled the definitions: ${change.deleted} removed.`)}>Uninstall definitions</button></div></uui-box>`;
    }
    renderAntiSpam() {
        const editing = this.antiSpam.find(service => service.alias === this.antiSpamEditing);
        if (editing) return html`<uui-box class="panel"><h2>${editing.name} keys</h2><form class="form" data-anti-spam-keys @submit=${event => { event.preventDefault(); this.saveAntiSpam(editing, Object.fromEntries(new FormData(event.target).entries())); }}>
            ${editing.siteKeyManaged ? html`<p>The site key is set in the site's configuration.</p>` : html`<label for="anti-spam-site-key">Site key</label><input id="anti-spam-site-key" name="siteKey" maxlength="256" pattern="[A-Za-z0-9_.:\\-]*" autocomplete="off" .value=${editing.siteKey ?? ""}><p class="hint">Public. Used by any Anti-spam block that leaves its own empty.</p>`}
            ${editing.secret === "configuration" ? html`<p>The secret key is set in the site's configuration.</p>` : html`<label for="anti-spam-secret">Secret key</label><input id="anti-spam-secret" name="secretKey" type="password" autocomplete="new-password" maxlength="1024" placeholder=${editing.secret === "none" ? "Enter the secret key" : "Leave blank to keep the saved secret key"}><p class="hint">Stored encrypted for this installation. Excluded from content exports. It is never shown again.</p>`}
            <div class="actions"><button type="submit" class="primary" ?disabled=${this.busy}>Save keys</button>${editing.secret === "saved" || editing.secret === "unreadable" ? html`<button type="button" ?disabled=${this.busy} @click=${() => this.saveAntiSpam(editing, { removeSecret: true })}>Remove the saved secret key</button>` : ""}<button type="button" ?disabled=${this.busy} @click=${() => this.antiSpamEditing = undefined}>Cancel</button></div></form></uui-box>`;
        return html`<uui-box class="panel"><div class="toolbar"><div><h2>Anti-spam</h2><p>The keys behind the Anti-spam block, which editors place inside a form. A form using a service with no secret key refuses every submission. The honeypot needs no keys.</p></div></div>
            <div class="table"><table><thead><tr><th>Service</th><th>Site key</th><th>Secret key</th><th>Status</th><th></th></tr></thead><tbody>${this.antiSpam.map(service => html`<tr data-service=${service.alias}><td><strong>${service.name}</strong></td><td>${service.siteKey ?? "On each block"}</td><td>${secretStates[service.secret] ?? service.secret}</td><td>${service.problem ? html`<span class="badge action">Refusing submissions</span><small role="alert">${service.problem}</small>` : service.secret === "configuration" || service.secret === "saved" ? html`<span class="badge ready">No problems seen</span>` : html`<span class="badge">Not set up</span>`}</td><td>${service.secret === "configuration" && service.siteKeyManaged ? "" : html`<button @click=${() => { this.antiSpamEditing = service.alias; this.message = undefined; this.error = undefined; }}>Set keys</button>`}</td></tr>`)}</tbody></table></div>
            <p>A problem clears itself once the service answers again. These services load third-party scripts in the visitor's browser and may need cookie consent on some sites; consent handling stays with the site. This is not a rate limit: see the business rules.</p></uui-box>`;
    }
    renderHelp() {
        return html`<uui-box class="panel"><p class="eyebrow">Build your first form</p><h2>From form class to working page</h2><p>Each form binds to a C# class registered as IFormResource. Its fields drive the editor's field pickers.</p><div class="steps">
            ${[["Register a form class", "Implement IFormResource, or derive from FormResource<TCreate, TUpdate>, and register it with services.AddScoped<IFormResource, YourResource>(). Define its fields, validation, permissions and persistence."], ["Choose storage", "Your class can use your application's services or database. SQL data source nodes support lists, tabular forms and record owners; they are not selectable as a form class. Add named database connections on the Connections view when needed."], ["Build the form", "Allow Forms container in your site's Block Grid or Block List. Add one to a page, add a Form, and select your registered class in the Form Interface. Add its fields and Form actions."], ["Set access and publish", "Enforce who may read, create, change and delete records in every resource operation. Umbraco Member Public Access still protects the page; an open page can admit anonymous visitors when the resource permits them."], ["Review your business rules", "The Forms view lists saved forms and offers C# examples. The shared guide covers identity, groups, ownership, validation and transactions."]].map(([title,body],index) => html`<article><span class="step">${index+1}</span><div><h3>${title}</h3><p>${body}</p></div></article>`)}</div><div class="actions"><a href=${this.viewUrl("business-rules")}>Read business rules</a></div></uui-box>`;
    }
    render() {
        return html`<umb-body-layout>
            ${!["help", "business-rules"].includes(this.tab) ? html`<div class="intro"><p>Forms and reports, connected to your data.</p><uui-button look="secondary" label="Refresh" ?disabled=${this.loading} @click=${this.load}><umb-icon name="icon-refresh" aria-hidden="true"></umb-icon> Refresh</uui-button></div>` : ""}
            <div aria-live="polite">${this.error ? html`<div class="error" role="alert">${this.error}</div>` : ""}${this.message ? html`<div class="notice">${this.message}</div>` : ""}</div>
            ${this.loading ? html`<p role="status">Loading AppBlocks…</p>` : this.tab === "forms" ? this.renderForms() : this.tab === "connections" ? this.renderConnections() : this.tab === "setup" ? this.renderSetup() : this.tab === "definitions" ? this.renderDefinitions() : this.tab === "anti-spam" ? this.renderAntiSpam() : this.tab === "help" ? this.renderHelp() : this.tab === "business-rules" ? html`<appblocks-business-rules></appblocks-business-rules>` : ""}
        </umb-body-layout>`;
    }
}
customElements.define("appblocks-dashboard", AppBlocksDashboard);
