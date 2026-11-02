import { backofficeRoute } from "./backoffice-harness.mjs";
import assert from "node:assert/strict";
import { test, before, after } from "node:test";
import { createRequire } from "node:module";
import { readFile } from "node:fs/promises";
import { existsSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../../", import.meta.url));
const require = createRequire(resolve(process.env.INTERACTIVE_REPORTS_ROOT || resolve(root, "../Interactive-Reports"), "package.json"));
const { chromium } = require("playwright");
const assets = process.env.UMBRACO_ASSETS || resolve(process.env.USERPROFILE || process.env.HOME, ".nuget/packages/umbraco.cms.staticassets/18.3.0/staticwebassets");
let browser;
before(async () => {
    browser = await chromium.launch({ headless: true, ...(existsSync(chromium.executablePath()) ? {} : { executablePath: process.env.BROWSER_PATH || "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe" }) });
});
after(async () => browser?.close());

const generated = "// A backing class, made by the AppBlocks C# wizard.\nnamespace SiteCode;\n\npublic sealed class OrdersRules : IDataSourceHandler\n{\n    public string Alias => \"orders.rules\";\n}\n";

/** The C# section and a backing-class picker on one page, against a management API that keeps the folder in memory. */
async function mount({ canEdit = true, files = [] } = {}) {
    const page = await browser.newPage();
    const errors = [], requests = [];
    const contents = new Map(files);
    const state = {
        canEdit, package: "Umbraco.Cms.DevelopmentMode.Backoffice", folder: "AppBlocks/Code",
        entries: files.map(([path]) => ({ path, isFolder: false, size: 10, modified: "2026-11-02T10:00:00Z" })),
        compile: { succeeded: true, attempted: null, generation: 0, compiled: null, diagnostics: [], classes: [] },
    };
    page.on("pageerror", error => errors.push(error.message));
    await page.route("**/*", async route => {
        if (await backofficeRoute(route, assets, "code-token")) return;
        const url = new URL(route.request().url());
        const path = url.pathname;
        const js = body => route.fulfill({ contentType: "text/javascript", body });
        const json = (body, status = 200) => route.fulfill({ status, contentType: "application/json", body: JSON.stringify(body) });
        if (path === "/sub/section") return route.fulfill({ contentType: "text/html", body: `<script type="importmap">{"imports":{ "@umbraco-cms/backoffice/http-client":"/http-client.js", "@umbraco-cms/backoffice/repository":"/repository.js",
            "@umbraco-cms/backoffice/lit-element":"/umb.js", "@umbraco-cms/backoffice/external/lit":"/lit.js", "@umbraco-cms/backoffice/event":"/umb.js", "@umbraco-cms/backoffice/auth":"/umb.js",
            "@umbraco-cms/backoffice/components":"/empty.js", "@umbraco-cms/backoffice/code-editor":"/code-editor.js"
        }}</script><script type="module">
            import "/sub/App_Plugins/AppBlocks/code.js";
            import "/sub/App_Plugins/ApplicationBlocks/backing-class-picker.js";
            window.picker = () => document.body.appendChild(document.createElement("appblocks-backing-class-picker"));
            window.picker();
            document.body.append(document.createElement("appblocks-code"));
        </script>` });
        if (path === "/lit.js") return js(await readFile(resolve(assets, "umbraco/backoffice/external/lit/index.js"), "utf8"));
        if (path === "/empty.js") return js("");
        // Monaco stands in as a labelled text area: the section only sets and reads its code.
        if (path === "/code-editor.js") return js(`import { LitElement, html } from "/lit.js";
            customElements.define("umb-code-editor", class extends LitElement {
                static properties = { code: {}, language: {}, label: {}, readonly: { type: Boolean } };
                render() { return html\`<textarea aria-label=\${this.label} data-language=\${this.language} .value=\${this.code ?? ""} ?readonly=\${this.readonly}
                    @input=\${event => { this.code = event.target.value; }}></textarea>\`; }
            });`);
        if (path === "/umb.js") return js(`import { LitElement } from "/lit.js";
            const auth = { isAuthorized: { subscribe(callback) { callback(true); } } };
            export class UmbLitElement extends LitElement { consumeContext(token, callback) { queueMicrotask(() => callback(auth)); } }
            export const UMB_AUTH_CONTEXT = "auth";
            export class UmbChangeEvent extends Event { constructor() { super("change", { bubbles: true }); } }`);
        if (path.includes("/App_Plugins/")) return js(await readFile(resolve(root, "src/AppBlocks/wwwroot", path.slice("/sub/".length)), "utf8"));
        const body = route.request().postData();
        requests.push({ path: path + url.search, method: route.request().method(), body: body ? JSON.parse(body) : undefined, authorization: route.request().headers().authorization });
        const api = path.replace("/sub/umbraco/management/api/v1/", "");
        if (api === "application-blocks/catalogue") return json({ formClasses: [], lists: [], resources: [],
            dataSourceHandlers: [{ alias: "site.rules" }, ...state.compile.classes.filter(c => c.extensionPoint === "IDataSourceHandler").map(c => ({ alias: c.alias }))] });
        if (api === "appblocks/code") return json(state);
        if (api === "appblocks/connections") return json([{ name: "Business", label: "Business", provider: "sqlServer" }]);
        if (api === "appblocks/tables") return json([{ name: "sales.Orders", value: '"sales"."Orders"' }]);
        if (api === "appblocks/code/wizard/groups") return json({ memberGroups: ["Buyers", "Crew"], userGroups: [{ alias: "editor", name: "Editors" }, { alias: "writer", name: "Writers" }] });
        if (api === "appblocks/code/wizard/suggest") return json(url.searchParams.get("recipe") === "backingClass" ? { className: "OrdersRules", alias: "orders.rules" } : { className: "OrdersForm", alias: "orders" });
        if (api === "appblocks/code/wizard/data-sources") return json([{ alias: "orders", name: "Orders", handler: null, fields: [] }]);
        if (api === "appblocks/code/wizard/preview") return json({ path: "Orders/OrdersRules.cs", content: generated });
        if (api === "appblocks/code/file" && route.request().method() === "GET") return json({ path: url.searchParams.get("path"), content: contents.get(url.searchParams.get("path")) });
        if (api === "appblocks/code/file/new") {
            const created = JSON.parse(body);
            contents.set(created.path, created.content);
            state.entries = [{ path: "Orders", isFolder: true, size: 0, modified: "2026-11-02T10:00:00Z" }, { path: created.path, isFolder: false, size: created.content.length, modified: "2026-11-02T10:00:00Z" }];
            state.compile = { succeeded: true, attempted: "2026-11-02T10:00:00Z", generation: 1, compiled: "2026-11-02T10:00:01Z",
                diagnostics: [{ severity: "warning", id: "CS0168", message: "The variable 'unused' is declared but never used", file: created.path, line: 12, column: 13, isError: false }],
                classes: [{ type: "SiteCode.OrdersRules", extensionPoint: "IDataSourceHandler", alias: "orders.rules", file: created.path, line: 4 }] };
            return json(state);
        }
        return json({ detail: "Not found" }, 404);
    });
    await page.goto("http://app.test/sub/section");
    await page.getByRole("heading", { name: "Compilation" }).waitFor();
    return { page, errors, requests };
}

test("the wizard creates a backing class, its messages show, and the backing-class picker offers it at once", async () => {
    const { page, errors, requests } = await mount();
    const firstPicker = page.locator("appblocks-backing-class-picker").first().getByLabel("Backing class");
    await firstPicker.locator("option", { hasText: "site.rules" }).waitFor({ state: "attached" });
    assert.deepEqual(await firstPicker.locator("option").allTextContents(), ["Select backing class", "site.rules"]);
    assert.match(await page.getByRole("navigation", { name: "C# files" }).textContent(), /No C# files yet/);

    await page.getByRole("button", { name: "New file" }).click();
    await page.getByRole("button", { name: "Use the wizard" }).click();
    await page.getByLabel("Connection").selectOption("Business");
    await page.getByLabel("Table or view").locator("option", { hasText: "sales.Orders" }).waitFor({ state: "attached" });
    await page.getByLabel("Table or view").selectOption({ label: "sales.Orders" });
    await page.getByRole("radio", { name: /^Backing class/ }).check();
    await page.getByLabel("Data source").locator("option", { hasText: "Orders (orders)" }).waitFor({ state: "attached" });
    assert.equal(await page.getByLabel("Class name").inputValue(), "OrdersRules");
    assert.equal(await page.getByLabel("Alias").inputValue(), "orders.rules");
    await page.getByRole("radio", { name: /^Specific groups/ }).check();
    await page.getByRole("group", { name: "Read Member groups" }).getByLabel("Buyers").check();
    await page.getByRole("group", { name: "Write User groups" }).getByLabel("Editors (editor)").check();
    await page.getByLabel("Folder").fill("Orders");
    await page.getByRole("button", { name: "Preview" }).click();
    const preview = page.getByLabel("Preview of the generated class");
    await preview.waitFor();
    assert.equal(await preview.inputValue(), generated);
    assert.deepEqual(requests.find(r => r.path.endsWith("/code/wizard/preview")).body, {
        connection: "Business", table: '"sales"."Orders"', recipe: "backingClass", access: "specificGroups", className: "OrdersRules", alias: "orders.rules", folder: "Orders",
        readMemberGroups: ["Buyers"], readUserGroups: [], writeMemberGroups: [], writeUserGroups: ["editor"], allowAnonymousWrites: false, dataSource: "orders",
    });

    await page.getByRole("button", { name: "Create file" }).click();
    await page.getByRole("status").filter({ hasText: "Created Orders/OrdersRules.cs." }).waitFor();
    assert.match(await page.getByRole("status").textContent(), /The folder compiled; 1 class in use\./);
    assert.deepEqual(requests.find(r => r.path.endsWith("/code/file/new")).body, { path: "Orders/OrdersRules.cs", content: generated });
    assert.equal(await page.getByLabel("C# source of Orders/OrdersRules.cs").inputValue(), generated);
    assert.equal(await page.getByRole("button", { name: "OrdersRules.cs", exact: true }).getAttribute("aria-current"), "true");

    const messages = page.getByRole("table", { name: "Compiler messages" });
    assert.match(await messages.textContent(), /warning.*Orders\/OrdersRules\.cs\(12,13\).*CS0168.*declared but never used/s);
    assert.match(await page.getByRole("table", { name: "Classes in use" }).textContent(), /SiteCode\.OrdersRules\s*IDataSourceHandler\s*orders\.rules/);

    // The catalogue cached for the first picker is dropped by the compile, so a picker opened now offers the compiled class.
    await page.evaluate(() => window.picker());
    const secondPicker = page.locator("appblocks-backing-class-picker").nth(1).getByLabel("Backing class");
    await secondPicker.locator("option", { hasText: "orders.rules" }).waitFor({ state: "attached" });
    assert.deepEqual(await secondPicker.locator("option").allTextContents(), ["Select backing class", "site.rules", "orders.rules"]);
    assert.equal(requests.filter(r => r.path.endsWith("/catalogue")).length, 2);
    assert.ok(requests.every(r => r.authorization === "Bearer code-token"));
    assert.deepEqual(errors, []);
    await page.close();
});

test("without a compiler the section lists and shows files but changes nothing", async () => {
    const { page, errors, requests } = await mount({ canEdit: false, files: [["Orders.cs", "namespace SiteCode;\n"]] });
    assert.match(await page.getByRole("note").textContent(), /Read-only\..*Umbraco\.Cms\.DevelopmentMode\.Backoffice/s);
    assert.ok(await page.getByRole("button", { name: "New file" }).isDisabled());
    assert.ok(await page.getByRole("button", { name: "New folder" }).isDisabled());
    await page.getByRole("button", { name: "Orders.cs", exact: true }).click();
    const source = page.getByLabel("C# source of Orders.cs");
    await source.waitFor();
    assert.equal(await source.inputValue(), "namespace SiteCode;\n");
    assert.equal(await source.getAttribute("readonly"), "");
    for (const name of ["Save", "Rename", "Delete"]) assert.ok(await page.getByRole("button", { name, exact: true }).isDisabled());
    assert.equal(await page.getByRole("button", { name: "Compile again" }).count(), 0);
    assert.ok(requests.every(r => r.method === "GET"));
    assert.deepEqual(errors, []);
    await page.close();
});
