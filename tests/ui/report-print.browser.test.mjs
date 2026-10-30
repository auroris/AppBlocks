import assert from "node:assert/strict";
import { test, before, after } from "node:test";
import { createRequire } from "node:module";
import { readFile, mkdir } from "node:fs/promises";
import { existsSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const root = fileURLToPath(new URL("../../", import.meta.url));
const reportsRoot = process.env.INTERACTIVE_REPORTS_ROOT || resolve(root, "../Interactive-Reports");
const require = createRequire(resolve(reportsRoot, "package.json"));
const { chromium } = require("playwright");
const { reportState, hydratedResult } = await import(pathToFileURL(resolve(reportsRoot, "tests/ui/report-state-fixture.js")));
let browser;
before(async () => {
    const edge = "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe";
    browser = await chromium.launch({ headless: true, ...(existsSync(chromium.executablePath()) ? {} : { executablePath: process.env.BROWSER_PATH || edge }) });
});
after(async () => browser?.close());

async function mount({ available = true, failure = false, fallback = false } = {}) {
    const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
    const requests = [], errors = [];
    page.on("pageerror", error => errors.push(error.message));
    const destinations = available ? [
        { id: "11111111-1111-1111-1111-111111111111", name: "Office", output: "printer", printer: "Office printer" },
        { id: "22222222-2222-2222-2222-222222222222", name: "PDF", output: "pdf", printer: null },
    ] : [];
    await page.route("**/*", async route => {
        const url = new URL(route.request().url());
        const path = url.pathname;
        const json = (value, status = 200) => route.fulfill({ status, contentType: "application/json", body: JSON.stringify(value) });
        if (path === "/sub/orders") return route.fulfill({ contentType: "text/html", headers: {
            "Content-Security-Policy": "default-src 'self'; script-src 'self' 'nonce-print-test'; style-src 'self' 'nonce-print-test'; frame-src 'self';",
        }, body: `<!doctype html><html><head><link rel="stylesheet" href="/sub/App_Plugins/ApplicationBlocks/report-print.css"></head><body>
            <template data-app-report data-state-key="test"><interactive-report report="orders" report-title="Orders" api-base="/sub/api/reports?appCulture=en" download-base="/sub/api/download?appCulture=en"></interactive-report></template>
            <script type="module" nonce="print-test" src="/sub/App_Plugins/ApplicationBlocks/reports.js"></script></body></html>` });
        if (path.includes("/App_Plugins/ApplicationBlocks/")) {
            const name = path.split("/").at(-1);
            return route.fulfill({ contentType: name.endsWith("css") ? "text/css" : "text/javascript", body: await readFile(resolve(root, "src/AppBlocks.InteractiveReports/wwwroot/App_Plugins/ApplicationBlocks", name), "utf8") });
        }
        if (path.endsWith("/ui/ir.js")) return route.fulfill({ contentType: "text/javascript", body: await readFile(resolve(reportsRoot, "src/InteractiveReport.Client.Json/Ui/dist/ir.js"), "utf8") });
        if (path.endsWith("/whoami")) return json({ identity: "reader" });
        if (path.endsWith("/schema")) return json({ title: "Orders", defaultState: reportState(), columns: [{ name: "ID", label: "ID", type: "number" }], features: ["download"], limits: { defaultPageSize: 25, maxPageSize: 100 }, capabilities: { aggregateFunctions: {}, expressionFunctions: [] } });
        if (path.endsWith("/default")) return json({ summary: null, result: hydratedResult() });
        if (path.endsWith("/orders")) return json([]);
        if (path.endsWith("/download/orders/html")) {
            requests.push({ action: "html", url: url.toString(), body: route.request().postDataJSON() });
            return route.fulfill({ contentType: "text/html; charset=utf-8", headers: { "Content-Disposition": 'attachment; filename="orders.html"' },
                body: "<!doctype html><html><head><title>Orders</title><style>table{width:100%}</style></head><body><h1>Orders</h1><p>All matching rows</p></body></html>" });
        }
        if (path.includes("/print/")) requests.push({ action: path.split("/").at(-1), url: url.toString(), body: route.request().postDataJSON(), headers: route.request().headers() });
        if (path.endsWith("/print/options")) return json({ destinations, antiforgeryToken: "csrf-test" });
        if (path.endsWith("/print/preview")) return json({ token: "preview-token", rows: 2, totalRows: 2, html: '<!doctype html><html><head><style>body{font:11px system-ui}table{width:100%;border-collapse:collapse}th,td{padding:8px;border-bottom:1px solid #ddd;text-align:left}</style></head><body><h1>Orders</h1><p>All matching rows · 2 of 2 rows</p><table><thead><tr><th>Order</th><th>Customer</th><th>Amount</th></tr></thead><tbody><tr><td>101</td><td>Northwind</td><td>$1,200.00</td></tr><tr><td>102</td><td>Contoso</td><td>$850.00</td></tr></tbody></table></body></html>' });
        if (path.endsWith("/print/submit")) {
            if (failure) return json({ title: "The printer may have received this job.", mayHavePrinted: true }, 502);
            if (fallback) return route.fulfill({ contentType: "text/html", headers: { "X-AppBlocks-Html-Fallback": "true" }, body: "<h1>Orders</h1>" });
            return json({ status: "submitted", printer: "Office printer", jobId: "job-123", pageCount: 2 });
        }
        return json({}, 404);
    });
    const options = page.waitForResponse(response => response.url().includes("/print/options"));
    await page.goto("http://app.test/sub/orders?customer=7"); await options;
    if (available) await page.waitForFunction(() => typeof document.querySelector("interactive-report")?.printHandler === "function");
    await page.getByRole("button", { name: "Actions", exact: true }).click();
    return { page, requests, errors };
}

test("no configured destinations means no Print menu item", async () => {
    const { page, errors } = await mount({ available: false });
    assert.equal(await page.getByText("Print…", { exact: true }).count(), 0);
    assert.deepEqual(errors, []); await page.close();
});

test("Download HTML works without print destinations and preserves the current report and page context", async () => {
    const { page, requests, errors } = await mount({ available: false });
    assert.equal(await page.getByText("Print…", { exact: true }).count(), 0);
    await page.getByText("Download", { exact: true }).click();
    assert.equal(await page.getByText("CSV", { exact: true }).count(), 1);
    const downloaded = page.waitForEvent("download");
    await page.getByText("HTML", { exact: true }).click();
    const file = await downloaded;
    assert.equal(file.suggestedFilename(), "orders.html");
    assert.match(await readFile(await file.path(), "utf8"), /<!doctype html>.*All matching rows/s);
    const request = requests.find(r => r.action === "html");
    assert.equal(new URL(request.url).searchParams.get("appCulture"), "en");
    assert.equal(new URL(request.url).searchParams.get("appPageQuery"), "?customer=7");
    assert.equal(request.body.activeTable, "base");
    assert.equal(request.body.page.size, 25);
    assert.equal(requests.filter(r => ["preview", "submit"].includes(r.action)).length, 0);
    assert.deepEqual(errors, []); await page.close();
});

test("dialog preserves virtual path and page context, previews safely, and submits once", async () => {
    const { page, requests, errors } = await mount();
    await page.getByText("Print…", { exact: true }).click();
    await page.getByText("2 of 2 rows ready.", { exact: true }).waitFor();
    assert.equal(await page.getByLabel("Rows", { exact: true }).inputValue(), "all");
    assert.equal(await page.getByLabel("Destination", { exact: true }).locator("option").count(), 2);
    const frame = page.frameLocator("iframe");
    assert.equal(await frame.locator("body").evaluate(node => getComputedStyle(node).fontSize), "11px");
    await page.getByLabel("Rows", { exact: true }).selectOption("page");
    await page.getByText("2 of 2 rows ready.", { exact: true }).waitFor();
    const preview = requests.filter(r => r.action === "preview").at(-1);
    assert.equal(new URL(preview.url).searchParams.get("appCulture"), "en");
    assert.equal(new URL(preview.url).searchParams.get("appPageQuery"), "?customer=7");
    assert.equal(preview.headers.requestverificationtoken, "csrf-test");
    assert.equal(preview.body.scope, "page");
    assert.equal(preview.body.document.page.size, 25);
    await mkdir(resolve(root, "artifacts"), { recursive: true });
    await page.screenshot({ path: resolve(root, "artifacts/report-print-preview.png") });
    await page.getByRole("button", { name: "Print", exact: true }).click();
    await page.getByText("Submitted to Office printer. Job job-123 · 2 pages.", { exact: true }).waitFor();
    assert.equal(await page.getByRole("button", { name: "Print", exact: true }).isDisabled(), true);
    assert.equal(requests.filter(r => r.action === "submit").length, 1);
    assert.deepEqual(errors, []); await page.getByRole("button", { name: "Close", exact: true }).click();
    await page.locator("dialog").waitFor({ state: "detached" });
    assert.equal(await page.locator("dialog").count(), 0); await page.close();
});

test("uncertain printer failure is visible and does not automatically retry", async () => {
    const { page, requests, errors } = await mount({ failure: true });
    await page.getByText("Print…", { exact: true }).click();
    await page.getByText("2 of 2 rows ready.", { exact: true }).waitFor();
    await page.getByRole("button", { name: "Print", exact: true }).click();
    await page.getByText(/Check the printer or queue before submitting again/).waitFor();
    assert.equal(await page.getByRole("button", { name: "Print", exact: true }).isDisabled(), true);
    assert.equal(requests.filter(r => r.action === "submit").length, 1);
    assert.deepEqual(errors, []); await page.close();
});

test("a PDF fallback is downloaded and identified as HTML", async () => {
    const { page, errors } = await mount({ fallback: true });
    await page.getByText("Print…", { exact: true }).click();
    await page.getByText("2 of 2 rows ready.", { exact: true }).waitFor();
    await page.getByLabel("Destination", { exact: true }).selectOption("22222222-2222-2222-2222-222222222222");
    await page.getByText("2 of 2 rows ready.", { exact: true }).waitFor();
    const download = page.waitForEvent("download");
    await page.getByRole("button", { name: "Download PDF", exact: true }).click();
    assert.equal((await download).suggestedFilename(), "report.html");
    await page.getByText("PDF generation was unavailable. Downloaded printable HTML instead.", { exact: true }).waitFor();
    assert.deepEqual(errors, []); await page.close();
});
