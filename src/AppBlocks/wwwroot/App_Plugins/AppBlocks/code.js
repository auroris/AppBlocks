import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import { html, css } from "@umbraco-cms/backoffice/external/lit";
import { UMB_AUTH_CONTEXT } from "@umbraco-cms/backoffice/auth";
import "@umbraco-cms/backoffice/components";
// Defines umb-code-editor, the backoffice's Monaco, which the backoffice otherwise loads only for its own editors.
import "@umbraco-cms/backoffice/code-editor";
import { request } from "./api.js";
import { forgetCatalogue } from "../ApplicationBlocks/catalogue.repository.js";

const blank = name => `namespace SiteCode;\n\npublic sealed class ${name}\n{\n}\n`;
const accessChoices = [["public", "Public", "Anyone the page admits may read, with or without an account."],
    ["notPublic", "Not public", "Any signed-in Member or backoffice User may read."],
    ["specificGroups", "Specific groups", "Only Members and Users in the read or write groups below may read."]];

/** The C# section: the site's C# folder, its compiler messages, and the New file wizard. */
export default class CodeWorkspace extends UmbLitElement {
    static properties = {
        state: { state: true }, loading: { state: true }, busy: { state: true }, error: { state: true }, message: { state: true },
        view: { state: true }, open: { state: true }, draft: { state: true }, folder: { state: true }, newPath: { state: true }, wizard: { state: true },
    };
    static styles = css`
        :host { display:block; height:100%; color:var(--uui-color-text); }
        * { box-sizing:border-box; }
        h2 { margin:0 0 10px; font-size:16px; } h3 { margin:16px 0 8px; font-size:14px; }
        p { line-height:1.6; color:var(--uui-color-text-alt); margin:4px 0 12px; }
        .error, .notice, .readonly { padding:14px 18px; border-left:3px solid; background:var(--uui-color-surface); margin-bottom:16px; line-height:1.5; }
        .error { color:var(--uui-color-danger); } .notice { color:var(--uui-color-positive); } .readonly { border-color:var(--uui-color-warning); }
        button { border:1px solid var(--uui-color-border); border-radius:var(--uui-border-radius); padding:7px 12px; background:var(--uui-color-surface); color:inherit; cursor:pointer; font:inherit; }
        button:hover { border-color:var(--uui-color-interactive); } button.primary { background:var(--uui-color-interactive); border-color:var(--uui-color-interactive); color:var(--uui-color-interactive-contrast); }
        button:disabled { opacity:.6; cursor:default; } :focus-visible { outline:2px solid var(--uui-color-focus); outline-offset:2px; }
        input[type=text], select { width:100%; max-width:520px; border:1px solid var(--uui-color-border); border-radius:var(--uui-border-radius); background:var(--uui-color-surface); color:inherit; padding:8px; font:inherit; }
        label { display:block; font-weight:600; margin:12px 0 6px; } .choice { font-weight:normal; display:flex; gap:8px; align-items:flex-start; margin:6px 0; } .choice small { display:block; color:var(--uui-color-text-alt); }
        fieldset { border:1px solid var(--uui-color-border); border-radius:var(--uui-border-radius); padding:10px 14px; margin:10px 0; } legend { font-weight:600; padding:0 4px; }
        .layout { display:grid; grid-template-columns:minmax(200px, 260px) 1fr; gap:16px; min-height:420px; }
        .tree { border-right:1px solid var(--uui-color-border); padding-right:12px; } .tree ul { list-style:none; margin:8px 0; padding:0; }
        .tree li button { display:flex; gap:6px; align-items:center; width:100%; text-align:left; border:0; padding:5px 6px; background:none; }
        .tree li button[aria-current=true] { background:var(--uui-color-current); color:var(--uui-color-current-contrast); }
        .tree li button.folder { font-weight:600; } .tree li button.selected-folder { outline:1px dashed var(--uui-color-border-emphasis); }
        .actions { display:flex; flex-wrap:wrap; gap:8px; align-items:center; margin:8px 0; } .actions .path { flex:1; font-family:ui-monospace,monospace; overflow-wrap:anywhere; }
        umb-code-editor { height:52vh; min-height:300px; border:1px solid var(--uui-color-border); border-radius:var(--uui-border-radius); }
        umb-code-editor.preview { height:40vh; }
        .status { display:flex; gap:12px; align-items:center; flex-wrap:wrap; margin:16px 0 8px; } .badge { font-size:11px; border-radius:var(--uui-border-radius); padding:3px 8px; background:var(--uui-color-surface-alt); }
        .badge.ok { background:var(--uui-color-positive); color:var(--uui-color-positive-contrast); } .badge.failed { background:var(--uui-color-danger); color:var(--uui-color-danger-contrast); }
        table { width:100%; border-collapse:collapse; } th { text-align:left; font-size:12px; color:var(--uui-color-text-alt); } th, td { padding:6px 8px; border-bottom:1px solid var(--uui-color-border); vertical-align:top; }
        td.where button { border:0; background:none; padding:0; color:var(--uui-color-interactive); text-decoration:underline; font-family:ui-monospace,monospace; }
        tr.error-row td:first-child { color:var(--uui-color-danger); font-weight:600; } .groups { display:grid; grid-template-columns:repeat(auto-fill, minmax(180px, 1fr)); gap:2px 12px; }
    `;
    constructor() {
        super(); this.loading = true; this.view = "empty"; this.folder = "";
        this.consumeContext(UMB_AUTH_CONTEXT, auth => { if (auth) { this.auth = auth; void this.load(); } });
    }
    get dirty() { return this.open && this.draft !== this.open.content; }
    async call(action, done) {
        this.busy = true; this.error = undefined; this.message = undefined;
        try {
            const result = await action();
            if (result?.entries) { this.state = result; forgetCatalogue(this.auth); }
            if (done) await done(result);
            return result;
        }
        catch (error) { this.error = error.message; }
        finally { this.busy = false; }
    }
    async load() {
        this.loading = true;
        await this.call(() => request("code"));
        this.loading = false;
    }
    canLeave() { return !this.dirty || confirm(`Discard the unsaved changes to ${this.open.path}?`); }
    async openFile(path, line, column) {
        if (this.open?.path !== path && !this.canLeave()) return;
        if (this.open?.path !== path) await this.call(() => request(`code/file?path=${encodeURIComponent(path)}`), file => {
            this.open = file; this.draft = file.content; this.view = "file";
        });
        if (this.open?.path === path) {
            this.view = "file"; await this.updateComplete;
            this.reveal(line, column); this.mark();
        }
    }
    reveal(line, column) {
        const editor = this.renderRoot.querySelector("umb-code-editor#source")?.editor?.monacoEditor;
        if (!editor || !line) return;
        editor.revealLineInCenter(line); editor.setPosition({ lineNumber: line, column: column || 1 }); editor.focus();
    }
    /** Shows the open file's compiler messages in the editor as well as in the list. */
    async mark() {
        const editor = this.renderRoot.querySelector("umb-code-editor#source")?.editor?.monacoEditor;
        const model = editor?.getModel();
        if (!model) return;
        const { monaco } = await import("@umbraco-cms/backoffice/external/monaco-editor");
        const severity = { error: monaco.MarkerSeverity.Error, warning: monaco.MarkerSeverity.Warning, info: monaco.MarkerSeverity.Info };
        monaco.editor.setModelMarkers(model, "appblocks", (this.state?.compile.diagnostics ?? []).filter(d => d.file === this.open?.path && d.line > 0).map(d => ({
            severity: severity[d.severity], message: `${d.id}: ${d.message}`, startLineNumber: d.line, startColumn: d.column, endLineNumber: d.line, endColumn: model.getLineMaxColumn(d.line),
        })));
    }
    async save() {
        if (!this.open || !this.state?.canEdit) return;
        const path = this.open.path, content = this.draft;
        await this.call(() => request("code/file", { path, content }), state => {
            this.open = { path, content };
            this.report(state, "Saved.");
            return this.mark();
        });
    }
    /** Says what a change did to the compiled classes: a notice when the folder compiled, an alert when the earlier classes stay in use. */
    report(state, done) {
        const compile = state.compile;
        const errors = compile.diagnostics.filter(d => d.severity === "error" && !d.id.startsWith("AB")).length;
        if (compile.succeeded) this.message = `${done} The folder compiled; ${compile.classes.length} class${compile.classes.length === 1 ? "" : "es"} in use.`;
        else this.error = `${done} The folder does not compile (${errors} error${errors === 1 ? "" : "s"}), so the classes compiled before stay in use.`;
    }
    async createFile(path, content) {
        await this.call(() => request("code/file/new", { path, content }), async state => {
            this.open = undefined; await this.openFile(path); this.report(state, `Created ${path}.`);
        });
    }
    async newFolder() {
        const path = prompt("Folder path, inside the C# folder", this.folder ? `${this.folder}/` : "");
        if (!path) return;
        await this.call(() => request("code/folder", { path }), () => { this.folder = path.replace(/\/+$/, ""); this.message = `Created the folder ${path}.`; });
    }
    async rename(path) {
        const to = prompt(`Rename or move ${path} to`, path);
        if (!to || to === path || !this.canLeave()) return;
        await this.call(() => request("code/rename", { from: path, to }), async () => {
            this.message = `Renamed ${path} to ${to}.`;
            if (this.open?.path === path) { this.open = undefined; await this.openFile(to); this.message = `Renamed ${path} to ${to}.`; }
            else if (this.open?.path.startsWith(path + "/")) { const moved = to + this.open.path.slice(path.length); this.open = undefined; await this.openFile(moved); }
        });
    }
    async remove(path) {
        if (!confirm(`Delete ${path}${path.endsWith(".cs") ? "" : " and everything in it"}? The classes it holds stop being used.`)) return;
        await this.call(() => request("code/delete", { path }), () => {
            if (this.open && (this.open.path === path || this.open.path.startsWith(path + "/"))) { this.open = undefined; this.view = "empty"; }
            this.message = `Deleted ${path}.`;
        });
    }
    startNew() {
        if (!this.canLeave()) return;
        this.view = "new"; this.newPath = `${this.folder ? this.folder + "/" : ""}NewClass.cs`; this.error = undefined; this.message = undefined;
    }
    // The wizard: each answer loads the next step's choices.
    async startWizard() {
        this.view = "wizard"; this.wizard = { step: "connection", recipe: "formClass", access: "notPublic", connections: [], tables: [], dataSources: [], groups: { memberGroups: [], userGroups: [] },
            read: { member: [], user: [] }, write: { member: [], user: [] }, anonymous: false, folder: this.folder };
        await this.call(async () => {
            const [connections, groups] = await Promise.all([request("connections"), request("code/wizard/groups")]);
            this.wizard = { ...this.wizard, connections, groups };
        });
    }
    setWizard(changes) { this.wizard = { ...this.wizard, ...changes, preview: undefined }; }
    async chooseConnection(connection) {
        this.setWizard({ connection, table: "", tables: [] });
        if (connection) await this.call(async () => this.setWizard({ tables: await request(`tables?connection=${encodeURIComponent(connection)}`) }));
    }
    async chooseTable(table) {
        this.setWizard({ table });
        await this.suggest();
    }
    async chooseRecipe(recipe) {
        this.setWizard({ recipe });
        await this.suggest();
    }
    async suggest() {
        const { connection, table, recipe } = this.wizard;
        if (!table) return;
        await this.call(async () => {
            const [names, dataSources] = await Promise.all([request(`code/wizard/suggest?table=${encodeURIComponent(table)}&recipe=${recipe}`),
                recipe === "backingClass" ? request(`code/wizard/data-sources?connection=${encodeURIComponent(connection)}&table=${encodeURIComponent(table)}`) : []]);
            this.setWizard({ className: names.className, alias: names.alias, dataSources, dataSource: dataSources[0]?.alias ?? "" });
        });
    }
    toggleGroup(list, kind, group, on) {
        const current = this.wizard[list][kind].filter(g => g !== group);
        this.setWizard({ [list]: { ...this.wizard[list], [kind]: on ? [...current, group] : current } });
    }
    recipeRequest() {
        const w = this.wizard, specific = w.access === "specificGroups";
        return { connection: w.connection, table: w.table, recipe: w.recipe, access: w.access, className: w.className ?? "", alias: w.alias ?? "", folder: w.folder ?? "",
            readMemberGroups: specific ? w.read.member : [], readUserGroups: specific ? w.read.user : [], writeMemberGroups: w.write.member, writeUserGroups: w.write.user,
            allowAnonymousWrites: w.access === "public" && w.anonymous && !w.write.member.length && !w.write.user.length, dataSource: w.recipe === "backingClass" ? w.dataSource : null };
    }
    async preview() {
        const form = this.renderRoot.querySelector("form.wizard");
        if (!form?.reportValidity()) return;
        await this.call(async () => { this.wizard = { ...this.wizard, preview: await request("code/wizard/preview", this.recipeRequest()) }; });
    }
    renderTree() {
        const entries = this.state?.entries ?? [];
        const editable = this.state?.canEdit;
        return html`<nav class="tree" aria-label="C# files">
            <div class="actions"><button class="primary" ?disabled=${!editable || this.busy} @click=${this.startNew}>New file</button><button ?disabled=${!editable || this.busy} @click=${this.newFolder}>New folder</button></div>
            <p>${this.state?.folder}${this.folder ? html` · new files go in <strong>${this.folder}</strong> <button @click=${() => this.folder = ""}>Use the top folder</button>` : ""}</p>
            ${entries.length ? html`<ul>${entries.map(entry => {
                const depth = entry.path.split("/").length - 1, name = entry.path.split("/").at(-1);
                return html`<li style="padding-left:${depth * 14}px">${entry.isFolder
                    ? html`<button class="folder ${this.folder === entry.path ? "selected-folder" : ""}" title="New files go in this folder" @click=${() => this.folder = this.folder === entry.path ? "" : entry.path}><umb-icon name="icon-folder" aria-hidden="true"></umb-icon>${name}</button>`
                    : html`<button aria-current=${this.open?.path === entry.path} @click=${() => this.openFile(entry.path)}><umb-icon name="icon-document" aria-hidden="true"></umb-icon>${name}</button>`}</li>`;
            })}</ul>` : html`<p>No C# files yet.${editable ? " Create one, or let the wizard write a form class or backing class for a table." : ""}</p>`}
        </nav>`;
    }
    renderFile() {
        const editable = this.state?.canEdit;
        return html`<div class="actions"><span class="path">${this.open.path}${this.dirty ? " •" : ""}</span>
                <button class="primary" ?disabled=${!editable || this.busy || !this.dirty} @click=${this.save}>Save</button>
                <button ?disabled=${!editable || this.busy} @click=${() => this.rename(this.open.path)}>Rename</button>
                <button ?disabled=${!editable || this.busy} @click=${() => this.remove(this.open.path)}>Delete</button></div>
            <umb-code-editor id="source" language="csharp" label=${`C# source of ${this.open.path}`} .code=${this.draft} ?readonly=${!editable}
                @input=${event => { this.draft = event.target.code; }} @loaded=${() => this.mark()}
                @keydown=${event => { if ((event.ctrlKey || event.metaKey) && event.key === "s") { event.preventDefault(); void this.save(); } }}></umb-code-editor>`;
    }
    renderNew() {
        const name = (this.newPath ?? "").split("/").at(-1).replace(/\.cs$/, "").replace(/[^A-Za-z0-9_]/g, "") || "NewClass";
        return html`<h2>New file</h2>
            <h3>Blank file</h3><form @submit=${event => { event.preventDefault(); void this.createFile(this.newPath, blank(/^[A-Za-z_]/.test(name) ? name : "NewClass")); }}>
                <label for="new-path">Path</label><input id="new-path" type="text" required pattern="[A-Za-z0-9_][A-Za-z0-9_./-]*\\.cs" .value=${this.newPath} @input=${event => this.newPath = event.target.value}>
                <p>Inside the C# folder, ending in .cs, such as Orders/OrderRules.cs. Folders are made as needed.</p>
                <div class="actions"><button class="primary" type="submit" ?disabled=${this.busy}>Create blank file</button><button type="button" @click=${() => this.view = this.open ? "file" : "empty"}>Cancel</button></div></form>
            <h3>From a table</h3><p>The wizard writes a form class with full create, read, update and delete for a table, or a backing class for a SQL data source on it, with the access checks you choose.</p>
            <div class="actions"><button @click=${this.startWizard}>Use the wizard</button></div>`;
    }
    renderGroups(list, kind, options, legend) {
        if (!options.length) return html`<fieldset><legend>${legend}</legend><p>There are none on this site.</p></fieldset>`;
        return html`<fieldset><legend>${legend}</legend><div class="groups">${options.map(option => {
            const value = kind === "member" ? option : option.alias, label = kind === "member" ? option : `${option.name} (${option.alias})`;
            return html`<label class="choice"><input type="checkbox" .checked=${this.wizard[list][kind].includes(value)} @change=${event => this.toggleGroup(list, kind, value, event.target.checked)}>${label}</label>`;
        })}</div></fieldset>`;
    }
    renderWizard() {
        const w = this.wizard;
        const writeGroups = w.write.member.length + w.write.user.length > 0;
        return html`<h2>New file from a table</h2><form class="wizard" @submit=${event => { event.preventDefault(); void this.preview(); }}>
            <h3>1. Connection and table</h3>
            <label for="wizard-connection">Connection</label><select id="wizard-connection" required .value=${w.connection ?? ""} @change=${event => this.chooseConnection(event.target.value)}>
                <option value="">Choose a connection</option>${w.connections.map(c => html`<option value=${c.name} ?selected=${w.connection === c.name}>${c.label}</option>`)}</select>
            <label for="wizard-table">Table or view</label><select id="wizard-table" required ?disabled=${!w.connection} .value=${w.table ?? ""} @change=${event => this.chooseTable(event.target.value)}>
                <option value="">${w.connection && !w.tables.length ? "No tables found" : "Choose a table or view"}</option>${w.tables.map(t => html`<option value=${t.value} ?selected=${w.table === t.value}>${t.name}</option>`)}</select>
            <h3>2. Recipe</h3>
            <label class="choice"><input type="radio" name="recipe" value="formClass" .checked=${w.recipe === "formClass"} @change=${() => this.chooseRecipe("formClass")}><span>Form class<small>Owns create, read, update and delete for the table, with version checking. Pick it in a form's Form Interface.</small></span></label>
            <label class="choice"><input type="radio" name="recipe" value="backingClass" .checked=${w.recipe === "backingClass"} @change=${() => this.chooseRecipe("backingClass")}><span>Backing class<small>Runs around the reads and writes of a SQL data source on the table. Pick it in the data source's Backing class.</small></span></label>
            ${w.recipe === "backingClass" ? html`<label for="wizard-data-source">Data source</label><select id="wizard-data-source" .value=${w.dataSource ?? ""} @change=${event => this.setWizard({ dataSource: event.target.value })}>
                <option value="">None yet: list the table's columns</option>${w.dataSources.map(d => html`<option value=${d.alias} ?selected=${w.dataSource === d.alias}>${d.name} (${d.alias})</option>`)}</select>` : ""}
            <h3>3. Access</h3>
            ${accessChoices.map(([value, label, hint]) => html`<label class="choice"><input type="radio" name="access" value=${value} .checked=${w.access === value} @change=${() => this.setWizard({ access: value })}><span>${label}<small>${hint}</small></span></label>`)}
            ${w.access === "specificGroups" ? html`${this.renderGroups("read", "member", w.groups.memberGroups, "Read Member groups")}${this.renderGroups("read", "user", w.groups.userGroups, "Read User groups")}` : ""}
            ${this.renderGroups("write", "member", w.groups.memberGroups, "Write Member groups")}${this.renderGroups("write", "user", w.groups.userGroups, "Write User groups")}
            <p>With no write groups, any account that may read may also write. With write groups, only their members may.</p>
            ${w.access === "public" ? html`<label class="choice"><input type="checkbox" .checked=${w.anonymous} ?disabled=${writeGroups} @change=${event => this.setWizard({ anonymous: event.target.checked })}><span>Allow anonymous writes<small>Visitors without an account may create, change and delete records too. Not with write groups.</small></span></label>` : ""}
            <h3>4. Names</h3>
            <label for="wizard-class">Class name</label><input id="wizard-class" type="text" required pattern="[A-Za-z_][A-Za-z0-9_]{0,63}" .value=${w.className ?? ""} @input=${event => this.setWizard({ className: event.target.value })}>
            <label for="wizard-alias">Alias</label><input id="wizard-alias" type="text" required pattern="[A-Za-z][A-Za-z0-9._\\-]{0,63}" .value=${w.alias ?? ""} @input=${event => this.setWizard({ alias: event.target.value })}>
            <label for="wizard-folder">Folder</label><input id="wizard-folder" type="text" .value=${w.folder ?? ""} placeholder="The top of the C# folder" @input=${event => this.setWizard({ folder: event.target.value })}>
            <div class="actions"><button class="primary" type="submit" ?disabled=${this.busy}>Preview</button><button type="button" @click=${() => { this.view = this.open ? "file" : "empty"; }}>Cancel</button></div></form>
            ${w.preview ? html`<h3>5. Preview</h3><p>${w.preview.path} — read it before you create it: this code runs inside the site once it is saved.</p>
                <umb-code-editor class="preview" language="csharp" label="Preview of the generated class" .code=${w.preview.content} readonly></umb-code-editor>
                <div class="actions"><button class="primary" ?disabled=${this.busy} @click=${() => this.createFile(w.preview.path, w.preview.content)}>Create file</button></div>` : ""}`;
    }
    renderCompile() {
        const compile = this.state?.compile;
        if (!compile) return "";
        const at = value => value ? new Date(value).toLocaleString() : "";
        return html`<section aria-label="Compilation">
            <div class="status"><h2>Compilation</h2>${!this.state.canEdit ? html`<span class="badge">Not compiled on this site</span>`
                : !compile.attempted ? html`<span class="badge">Nothing compiled yet</span>`
                : compile.succeeded ? html`<span class="badge ok">Compiled</span>` : html`<span class="badge failed">Does not compile</span>`}
                ${compile.generation ? html`<span>Generation ${compile.generation} in use since ${at(compile.compiled)}</span>` : ""}
                ${this.state.canEdit ? html`<button ?disabled=${this.busy} @click=${() => this.call(() => request("code/compile", {}), state => { this.report(state, "Compiled again."); return this.mark(); })}>Compile again</button>` : ""}</div>
            ${compile.diagnostics.length ? html`<table aria-label="Compiler messages"><thead><tr><th>Severity</th><th>Where</th><th>Code</th><th>Message</th></tr></thead><tbody>${compile.diagnostics.map(d => html`<tr class=${d.severity === "error" ? "error-row" : ""}>
                <td>${d.severity}</td><td class="where">${d.file ? html`<button @click=${() => this.openFile(d.file, d.line, d.column)}>${d.file}(${d.line},${d.column})</button>` : ""}</td><td>${d.id}</td><td>${d.message}</td></tr>`)}</tbody></table>`
                : compile.attempted ? html`<p>No errors or warnings.</p>` : ""}
            ${compile.classes.length ? html`<h3>Classes in use</h3><table aria-label="Classes in use"><thead><tr><th>Class</th><th>Extension point</th><th>Alias</th></tr></thead><tbody>${compile.classes.map(c => html`<tr><td>${c.type}</td><td>${c.extensionPoint}</td><td>${c.alias ?? ""}</td></tr>`)}</tbody></table>` : ""}
        </section>`;
    }
    render() {
        return html`<umb-body-layout headline="C#">
            <div aria-live="polite">${this.error ? html`<div class="error" role="alert">${this.error}</div>` : ""}${this.message ? html`<div class="notice" role="status">${this.message}</div>` : ""}</div>
            ${this.loading ? html`<p role="status">Loading the C# folder…</p>` : !this.state ? "" : html`
                ${this.state.canEdit ? html`<p>C# here is compiled into the site while it runs, the way development mode compiles views. Classes implementing IFormResource, IDataSourceHandler, IDataSourcePolicy or IBindVariableProvider are used beside the site's own; the site's own keep an alias they share. Code saved here runs with the site's full rights.</p>`
                    : html`<div class="readonly" role="note"><strong>Read-only.</strong> This site has no C# compiler, so these files are listed but not compiled or used, and nothing can be changed here. To edit and compile them, add the <code>${this.state.package}</code> package to the site; Umbraco refuses that package in Production mode.</div>`}
                <div class="layout">${this.renderTree()}<section aria-label="Editor">${this.view === "file" && this.open ? this.renderFile() : this.view === "new" ? this.renderNew() : this.view === "wizard" && this.wizard ? this.renderWizard()
                    : html`<p>${this.state.entries.length ? "Choose a file to open it." : ""}</p>`}</section></div>
                ${this.renderCompile()}`}
        </umb-body-layout>`;
    }
}
customElements.define("appblocks-code", CodeWorkspace);
