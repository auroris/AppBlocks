import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import { html, css, unsafeHTML } from "@umbraco-cms/backoffice/external/lit";
import { Marked } from "@umbraco-cms/backoffice/external/marked";
import { DOMPurify } from "@umbraco-cms/backoffice/external/dompurify";
import { UMB_AUTH_CONTEXT } from "@umbraco-cms/backoffice/auth";
import "@umbraco-cms/backoffice/components";
import { request, download } from "./api.js";

export default class BusinessRulesHelp extends UmbLitElement {
    static properties = { loading: { state: true }, error: { state: true }, guide: { state: true }, sections: { state: true }, notice: { state: true } };
    static styles = css`
        :host { display:block; container-type:inline-size; color:var(--uui-color-text); }
        * { box-sizing:border-box; }
        .toolbar { display:flex; justify-content:space-between; align-items:center; flex-wrap:wrap; gap:12px; margin-bottom:24px; }
        .eyebrow { margin:0; font-size:12px; font-weight:700; color:var(--uui-color-text-alt); }
        .layout { display:grid; grid-template-columns:210px minmax(0,1fr); gap:40px; align-items:start; }
        nav { position:sticky; top:24px; border-left:2px solid var(--uui-color-border); padding-left:18px; }
        nav h2 { font-size:13px; margin:0 0 12px; }
        nav a { display:block; padding:7px 0; text-decoration:none; font-size:13px; line-height:1.5; }
        a { color:var(--uui-color-interactive); } a:hover { text-decoration:underline; }
        article { min-width:0; max-width:850px; line-height:1.75; overflow-wrap:anywhere; }
        article h2 { font-size:26px; line-height:1.3; margin:0 0 20px; }
        article h3 { font-size:20px; line-height:1.4; margin:36px 0 12px; padding-top:8px; border-top:1px solid var(--uui-color-border); scroll-margin-top:24px; }
        article p { margin:0 0 18px; } article li { margin:6px 0; }
        article table { width:100%; border-collapse:collapse; margin:20px 0; }
        article th, article td { padding:10px 12px; border-bottom:1px solid var(--uui-color-border); text-align:left; vertical-align:top; }
        code { font:0.9em/1.6 ui-monospace,SFMono-Regular,Consolas,monospace; }
        :not(pre) > code { padding:2px 5px; border-radius:3px; background:var(--uui-color-surface-alt); }
        .example { margin:22px 0; border:1px solid var(--uui-color-border); border-radius:var(--uui-border-radius,4px); overflow:hidden; }
        .code-toolbar { display:flex; align-items:center; justify-content:space-between; padding:8px 14px; background:var(--uui-color-surface-alt); font-size:12px; font-weight:600; }
        pre { margin:0; padding:20px; overflow:auto; background:var(--uui-color-surface); line-height:1.65; tab-size:4; }
        pre code { white-space:pre; overflow-wrap:normal; }
        button { border:1px solid var(--uui-color-border); border-radius:var(--uui-border-radius,4px); padding:8px 12px; background:var(--uui-color-surface); color:inherit; font:inherit; cursor:pointer; }
        button:hover { border-color:var(--uui-color-interactive); } button:disabled { opacity:.6; cursor:wait; }
        :focus-visible { outline:2px solid var(--uui-color-focus); outline-offset:3px; }
        .error { color:var(--uui-color-danger); } .notice { margin:0 0 16px; color:var(--uui-color-text-alt); }
        @container(max-width:800px) { .layout { grid-template-columns:minmax(0,1fr); gap:28px; } nav { position:static; } article h2 { font-size:23px; } pre { padding:14px; } }
    `;
    constructor() {
        super(); this.sections = []; this.loading = false;
        this.consumeContext(UMB_AUTH_CONTEXT, auth => {
            this.auth = auth;
            if (auth && !this.markdown && !this.loading) this.load();
        });
    }
    async load() {
        this.loading = true; this.error = undefined; this.notice = undefined;
        try {
            const markdown = await request("guide", undefined, true);
            const safe = DOMPurify.sanitize(new Marked().parse(markdown), { USE_PROFILES: { html: true } });
            const document = new DOMParser().parseFromString(safe, "text/html");
            const sections = [];
            document.querySelectorAll("h1, h2").forEach((heading, index) => {
                const replacement = document.createElement(heading.tagName === "H1" ? "h2" : "h3");
                replacement.id = `business-rules-${index}`;
                replacement.tabIndex = -1;
                replacement.textContent = heading.textContent;
                if (heading.tagName === "H2") sections.push({ id: replacement.id, title: heading.textContent });
                heading.replaceWith(replacement);
            });
            document.querySelectorAll("pre").forEach(pre => {
                pre.tabIndex = 0;
                const example = document.createElement("div"); example.className = "example";
                const toolbar = document.createElement("div"); toolbar.className = "code-toolbar";
                const label = document.createElement("span"); label.textContent = pre.querySelector(".language-csharp") ? "C#" : pre.querySelector(".language-json") ? "JSON" : "Code example";
                const copy = document.createElement("button"); copy.type = "button"; copy.dataset.copy = ""; copy.textContent = "Copy code";
                toolbar.append(label, copy); pre.replaceWith(example); example.append(toolbar, pre);
            });
            this.markdown = markdown; this.guide = document.body.innerHTML; this.sections = sections;
        } catch (error) { this.error = `The business rules guide could not be loaded. ${error.message}`; }
        finally { this.loading = false; }
    }
    jump(event, id) {
        event.preventDefault();
        event.stopPropagation(); // Keep the backoffice router from treating an in-page link as a workspace route.
        const heading = this.renderRoot.getElementById(id);
        heading?.scrollIntoView({ block: "start" });
        heading?.focus({ preventScroll: true });
    }
    async copy(event) {
        const button = event.target.closest("button[data-copy]");
        if (!button) return;
        try {
            await navigator.clipboard.writeText(button.closest(".example").querySelector("pre").textContent);
            this.renderRoot.querySelectorAll("button[data-copy]").forEach(copy => copy.textContent = "Copy code");
            button.textContent = "Copied";
            this.notice = "Code copied to clipboard.";
        } catch { this.notice = "Copy was unavailable. Select the code example and copy it manually."; }
    }
    render() {
        return html`<uui-box>
            <div class="toolbar"><p class="eyebrow">Help · Forms</p><button ?disabled=${!this.markdown || this.loading} @click=${() => download(this.markdown, "business-rules.md", "text/markdown")}>Download Markdown</button></div>
            ${this.loading ? html`<p role="status">Loading business rules…</p>` : this.error ? html`<div class="error" role="alert"><p>${this.error}</p><button @click=${this.load}>Retry</button></div>` : html`
                <p class="notice" role="status">${this.notice}</p>
                <div class="layout">
                    <nav aria-label="Business rules sections"><h2>On this page</h2>${this.sections.map(section => html`<a href=${"#" + section.id} @click=${event => this.jump(event, section.id)}>${section.title}</a>`)}</nav>
                    <article aria-label="Business rules for AppBlocks forms" @click=${this.copy}>${unsafeHTML(this.guide)}</article>
                </div>`}
        </uui-box>`;
    }
}
customElements.define("appblocks-business-rules", BusinessRulesHelp);
