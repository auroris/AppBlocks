import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import { html, css } from "@umbraco-cms/backoffice/external/lit";
import { UmbChangeEvent } from "@umbraco-cms/backoffice/event";
import { UMB_AUTH_CONTEXT } from "@umbraco-cms/backoffice/auth";
import { UMB_DOCUMENT_WORKSPACE_CONTEXT } from "@umbraco-cms/backoffice/document";
import { request } from "./api.js";

export default class TablePicker extends UmbLitElement {
    static properties = { value:{}, readonly:{type:Boolean}, tables:{state:true}, error:{state:true}, loading:{state:true} };
    static styles = css`select, textarea { box-sizing:border-box; width:100%; padding:10px; border:1px solid var(--uui-color-border); border-radius:4px; background:var(--uui-color-surface); color:inherit; } textarea { display:block; resize:vertical; font:inherit; } label { display:block; margin:12px 0 6px; } button { margin-top:8px; padding:6px 10px; } p { color:var(--uui-color-text-alt); }`;
    constructor() {
        super(); this.tables=[];
        this.consumeContext(UMB_AUTH_CONTEXT, auth => { this.auth=auth; if (auth) this.load(); });
        this.consumeContext(UMB_DOCUMENT_WORKSPACE_CONTEXT, workspace => {
            this.workspace=workspace;
            if (workspace?.data) this.observe(workspace.data, () => {
                const connection=this.connection(); if(connection!==this.loadedConnection && this.auth) this.load();
            });
        });
    }
    connection() { return this.workspace?.getData()?.values?.find(value => value.alias==="connection")?.value || "AppBlocksDb"; }
    async load() {
        const connection=this.connection(); const version=(this.requestVersion??0)+1; this.requestVersion=version;
        this.loading=true; this.error=undefined; this.loadedConnection=connection; this.tables=[];
        try { const tables=await request(`tables?connection=${encodeURIComponent(connection)}`); if(version===this.requestVersion) this.tables=tables; }
        catch(error) { if(version===this.requestVersion) this.error=error.message; }
        finally { if(version===this.requestVersion) this.loading=false; }
    }
    change(value) {
        if (this.readonly) return;
        this.value=value;
        this.dispatchEvent(new UmbChangeEvent());
    }
    render() {
        const selected=this.tables.some(table=>table.value===this.value)?this.value:"";
        return html`<select aria-label="Database table" .value=${selected} ?disabled=${this.readonly||this.loading} @change=${event => this.change(event.target.value)}>
            <option value="" ?selected=${!selected}>${this.loading?"Loading tables…":"Choose a table or view"}</option>
            ${this.tables.map(table=>html`<option value=${table.value} ?selected=${selected===table.value}>${table.name}</option>`)}</select>
            <button type="button" ?disabled=${this.loading} @click=${this.load}>Refresh tables</button>
            <label for="source">Table name or SELECT</label>
            <textarea id="source" rows="5" maxlength="20000" .value=${this.value??""} ?disabled=${this.readonly} @input=${event => this.change(event.target.value)}></textarea>
            <p>Choose a table above, or enter a table name or SELECT without ORDER BY. Up to 20,000 characters.</p>
            ${this.error?html`<p role="alert">${this.error}</p>`:!this.loading&&!this.tables.length?html`<p>Create your application tables in the selected database, then refresh this list.</p>`:""}`;
    }
}
customElements.define("appblocks-table-picker", TablePicker);
