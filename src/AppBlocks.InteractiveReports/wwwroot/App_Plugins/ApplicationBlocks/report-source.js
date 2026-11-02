import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import { html, css } from "@umbraco-cms/backoffice/external/lit";
import { UmbChangeEvent } from "@umbraco-cms/backoffice/event";
import { UMB_AUTH_CONTEXT } from "@umbraco-cms/backoffice/auth";
import { request } from "../AppBlocks/api.js";

export default class ReportSource extends UmbLitElement {
    static properties = { value: {}, readonly: { type: Boolean }, connections: { state: true }, tables: { state: true }, loading: { state: true }, error: { state: true } };
    static styles = css`
        :host { display:block; } label { display:block; margin:12px 0 5px; }
        select, textarea { width:100%; box-sizing:border-box; padding:10px; color:inherit; background:var(--uui-color-surface); border:1px solid var(--uui-color-border); border-radius:4px; }
        textarea { min-height:160px; font:13px monospace; } button { margin-top:8px; padding:6px 10px; }
        p { color:var(--uui-color-text-alt); } [role=alert] { color:var(--uui-color-danger); }
    `;
    constructor() {
        super(); this.connections = []; this.tables = [];
        this.consumeContext(UMB_AUTH_CONTEXT, auth => { this.auth = auth; if (auth) this.load(); });
    }
    get selection() {
        try { return JSON.parse(this.value || "null") ?? { kind: "table", connection: "AppBlocksDb", source: "" }; }
        catch { return { kind: "table", connection: "AppBlocksDb", source: "" }; }
    }
    change(selection) {
        if (this.readonly) return;
        this.value = JSON.stringify(selection);
        this.dispatchEvent(new UmbChangeEvent());
    }
    async load() {
        const version = this.requestVersion = (this.requestVersion ?? 0) + 1;
        this.loading = true; this.error = "";
        try {
            const connections = await request("connections");
            if (version !== this.requestVersion) return;
            this.connections = connections;
            const results = await Promise.allSettled(connections.map(async connection => {
                const tables = await request(`tables?connection=${encodeURIComponent(connection.name)}`);
                return tables.map(table => ({ connection: connection.name, source: table.value, label: `${connection.name}.${table.name}` }));
            }));
            if (version !== this.requestVersion) return;
            this.tables = results.flatMap(result => result.status === "fulfilled" ? result.value : [])
                .sort((a, b) => a.label.localeCompare(b.label));
            const failed = results.flatMap((result, index) => result.status === "rejected" ? [connections[index].name] : []);
            if (failed.length) this.error = `Could not read tables from ${failed.join(", ")}. Check those connections in Settings → AppBlocks, then refresh.`;
        } catch (error) { if (version === this.requestVersion) this.error = error.message; }
        finally { if (version === this.requestVersion) this.loading = false; }
    }
    render() {
        const selected = this.selection;
        const tableValue = table => JSON.stringify([table.connection, table.source]);
        const savedTable = selected.source && !this.tables.some(table => tableValue(table) === tableValue(selected));
        return html`
            <label for="kind">Source type</label>
            <select id="kind" .value=${selected.kind} ?disabled=${this.readonly} @change=${event => this.change({ kind: event.target.value, connection: selected.connection, source: "" })}>
                <option value="table" ?selected=${selected.kind === "table"}>Table</option>
                <option value="sql" ?selected=${selected.kind === "sql"}>SQL query</option>
            </select>
            ${selected.kind === "sql" ? html`
                <label for="connection">Data source</label>
                <select id="connection" .value=${selected.connection} ?disabled=${this.readonly} @change=${event => this.change({ ...selected, connection: event.target.value })}>
                    ${!this.connections.some(connection => connection.name === selected.connection) ? html`<option value=${selected.connection}>${selected.connection} (unavailable)</option>` : ""}
                    ${this.connections.map(connection => html`<option value=${connection.name} ?selected=${connection.name === selected.connection}>${connection.name}</option>`)}
                </select>
                <label for="sql">SQL query</label>
                <textarea id="sql" spellcheck="false" .value=${selected.source} ?readonly=${this.readonly} @input=${event => this.change({ ...this.selection, source: event.target.value })}></textarea>
                <p>Enter a SELECT query. Page parameters use :PARAM.name; signed-in user values include :USER_KEY and :USER_EMAIL.</p>
            ` : html`
                <label for="table">Table or view</label>
                <select id="table" .value=${selected.source ? tableValue(selected) : ""} ?disabled=${this.readonly || this.loading} @change=${event => {
                    const table = this.tables.find(table => tableValue(table) === event.target.value);
                    this.change({ kind: "table", connection: table?.connection ?? "AppBlocksDb", source: table?.source ?? "" });
                }}>
                    <option value="">${this.loading ? "Loading tables…" : "Choose DataSourceName.TableName"}</option>
                    ${savedTable ? html`<option value=${tableValue(selected)} selected>${selected.connection}.${selected.source} (unavailable)</option>` : ""}
                    ${this.tables.map(table => html`<option value=${tableValue(table)} ?selected=${tableValue(table) === tableValue(selected)}>${table.label}</option>`)}
                </select>
                ${!this.loading && !this.tables.length ? html`<p>Create your application tables in a configured data source, then refresh.</p>` : ""}
            `}
            <button type="button" ?disabled=${this.loading} @click=${this.load}>Refresh data sources</button>
            <p>Manage data sources in Settings → AppBlocks → Connections.</p>
            ${this.error ? html`<p role="alert">${this.error}</p>` : ""}`;
    }
}
customElements.define("appblocks-report-source", ReportSource);
