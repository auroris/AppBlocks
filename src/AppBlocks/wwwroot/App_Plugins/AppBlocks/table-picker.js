import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import { html, css } from "@umbraco-cms/backoffice/external/lit";
import { UmbChangeEvent } from "@umbraco-cms/backoffice/event";
import { UMB_AUTH_CONTEXT } from "@umbraco-cms/backoffice/auth";
import { UMB_DOCUMENT_WORKSPACE_CONTEXT } from "@umbraco-cms/backoffice/document";
import { request } from "./api.js";

export default class TablePicker extends UmbLitElement {
    static properties = { value:{}, readonly:{type:Boolean}, tables:{state:true}, error:{state:true}, loading:{state:true} };
    static styles = css`select { width:100%; padding:10px; border:1px solid var(--uui-color-border); border-radius:4px; background:var(--uui-color-surface); color:inherit; } button { margin-top:8px; padding:6px 10px; } p { color:var(--uui-color-text-alt); }`;
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
        this.loading=true; this.error=undefined; this.loadedConnection=connection;
        try { const tables=await request(this.auth, `tables?connection=${encodeURIComponent(connection)}`); if(version===this.requestVersion) this.tables=tables; }
        catch(error) { if(version===this.requestVersion) this.error=error.message; }
        finally { if(version===this.requestVersion) this.loading=false; }
    }
    render() { return html`<select aria-label="Database table" .value=${this.value??""} ?disabled=${this.readonly||this.loading} @change=${event => { this.value=event.target.value; this.dispatchEvent(new UmbChangeEvent()); }}><option value="">${this.loading?"Loading tables…":"Choose a table or view"}</option>${this.value&&!this.tables.some(table=>table.value===this.value)?html`<option value=${this.value}>${this.value} (saved value)</option>`:""}${this.tables.map(table=>html`<option value=${table.value} ?selected=${this.value===table.value}>${table.name}</option>`)}</select><button type="button" ?disabled=${this.loading} @click=${this.load}>Refresh tables</button>${this.error?html`<p role="alert">${this.error}</p>`:!this.loading&&!this.tables.length?html`<p>Create your application tables in the selected database, then refresh this list.</p>`:""}`; }
}
customElements.define("appblocks-table-picker", TablePicker);
