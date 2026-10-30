import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import { html, css } from "@umbraco-cms/backoffice/external/lit";
import { UMB_AUTH_CONTEXT } from "@umbraco-cms/backoffice/auth";
import { request, download, pathBase } from "./api.js";

export default class AppBlocksDashboard extends UmbLitElement {
    static properties = {
        companionPages: { state: true }, companionView: { state: true }, tab: { state: true }, forms: { state: true }, connections: { state: true }, issues: { state: true },
        error: { state: true }, message: { state: true }, busy: { state: true }, loading: { state: true },
        editing: { state: true }, sample: { state: true }, selectedForm: { state: true }, search: { state: true },
    };
    static styles = css`
        :host { display:block; padding:30px; color:var(--uui-color-text); --accent:#5143a3; }
        * { box-sizing:border-box; } header { display:flex; gap:18px; align-items:center; margin-bottom:28px; }
        .mark { display:grid; grid-template-columns:repeat(2,16px); gap:4px; padding:16px; border-radius:16px; background:#edeaf8; }
        .mark i { display:block; width:16px; height:16px; background:var(--accent); border-radius:4px; } .mark i:last-child { opacity:.45; }
        h1 { font-size:28px; margin:0 0 4px; letter-spacing:-.6px; } h2 { margin:0 0 10px; font-size:20px; } h3 { margin:0 0 8px; }
        p { margin:4px 0 16px; line-height:1.6; color:var(--uui-color-text-alt); } .eyebrow { text-transform:uppercase; letter-spacing:1.4px; font-size:11px; font-weight:700; color:var(--accent); margin:0 0 10px; }
        .summary { display:grid; grid-template-columns:repeat(3,minmax(0,1fr)); gap:16px; margin-bottom:26px; }
        .summary article, .panel { border:1px solid var(--uui-color-border); background:var(--uui-color-surface); border-radius:10px; padding:22px; }
        .summary strong { font-size:25px; display:block; margin-bottom:5px; } .summary span { color:var(--uui-color-text-alt); }
        nav { display:flex; gap:8px; border-bottom:1px solid var(--uui-color-border); margin-bottom:22px; padding-bottom:12px; }
        button, .button { border:1px solid var(--uui-color-border); border-radius:6px; padding:9px 15px; background:var(--uui-color-surface); color:inherit; cursor:pointer; font:inherit; text-decoration:none; }
        button:hover, .button:hover { border-color:var(--accent); } button[aria-pressed=true], button.primary { background:var(--accent); border-color:var(--accent); color:white; }
        button:disabled { opacity:.6; cursor:wait; } button:focus-visible, a:focus-visible { outline:3px solid #8f83db; outline-offset:3px; }
        a { color:var(--accent); } .toolbar { display:flex; align-items:center; justify-content:space-between; flex-wrap:wrap; gap:16px; margin-bottom:20px; }
        input, select, textarea { width:100%; border:1px solid var(--uui-color-border); border-radius:6px; background:var(--uui-color-surface); color:inherit; padding:10px; font:inherit; }
        .search { max-width:300px; } table { width:100%; border-collapse:collapse; } th { text-align:left; font-size:12px; color:var(--uui-color-text-alt); font-weight:600; }
        th, td { padding:14px 10px; border-bottom:1px solid var(--uui-color-border); } td:first-child { padding-left:0; } .table { overflow:auto; }
        td small { display:block; color:var(--uui-color-text-alt); margin-top:5px; } .badge { font-size:11px; border-radius:20px; padding:4px 8px; background:#eeeaf8; color:#5143a3; white-space:nowrap; }
        .empty { text-align:center; padding:48px 20px; max-width:600px; margin:auto; } .empty p { margin-bottom:24px; }
        .error, .notice { padding:14px 18px; border-radius:6px; margin-bottom:18px; line-height:1.5; } .error { background:#fff0f0; color:#9b2020; } .notice { background:#eaf5ee; color:#235635; }
        .form { max-width:680px; } label { display:block; margin:18px 0 6px; font-weight:600; } .hint { margin-top:6px; font-size:12px; }
        .actions { display:flex; flex-wrap:wrap; gap:10px; margin-top:22px; } pre { overflow:auto; padding:24px; background:#202331; color:#e6e8f0; border-radius:8px; font:12px/1.65 ui-monospace,monospace; tab-size:4; max-height:65vh; }
        .steps { display:grid; gap:20px; max-width:800px; } .steps article { display:grid; grid-template-columns:30px 1fr; gap:15px; } .step { width:28px; height:28px; display:grid; place-items:center; border-radius:50%; background:#edeaf8; color:var(--accent); }
        @media(max-width:760px) { :host { padding:18px; } .summary { grid-template-columns:1fr; } .summary article { padding:16px; } }
    `;
    constructor() {
        super(); this.companionPages = []; this.tab = "forms"; this.forms = []; this.connections = []; this.issues = []; this.search = ""; this.loading = true;
        this.consumeContext(UMB_AUTH_CONTEXT, auth => { if (auth) { this.auth = auth; this.load(); } });
    }
    async load() {
        this.error = undefined; this.loading = true;
        const results = await Promise.allSettled([request(this.auth, "forms"), request(this.auth, "connections"), request(this.auth, "dashboard-pages")]);
        if (results[0].status === "fulfilled") { this.forms = results[0].value.forms; this.issues = results[0].value.problems; }
        if (results[1].status === "fulfilled") this.connections = results[1].value;
        if (results[2].status === "fulfilled") this.companionPages = results[2].value;
        this.error = results.filter(r => r.status === "rejected").map(r => r.reason.message).join(" ") || undefined;
        this.loading = false;
    }
    async showCompanion(page) {
        this.tab = page.alias; this.error = undefined; this.message = undefined; this.companionView = undefined;
        try {
            await import(pathBase + page.script);
            if (this.tab === page.alias) this.companionView = document.createElement(page.element);
        } catch { this.error = "This settings view could not be loaded. Refresh and try again."; }
    }
    async showSample(form) {
        this.busy = true; this.error = undefined;
        try {
            this.sample = await request(this.auth, `forms/${form.pageKey}/${form.formKey}/sample${form.culture ? `?culture=${encodeURIComponent(form.culture)}` : ""}`, undefined, true);
            this.selectedForm = form;
        } catch (error) { this.error = error.message; }
        finally { this.busy = false; }
    }
    async guide() {
        try { download(await request(this.auth, "guide", undefined, true), "business-rules.md", "text/markdown"); }
        catch (error) { this.error = error.message; }
    }
    edit(connection) {
        this.editing = { name: connection?.name ?? "", provider: connection?.provider ?? "sqlServer", connectionString: "", existing: !!connection };
        this.message = undefined; this.error = undefined;
    }
    async saveConnection(test) {
        const form = this.renderRoot.querySelector("form");
        if (!form?.reportValidity()) return;
        const data = new FormData(form); const body = Object.fromEntries(data.entries());
        this.busy = true; this.error = undefined; this.message = undefined;
        try {
            const result = await request(this.auth, test ? "connections/test" : "connections", body);
            if (test) this.message = result.message;
            else { this.connections = result; this.editing = undefined; this.message = "Connection saved. It is available to forms and reports now."; }
        } catch (error) { this.error = error.message; }
        finally { this.busy = false; }
    }
    renderForms() {
        if (this.sample) return html`<section class="panel" aria-label="Sample C#"><div class="toolbar"><div><p class="eyebrow">Sample code</p><h2>${this.selectedForm?.name}</h2></div><button @click=${() => this.sample = undefined}>Back to forms</button></div>
            <p>A SQLite example using this form’s writable fields. It includes a sample table, a parameterized read and an upsert with version checking. AppBlocks does not execute this code.</p>
            <div class="actions"><button class="primary" @click=${() => download(this.sample, "SampleFormRepository.cs")}>Download sample C#</button><button @click=${this.guide}>Download business rules guide (.md)</button></div><pre tabindex="0"><code>${this.sample}</code></pre></section>`;
        const found = this.forms.filter(f => `${f.name} ${f.pageName} ${f.resource}`.toLowerCase().includes(this.search.toLowerCase()));
        return html`<section class="panel"><div class="toolbar"><div><h2>Forms on this site</h2><p>Saved forms, including drafts and language variants.</p></div><input class="search" type="search" aria-label="Find a form" placeholder="Find a form…" .value=${this.search} @input=${event => this.search = event.target.value}></div>
            ${this.issues.map(issue => html`<div class="error">${issue}</div>`)}
            ${this.forms.length ? html`<div class="table"><table><thead><tr><th>Form / page</th><th>Resource</th><th>Fields</th><th>State</th><th>Developer example</th></tr></thead><tbody>${found.map(form => html`<tr><td><strong>${form.name}</strong><small>${form.pageName}${form.culture ? ` · ${form.culture}` : ""}</small></td><td>${form.resource || "Not connected"}</td><td>${form.fields.length}</td><td><span class="badge">${form.published ? "Published" : "Draft"}</span></td><td><button ?disabled=${this.busy} @click=${() => this.showSample(form)}>View sample C#</button></td></tr>`)}</tbody></table>${found.length ? "" : html`<p>No forms match your search.</p>`}</div>` : html`<div class="empty"><p class="eyebrow">Your first form</p><h2>Start with a table. Build with blocks.</h2><p>Choose a database table as a data source, then add a Forms container to any content page. Its field pickers use the columns already in your table.</p><button class="primary" @click=${() => this.tab = "help"}>Show me how</button></div>`}</section>`;
    }
    renderConnections() {
        if (this.editing) return html`<section class="panel"><h2>${this.editing.existing ? "Edit" : "Add"} database connection</h2><p>Use a memorable name so editors can choose the right database.</p><form class="form" @submit=${event => { event.preventDefault(); this.saveConnection(false); }}>
            <label for="name">Connection name</label><input id="name" name="name" required pattern="[A-Za-z][A-Za-z0-9_-]{0,63}" maxlength="64" .value=${this.editing.name} ?readonly=${this.editing.existing} autocomplete="off">
            <label for="provider">Database type</label><select id="provider" name="provider" .value=${this.editing.provider}>${[["sqlServer","SQL Server"],["sqlite","SQLite"],["postgres","PostgreSQL"]].map(([value,label]) => html`<option value=${value} ?selected=${this.editing.provider === value}>${label}</option>`)}</select>
            <label for="connection">Connection string</label><input id="connection" name="connectionString" type="password" autocomplete="new-password" ?required=${!this.editing.existing} placeholder=${this.editing.existing ? "Leave blank to keep the saved connection string" : "Enter the database connection string"}>
            <p class="hint">Stored encrypted for this installation. Excluded from content and uSync exports. <a href="https://www.connectionstrings.com/" target="_blank" rel="noopener noreferrer">Connection string examples ↗</a></p>
            <div class="actions"><button type="submit" class="primary" ?disabled=${this.busy}>Save connection</button><button type="button" ?disabled=${this.busy} @click=${() => this.saveConnection(true)}>Test connection</button><button type="button" ?disabled=${this.busy} @click=${() => this.editing = undefined}>Cancel</button></div></form></section>`;
        return html`<section class="panel"><div class="toolbar"><div><h2>Database connections</h2><p>Umbraco’s database is ready to use. Add other databases when you need them.</p></div><button class="primary" @click=${() => this.edit()}>Add connection</button></div><div class="table"><table><thead><tr><th>Connection</th><th>Database type</th><th>Managed by</th><th></th></tr></thead><tbody>${this.connections.map(connection => html`<tr><td><strong>${connection.label}</strong> ${connection.isDefault ? html`<span class="badge">Default</span>` : ""}</td><td>${({sqlite:"SQLite",sqlServer:"SQL Server",postgres:"PostgreSQL",oracle:"Oracle"})[connection.provider] ?? connection.provider}</td><td>${connection.readOnly ? "Site configuration" : "AppBlocks"}</td><td>${connection.readOnly ? "" : html`<button @click=${() => this.edit(connection)}>Edit connection</button>`}</td></tr>`)}</tbody></table></div></section>`;
    }
    renderHelp() {
        return html`<section class="panel"><p class="eyebrow">Build your first form</p><h2>From database to working page</h2><p>Simple forms use the built-in data-source engine. Custom behaviour can use a C# resource.</p><div class="steps">
            ${[["Choose a database", "The default is the database Umbraco already uses. Add other named connections on the Connections tab."], ["Choose a table", "In Content, create a Data sources root and add a SQL data source. Give it an alias, choose its connection and table, and specify the key column. Choose whether it allows creating, editing or deleting rows, then publish it."], ["Build the form", "Add Forms container and Interactive Report to the allowed blocks in your site’s Block Grid or Block List. Add a Forms container to any page, then a Form with its interface, fields and actions. Reports choose a data source and save their view on the block."], ["Set access and publish", "Use Umbraco Public Access and the data source’s read/write groups. Interactive forms require a member with access or a backoffice administrator."], ["Add your business rules", "Open the Forms tab to download a sample for any saved form. Use the shared guide for user identity, groups, ownership and validation."]].map(([title,body],index) => html`<article><span class="step">${index+1}</span><div><h3>${title}</h3><p>${body}</p></div></article>`)}</div><div class="actions"><button @click=${this.guide}>Download business rules guide (.md)</button><a href="https://www.connectionstrings.com/" target="_blank" rel="noopener noreferrer">Connection string examples ↗</a></div></section>`;
    }
    render() {
        return html`<header><span class="mark" aria-hidden="true"><i></i><i></i><i></i><i></i></span><div><h1>AppBlocks</h1><p>Forms and reports, connected to your data.</p></div></header>
            <div class="summary"><article><strong>${this.loading ? "…" : this.forms.length}</strong><span>Forms across this site</span></article><article><strong>${this.loading ? "…" : this.connections.length}</strong><span>Database connections</span></article><article><strong>Umbraco database</strong><span>Your default connection · detected automatically</span></article></div>
            <nav aria-label="AppBlocks views">${[["forms","Forms"],["connections","Connections"],["help","Getting started"]].map(([key,label]) => html`<button aria-pressed=${this.tab===key} @click=${() => { this.tab=key; this.error=undefined; this.message=undefined; }}>${label}</button>`)}${this.companionPages.map(page => html`<button aria-pressed=${this.tab === page.alias} @click=${() => this.showCompanion(page)}>${page.label}</button>`)}<button ?disabled=${this.loading} @click=${this.load}>Refresh</button></nav>
            <div aria-live="polite">${this.error ? html`<div class="error" role="alert">${this.error}</div>` : ""}${this.message ? html`<div class="notice">${this.message}</div>` : ""}</div>
            ${this.loading ? html`<p role="status">Loading AppBlocks…</p>` : this.tab === "forms" ? this.renderForms() : this.tab === "connections" ? this.renderConnections() : this.tab === "help" ? this.renderHelp() : this.companionView}`;
    }
}
customElements.define("appblocks-dashboard", AppBlocksDashboard);
