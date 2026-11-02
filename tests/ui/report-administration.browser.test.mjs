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

async function mount({ allowed = true, rejectSave = false, sourcesUnavailable = false } = {}) {
    const page = await browser.newPage();
    const errors = [], calls = [];
    let reports = [
        { id: '1', title: 'Private tasks', reportName: 'removed-page', owner: 'former-user', isPublic: false, readOnly: false, modifiedUtc: '2026-11-02T10:00:00Z' },
        { id: '2', title: 'Public tasks', reportName: 'page-source', owner: 'another-user', isPublic: true, readOnly: false, modifiedUtc: '2026-11-02T10:00:00Z' },
        { id: '3', title: 'Configured tasks', reportName: 'page-source', owner: null, isPublic: true, readOnly: true, modifiedUtc: '2026-11-02T10:00:00Z' },
    ];
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
        if (path.endsWith('/sources')) return sourcesUnavailable
            ? route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ detail: 'Report sources are unavailable.' }) })
            : json([{ name: 'page-source', title: 'Tasks / Open tasks', culture: 'fr-CA' }]);
        if (path.endsWith('/report-administration/reports')) return json(reports);
        if (path.endsWith('/admin/users')) return json({ items: [{ value: 'new-user', display: 'New owner' }], truncated: false });
        if (path.endsWith('/owner')) {
            const id = path.split('/').at(-2);
            reports = reports.map(report => report.id === id ? { ...report, owner: route.request().postDataJSON().owner } : report);
            return route.fulfill({ status: 204 });
        }
        if (route.request().method() === 'DELETE') {
            reports = reports.filter(report => report.id !== path.split('/').at(-1));
            return route.fulfill({ status: 204 });
        }
        // The engine has its own widget tests. Exercise the host's mount, state capture and transport contracts here.
        if (path.endsWith('/ir.js')) return js(`
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
    await page.getByRole('cell', { name: 'Private tasks removed-page' }).waitFor();
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
    assert.equal(await page.locator('table tbody tr').count(), 3);
    assert.ok(calls.filter(call => call.path.includes('/management/')).every(call => call.authorization === 'Bearer admin-token'));
    assert.deepEqual(errors, []);
    await page.close();
});

test('private and public reports can be reassigned and deleted without opening their source pages', async () => {
    const { page, calls, errors } = await mount();
    const configured = page.getByRole('row').filter({ hasText: 'Configured tasks' });
    assert.equal(await configured.getByRole('button').count(), 0);
    await configured.getByRole('cell', { name: 'Read-only' }).waitFor();
    await page.getByRole('row').filter({ hasText: 'Private tasks' }).getByRole('button', { name: 'Reassign' }).click();
    await page.getByLabel('New owner', { exact: true }).fill('new-user');
    await page.getByRole('button', { name: 'Save owner', exact: true }).click();
    await page.getByRole('status').filter({ hasText: 'reassigned' }).waitFor();
    const reassigned = calls.find(call => call.method === 'PUT');
    assert.equal(reassigned.path, '/sub/umbraco/management/api/v1/appblocks/report-administration/reports/1/owner');
    assert.deepEqual(reassigned.body, { owner: 'new-user', modifiedUtc: '2026-11-02T10:00:00Z' });
    assert.equal(reassigned.authorization, 'Bearer admin-token');
    await page.getByRole('row').filter({ hasText: 'Private tasks' }).getByRole('cell', { name: 'new-user', exact: true }).waitFor();
    page.once('dialog', dialog => dialog.dismiss());
    await page.getByRole('row').filter({ hasText: 'Public tasks' }).getByRole('button', { name: 'Delete', exact: true }).click();
    assert.equal(calls.filter(call => call.method === 'DELETE').length, 0);
    page.once('dialog', dialog => dialog.accept());
    await page.getByRole('row').filter({ hasText: 'Public tasks' }).getByRole('button', { name: 'Delete', exact: true }).click();
    await page.getByRole('status').filter({ hasText: 'deleted' }).waitFor();
    const deleted = calls.find(call => call.method === 'DELETE');
    assert.equal(deleted.path, '/sub/umbraco/management/api/v1/appblocks/report-administration/reports/2');
    assert.equal(new URLSearchParams(deleted.query).get('modifiedUtc'), '2026-11-02T10:00:00Z');
    assert.equal(deleted.authorization, 'Bearer admin-token');
    assert.ok(!calls.some(call => call.path.includes('removed-page')));
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

test('saved reports remain manageable when the source catalogue cannot load', async () => {
    const { page, calls, errors } = await mount({ sourcesUnavailable: true });
    await page.getByRole('alert').filter({ hasText: 'Report sources are unavailable.' }).waitFor();
    assert.equal(await page.getByRole('button', { name: 'Create public report', exact: true }).isDisabled(), true);
    assert.equal(await page.getByRole('button', { name: 'Reassign', exact: true }).count(), 2);
    assert.ok(!calls.some(call => call.path.endsWith('/ir.js')));
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
