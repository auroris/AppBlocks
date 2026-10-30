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

async function mount({ value = "", readonly = false, fail = false, form = false } = {}) {
    const page = await browser.newPage();
    const errors = [], requests = [];
    page.on("pageerror", error => errors.push(error.message));
    await page.route("**/*", async route => {
        const url = new URL(route.request().url());
        const path = url.pathname;
        const js = body => route.fulfill({ contentType: "text/javascript", body });
        const json = (body, status = 200) => route.fulfill({ status, contentType: "application/json", body: JSON.stringify(body) });
        if (path === "/sub/editor") return route.fulfill({ contentType: "text/html", body: `<script type="importmap">{"imports":{
            "@umbraco-cms/backoffice/lit-element":"/umb.js", "@umbraco-cms/backoffice/external/lit":"/lit.js",
            "@umbraco-cms/backoffice/event":"/umb.js", "@umbraco-cms/backoffice/auth":"/umb.js"
        }}</script><script type="module">
            import "/sub/App_Plugins/ApplicationBlocks/${form ? "form-class-picker" : "report-source"}.js";
            const editor = document.createElement("${form ? "appblocks-form-class-picker" : "appblocks-report-source"}");
            editor.value = ${JSON.stringify(value)}; editor.readonly = ${readonly};
            window.changes = []; editor.addEventListener("change", () => window.changes.push(editor.value));
            document.body.append(editor);
        </script>` });
        if (path === "/lit.js") return js(await readFile(resolve(assets, "umbraco/backoffice/external/lit/index.js"), "utf8"));
        if (path === "/umb.js") return js(`import {LitElement} from "/lit.js";
            export class UmbLitElement extends LitElement { consumeContext(token, callback) { queueMicrotask(() => callback({ getLatestToken: async () => "editor-token" })); } }
            export const UMB_AUTH_CONTEXT = "auth";
            export class UmbChangeEvent extends Event { constructor() { super("change", { bubbles: true }); } }`);
        if (path.includes("/App_Plugins/")) {
            const relative = path.slice("/sub/".length);
            const companion = resolve(root, "src/AppBlocks.InteractiveReports/wwwroot", relative);
            return js(await readFile(existsSync(companion) ? companion : resolve(root, "src/AppBlocks/wwwroot", relative), "utf8"));
        }
        requests.push({ path, headers: route.request().headers() });
        if (path.endsWith("/catalogue")) return json({ formClasses: [{ alias: "Orders" }], resources: [{ alias: "content-node", dataSource: { name: "Old source" } }] });
        if (path.endsWith("/connections")) return json([{ name: "AppBlocksDb" }, { name: "Business" }]);
        if (path.endsWith("/tables")) return url.searchParams.get("connection") === "Business"
            ? fail ? json({ detail: "Connection unavailable" }, 422) : json([{ name: "sales.Order.Items", value: '"sales"."Order.Items"' }])
            : json([{ name: "Tasks", value: '"Tasks"' }]);
        return json({}, 404);
    });
    await page.goto("http://app.test/sub/editor");
    if (form) await page.getByRole("option", { name: "Orders", exact: true }).waitFor({ state: "attached" });
    else await page.waitForFunction(() => document.querySelector("appblocks-report-source")?.loading === false);
    return { page, errors, requests };
}

test("table choices show DataSourceName.TableName and preserve quoted identifiers when saved and reopened", async () => {
    const { page, errors, requests } = await mount();
    const choice = page.getByLabel("Table or view");
    assert.deepEqual(await choice.locator("option").allTextContents(), ["Choose DataSourceName.TableName", "AppBlocksDb.Tasks", "Business.sales.Order.Items"]);
    await choice.selectOption({ label: "Business.sales.Order.Items" });
    const saved = await page.evaluate(() => window.changes.at(-1));
    assert.deepEqual(JSON.parse(saved), { kind: "table", connection: "Business", source: '"sales"."Order.Items"' });
    assert.ok(requests.every(request => request.path.startsWith("/sub/umbraco/management/") && request.headers.authorization === "Bearer editor-token"));
    assert.deepEqual(errors, []);
    await page.close();
    const reopened = await mount({ value: saved });
    assert.equal(await reopened.page.getByLabel("Table or view").inputValue(), JSON.stringify(["Business", '"sales"."Order.Items"']));
    assert.deepEqual(reopened.errors, []);
    await reopened.page.close();
});

test("SQL mode saves its chosen data source and query; changing mode clears the other source", async () => {
    const { page, errors } = await mount();
    await page.getByLabel("Source type").selectOption("sql");
    await page.getByLabel("Data source", { exact: true }).selectOption("Business");
    await page.getByLabel("SQL query", { exact: true }).fill("SELECT id FROM Orders WHERE owner = :USER_KEY");
    assert.deepEqual(JSON.parse(await page.evaluate(() => window.changes.at(-1))), { kind: "sql", connection: "Business", source: "SELECT id FROM Orders WHERE owner = :USER_KEY" });
    await page.getByLabel("Source type").selectOption("table");
    assert.equal(JSON.parse(await page.evaluate(() => window.changes.at(-1))).source, "");
    assert.deepEqual(errors, []); await page.close();
});

test("an unavailable connection leaves other tables usable and preserves a saved choice", async () => {
    const saved = JSON.stringify({ kind: "table", connection: "Business", source: '"Orders"' });
    const { page, errors } = await mount({ value: saved, fail: true });
    assert.match(await page.getByRole("alert").textContent(), /Could not read tables from Business/);
    assert.equal(await page.getByRole("option", { name: "AppBlocksDb.Tasks", exact: true }).count(), 1);
    assert.equal(await page.evaluate(() => document.querySelector("appblocks-report-source").value), saved);
    assert.deepEqual(await page.evaluate(() => window.changes), []);
    assert.deepEqual(errors, []); await page.close();
});

test("readonly SQL configuration cannot be changed", async () => {
    const saved = JSON.stringify({ kind: "sql", connection: "Business", source: "SELECT 1" });
    const { page, errors } = await mount({ value: saved, readonly: true });
    assert.equal(await page.getByLabel("Source type").isDisabled(), true);
    assert.equal(await page.getByLabel("Data source", { exact: true }).isDisabled(), true);
    assert.equal(await page.getByLabel("SQL query", { exact: true }).getAttribute("readonly"), "");
    await page.evaluate(() => document.querySelector("appblocks-report-source").change({ kind: "table", connection: "AppBlocksDb", source: "Tasks" }));
    assert.equal(await page.evaluate(() => document.querySelector("appblocks-report-source").value), saved);
    assert.deepEqual(errors, []); await page.close();
});

test("forms offer registered C# classes without content-node sources", async () => {
    const { page, errors } = await mount({ form: true });
    assert.deepEqual(await page.getByLabel("Form class").locator("option").allTextContents(), ["Select form class", "Orders"]);
    await page.getByLabel("Form class").selectOption("Orders");
    assert.equal(await page.evaluate(() => window.changes.at(-1)), "Orders");
    assert.deepEqual(errors, []); await page.close();
});
