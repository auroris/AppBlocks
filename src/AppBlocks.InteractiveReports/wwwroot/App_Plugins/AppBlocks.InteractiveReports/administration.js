import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import { html, css } from "@umbraco-cms/backoffice/external/lit";
import { UMB_AUTH_CONTEXT } from "@umbraco-cms/backoffice/auth";
import "@umbraco-cms/backoffice/components";
import { pathBase, request } from "../AppBlocks/api.js";

const reportsBase = `${pathBase}/umbraco/reports`;
const viewControls = "search columns rename columnSettings filter sort aggregate controlBreak highlight compute groupBy pivot chart pagination";

export default class ReportAdministration extends UmbLitElement {
    static properties = {
        loading: { state: true }, allowed: { state: true }, error: { state: true }, message: { state: true },
        creating: { state: true }, sources: { state: true }, sourceIndex: { state: true }, busy: { state: true }, preview: { state: true },
        reports: { state: true }, search: { state: true }, page: { state: true }, editing: { state: true }, owners: { state: true },
    };
    static styles = css`
        :host { display:block; height:100%; color:var(--uui-color-text); }
        h1 { font-size:var(--uui-type-h5-size,18px); margin:0; }
        p { line-height:1.6; color:var(--uui-color-text-alt); }
        .toolbar, .actions { display:flex; align-items:center; gap:12px; margin-bottom:20px; flex-wrap:wrap; }
        .toolbar p { flex:1; margin:0; }
        .error, .notice { padding:14px 18px; border-left:3px solid; margin-bottom:18px; background:var(--uui-color-surface); }
        .error { color:var(--uui-color-danger); } .notice { color:var(--uui-color-positive); }
        label { display:block; font-weight:600; margin:16px 0 6px; }
        input, select { display:block; box-sizing:border-box; width:100%; max-width:700px; padding:10px; font:inherit; color:inherit; background:var(--uui-color-surface); border:1px solid var(--uui-color-border); border-radius:var(--uui-border-radius); }
        .preview { margin:20px 0; } .actions { margin-top:20px; }
        button { padding:10px 16px; border:1px solid var(--uui-color-border); border-radius:var(--uui-border-radius); background:var(--uui-color-surface); color:inherit; font:inherit; cursor:pointer; }
        button.primary { background:var(--uui-color-interactive); color:var(--uui-color-interactive-contrast); border-color:var(--uui-color-interactive); }
        button:disabled { opacity:.6; cursor:default; }
        .table { overflow:auto; } table { width:100%; border-collapse:collapse; } th, td { padding:12px; border-bottom:1px solid var(--uui-color-border); text-align:left; }
        td small { display:block; color:var(--uui-color-text-alt); margin-top:5px; overflow-wrap:anywhere; } td:last-child { white-space:nowrap; } td button { margin:3px; }
        .pagination { display:flex; align-items:center; gap:16px; margin-top:20px; }
        :focus-visible { outline:2px solid var(--uui-color-focus); outline-offset:3px; }
    `;
    constructor() {
        super(); this.loading = true; this.allowed = false; this.sources = []; this.sourceIndex = "";
        this.reports = []; this.search = ""; this.page = 0; this.owners = []; this.ownerSearch = 0;
        this.consumeContext(UMB_AUTH_CONTEXT, auth => { if (auth) { this.auth = auth; void this.load(); } });
    }
    async load() {
        this.loading = true; this.error = undefined;
        try {
            this.allowed = (await request(this.auth, "report-administration/access")).allowed;
            if (!this.allowed) return;
            const results = await Promise.allSettled([
                request(this.auth, "report-administration/sources"), request(this.auth, "report-administration/reports"),
            ]);
            this.sources = results[0].status === "fulfilled" ? results[0].value : [];
            this.reports = results[1].status === "fulfilled" ? results[1].value : [];
            this.error = results.filter(result => result.status === "rejected").map(result => result.reason.message).join(" ") || undefined;
        } catch (error) { this.error = error.message; }
        finally { this.loading = false; }
    }
    async refreshReports() {
        this.reports = await request(this.auth, "report-administration/reports");
        this.page = 0;
    }
    async changeReport(report, method, suffix, body) {
        const token = await this.auth.getLatestToken();
        const response = await fetch(`${pathBase}/umbraco/management/api/v1/appblocks/report-administration/reports/${encodeURIComponent(report.id)}${suffix}`, {
            method, credentials: "same-origin", headers: { Authorization: `Bearer ${token}`, ...(body ? { "Content-Type": "application/json" } : {}) },
            body: body ? JSON.stringify(body) : undefined,
        });
        if (!response.ok) {
            const problem = await response.json().catch(() => null);
            throw new Error(problem?.detail ?? (response.status === 403 ? "You no longer have permission to manage this report." : `The report could not be changed (${response.status}).`));
        }
    }
    async deleteReport(report) {
        if (this.busy || !confirm(`Delete “${report.title}”? This permanently removes the saved report.`)) return;
        this.busy = true; this.error = undefined; this.message = undefined;
        try {
            await this.changeReport(report, "DELETE", `?modifiedUtc=${encodeURIComponent(report.modifiedUtc)}`);
            this.message = `Report “${report.title}” deleted.`;
            await this.refreshReports();
        } catch (error) { this.error = error.message; }
        finally { this.busy = false; }
    }
    async editOwner(report) {
        this.editing = report; this.error = undefined; this.message = undefined; this.owners = [];
        await this.findOwners("");
    }
    async findOwners(search) {
        const sequence = this.ownerSearch = (this.ownerSearch ?? 0) + 1;
        try {
            const response = await fetch(`${reportsBase}/admin/users?search=${encodeURIComponent(search)}`, { credentials: "same-origin" });
            if (!response.ok) throw new Error("Owner choices could not be loaded. You can still enter the account identity.");
            const result = await response.json();
            if (sequence === this.ownerSearch) this.owners = result.items;
        } catch (error) { if (sequence === this.ownerSearch) this.error = error.message; }
    }
    disconnectedCallback() { clearTimeout(this.ownerTimer); ++this.ownerSearch; super.disconnectedCallback(); }
    async saveOwner(event) {
        event.preventDefault();
        if (this.busy) return;
        this.busy = true; this.error = undefined; this.message = undefined;
        try {
            const owner = this.shadowRoot.querySelector("#report-owner").value.trim();
            await this.changeReport(this.editing, "PUT", "/owner", { owner, modifiedUtc: this.editing.modifiedUtc });
            this.message = `Report “${this.editing.title}” reassigned.`; this.editing = undefined;
            await this.refreshReports();
        } catch (error) { this.error = error.message; }
        finally { this.busy = false; }
    }
    async selectSource(event) {
        this.sourceIndex = event.target.value; this.preview = undefined; this.error = undefined;
        const selectedIndex = this.sourceIndex;
        const source = this.sources[Number(this.sourceIndex)];
        if (this.sourceIndex === "" || !source) return;
        try { await import(`${reportsBase}/ui/ir.js`); }
        catch { if (this.sourceIndex === selectedIndex) this.error = "The report viewer could not be loaded. Refresh and try again."; return; }
        if (this.sourceIndex !== selectedIndex || !this.creating) return;
        // Both viewer and save requests retain the block's culture and the backoffice session.
        const element = document.createElement("interactive-report");
        element.setAttribute("report", source.name);
        element.setAttribute("api-base", source.culture ? `${reportsBase}?appCulture=${encodeURIComponent(source.culture)}` : reportsBase);
        element.setAttribute("controls", viewControls);
        element.editLink = null; element.createLink = null;
        this.preview = element;
    }
    async savePublicReport(event) {
        event.preventDefault();
        if (this.busy) return;
        this.error = undefined; this.message = undefined; this.busy = true;
        try {
            const source = this.sources[Number(this.sourceIndex)];
            const title = this.shadowRoot.querySelector("#report-title").value.trim();
            if (!title || !source || !this.preview) throw new Error("Choose a report source and enter a title.");
            const state = this.preview.getReportDocument();
            const culture = source.culture ? `?appCulture=${encodeURIComponent(source.culture)}` : "";
            const response = await fetch(`${reportsBase}/${encodeURIComponent(source.name)}/saved${culture}`, {
                method: "POST", credentials: "same-origin", headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ title, state, isGlobal: true }),
            });
            if (!response.ok) {
                const problem = await response.json().catch(() => null);
                throw new Error(problem?.detail ?? problem?.description ?? problem?.message ?? `The report could not be created (${response.status}).`);
            }
            this.message = `Public report “${title}” created.`;
            this.creating = false; this.preview = undefined; this.sourceIndex = "";
            await this.refreshReports();
        } catch (error) { this.error = error.message; }
        finally { this.busy = false; }
    }
    renderReports() {
        const search = this.search.trim().toLowerCase();
        const filtered = this.reports.filter(report => [report.title, report.reportName, report.owner, report.isPublic ? "public" : "private"].some(value => value?.toLowerCase().includes(search)));
        const last = Math.max(0, Math.ceil(filtered.length / 25) - 1);
        const page = Math.min(this.page, last);
        return html`<label for="report-search">Find reports</label><input id="report-search" type="search" placeholder="Title, source, owner or visibility" .value=${this.search} @input=${event => { this.search = event.target.value; this.page = 0; }} />
            <p>${filtered.length} saved report${filtered.length === 1 ? "" : "s"}</p>
            ${filtered.length ? html`<div class="table"><table><thead><tr><th>Report</th><th>Owner</th><th>Visibility</th><th>Actions</th></tr></thead><tbody>
                ${filtered.slice(page * 25, (page + 1) * 25).map(report => html`<tr><td>${report.title}<small>${report.reportName}</small></td><td>${report.owner ?? "Configured report"}</td><td>${report.isPublic ? "Public" : "Private"}</td><td>
                    ${report.readOnly ? "Read-only" : html`<button type="button" ?disabled=${this.busy} @click=${() => this.editOwner(report)}>Reassign</button><button type="button" ?disabled=${this.busy} @click=${() => this.deleteReport(report)}>Delete</button>`}
                </td></tr>`)}
            </tbody></table></div><div class="pagination"><button type="button" ?disabled=${page === 0} @click=${() => this.page = page - 1}>Previous</button><span>Page ${page + 1} of ${last + 1}</span><button type="button" ?disabled=${page === last} @click=${() => this.page = page + 1}>Next</button></div>` : html`<p>No saved reports found.</p>`}`;
    }
    render() {
        return html`<umb-body-layout headline="Interactive Reports Administration">
            <h1 slot="header">Interactive Reports Administration</h1>
            <div aria-live="polite">${this.error ? html`<p class="error" role="alert">${this.error}</p>` : ""}${this.message ? html`<p class="notice" role="status">${this.message}</p>` : ""}</div>
            ${this.loading ? html`<p role="status">Loading report administration…</p>` : !this.allowed ? html`<p>You need the Interactive Reports Administrator or Administrator group to manage reports.</p>` : html`
                <div class="toolbar"><p>Manage public and private reports created by users. Reassign ownership or delete reports.</p>
                    <button type="button" ?disabled=${this.busy || this.creating || this.editing} @click=${this.load}>Refresh</button>
                    <button type="button" class="primary" ?disabled=${this.creating || this.busy || !this.sources.length} @click=${() => { this.creating = true; this.editing = undefined; this.error = undefined; this.message = undefined; }}>Create public report</button>
                </div>
                ${this.editing ? html`<uui-box headline="Reassign report"><form @submit=${this.saveOwner}>
                    <p>Choose a new owner for “${this.editing.title}”. The report keeps its current visibility and view.</p>
                    <label for="report-owner">New owner</label><input id="report-owner" list="report-owners" required maxlength="400" placeholder="Search users or enter an account identity" ?disabled=${this.busy} @input=${event => {
                        const value = event.target.value; clearTimeout(this.ownerTimer); this.ownerTimer = setTimeout(() => this.findOwners(value), 250);
                    }} /><datalist id="report-owners">${this.owners.map(owner => html`<option value=${owner.value}>${owner.display}</option>`)}</datalist>
                    <div class="actions"><button type="submit" class="primary" ?disabled=${this.busy}>Save owner</button><button type="button" ?disabled=${this.busy} @click=${() => this.editing = undefined}>Cancel</button></div>
                </form></uui-box>` : ""}
                ${this.creating ? html`<uui-box headline="Create public report"><form @submit=${this.savePublicReport}>
                    <p>Choose an existing report source, configure its view, then save a public report for everyone who can access that source.</p>
                    <label for="report-source">Report source</label><select id="report-source" required ?disabled=${this.busy} @change=${this.selectSource}>
                        <option value="">Choose a report source</option>${this.sources.map((source, index) => html`<option value=${index}>${source.title}${source.culture ? ` (${source.culture})` : ""}</option>`)}
                    </select>
                    <label for="report-title">Report title</label><input id="report-title" required maxlength="200" ?disabled=${this.busy} />
                    <div class="preview">${this.preview}</div>
                    <div class="actions"><button type="submit" class="primary" ?disabled=${this.busy || !this.preview}>Save public report</button>
                        <button type="button" ?disabled=${this.busy} @click=${() => { this.creating = false; this.preview = undefined; this.sourceIndex = ""; }}>Cancel</button></div>
                </form></uui-box>` : this.renderReports()}
            `}
        </umb-body-layout>`;
    }
}
customElements.define("appblocks-report-administration", ReportAdministration);
