import assert from 'node:assert/strict';
import { test, before, after } from 'node:test';
import { createRequire } from 'node:module';
import { readFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../', import.meta.url));
const require = createRequire(resolve(process.env.INTERACTIVE_REPORTS_ROOT || resolve(root, '../Interactive-Reports'), 'package.json'));
const { chromium } = require('playwright');
const assets = resolve(process.env.UMBRACO_ASSETS || resolve(process.env.USERPROFILE || process.env.HOME, '.nuget/packages/umbraco.cms.staticassets/18.3.0/staticwebassets'), 'umbraco/backoffice');
let browser;
before(async () => { browser = await chromium.launch({ headless: true, ...(existsSync(chromium.executablePath()) ? {} : { executablePath: process.env.BROWSER_PATH || 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe' }) }); });
after(async () => browser?.close());

async function mount({ allowed = true, rejectSave = false } = {}) {
    const page = await browser.newPage();
    const errors = [], calls = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.route('**/*', async route => {
        const url = new URL(route.request().url()), path = url.pathname;
        const js = body => route.fulfill({ contentType: 'text/javascript', body });
        const json = body => route.fulfill({ contentType: 'application/json', body: JSON.stringify(body) });
        if (path === '/sub/admin') return route.fulfill({ contentType: 'text/html', body: `<script type="importmap">{"imports":{
            "@umbraco-cms/backoffice/lit-element":"/umb.js", "@umbraco-cms/backoffice/auth":"/umb.js", "@umbraco-cms/backoffice/components":"/components.js",
            "@umbraco-cms/backoffice/external/lit":"/vendor/lit/index.js"
        }}</script><script type="module" src="/sub/App_Plugins/AppBlocks.InteractiveReports/administration.js"></script><appblocks-report-administration></appblocks-report-administration>` });
        if (path.startsWith('/vendor/')) return js(await readFile(resolve(assets, 'external', path.slice('/vendor/'.length)), 'utf8'));
        if (path === '/umb.js') return js(`import {LitElement} from '/vendor/lit/index.js';
            export class UmbLitElement extends LitElement { consumeContext(token, callback) { queueMicrotask(() => callback({getLatestToken: async () => 'admin-token'})); } }
            export const UMB_AUTH_CONTEXT='auth';`);
        if (path === '/components.js') return js('');
        if (path.includes('/App_Plugins/')) {
            const pkg = path.includes('/AppBlocks.InteractiveReports/') ? 'AppBlocks.InteractiveReports' : 'AppBlocks';
            return js(await readFile(resolve(root, `src/${pkg}/wwwroot`, path.slice('/sub/'.length)), 'utf8'));
        }
        calls.push({ path, query: url.search, method: route.request().method(), body: route.request().postDataJSON(), authorization: route.request().headers().authorization });
        if (path.endsWith('/access')) return json({ allowed });
        if (path.endsWith('/sources')) return json([{ name: 'page-source', title: 'Tasks / Open tasks', culture: 'fr-CA' }]);
        // The engine has its own widget tests. Exercise the host's mount, state capture and transport contracts here.
        if (path.endsWith('/ir-admin.js')) return js(`
            customElements.define('interactive-report-admin', class extends HTMLElement { connectedCallback() { this.textContent = 'Saved report administration'; } });
            customElements.define('interactive-report', class extends HTMLElement { getReportDocument() { return {page:{index:1,size:25},tables:{}}; } });`);
        if (path.endsWith('/saved')) return route.fulfill({ status: rejectSave ? 409 : 201, contentType: 'application/json', body: JSON.stringify(rejectSave ? { description: 'A report with this title already exists.' } : { id: 1 }) });
        return route.fulfill({ status: 404 });
    });
    await page.goto('http://localhost/sub/admin');
    await page.waitForFunction(() => document.querySelector('appblocks-report-administration')?.loading === false);
    return { page, calls, errors };
}

test('public creation uses the backoffice session, virtual path, selected culture and configured view', async () => {
    const { page, calls, errors } = await mount();
    assert.equal(await page.locator('interactive-report-admin').getAttribute('api-base'), '/sub/umbraco/reports');
    await page.getByRole('button', { name: 'Create public report', exact: true }).click();
    await page.getByLabel('Report source').selectOption('0');
    assert.equal(await page.locator('interactive-report').getAttribute('api-base'), '/sub/umbraco/reports?appCulture=fr-CA');
    await page.getByLabel('Report title').fill('Open tasks for everyone');
    await page.getByRole('button', { name: 'Save public report', exact: true }).click();
    await page.getByRole('status').filter({ hasText: 'Public report' }).waitFor();
    const saved = calls.find(call => call.method === 'POST');
    assert.equal(saved.path, '/sub/umbraco/reports/page-source/saved');
    assert.equal(saved.query, '?appCulture=fr-CA');
    assert.deepEqual(saved.body, { title: 'Open tasks for everyone', state: { page: { index: 1, size: 25 }, tables: {} }, isGlobal: true });
    assert.equal(await page.locator('interactive-report-admin').count(), 1);
    assert.ok(calls.filter(call => call.path.includes('/management/')).every(call => call.authorization === 'Bearer admin-token'));
    assert.deepEqual(errors, []);
    await page.close();
});

test('a refused user never loads report data or the administration widget', async () => {
    const { page, calls, errors } = await mount({ allowed: false });
    await page.getByText('You need the Interactive Reports Administrator or Administrator group to manage reports.').waitFor();
    assert.equal(await page.locator('interactive-report-admin').count(), 0);
    assert.equal(calls.length, 1);
    assert.deepEqual(errors, []);
    await page.close();
});

test('a failed creation preserves the configured view and explains the server refusal', async () => {
    const { page, calls, errors } = await mount({ rejectSave: true });
    await page.getByRole('button', { name: 'Create public report', exact: true }).click();
    await page.getByLabel('Report source').selectOption('0');
    await page.getByLabel('Report title').fill('Existing title');
    await page.getByRole('button', { name: 'Save public report', exact: true }).click();
    await page.getByRole('alert').filter({ hasText: 'A report with this title already exists.' }).waitFor();
    assert.equal(await page.getByLabel('Report title').inputValue(), 'Existing title');
    assert.equal(await page.locator('interactive-report').count(), 1);
    assert.equal(calls.filter(call => call.method === 'POST').length, 1);
    assert.deepEqual(errors, []);
    await page.close();
});
