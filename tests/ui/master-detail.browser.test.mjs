import assert from 'node:assert/strict';
import { test, before, after } from 'node:test';
import { createRequire } from 'node:module';
import { readFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// A Tabular form block inside a form: its pending rows go with the form's own save, in one request, and it shows no save
// control of its own; on its own, or inside a form the caller cannot save, it saves itself as before.
const root = fileURLToPath(new URL('../../', import.meta.url));
const require = createRequire(resolve(process.env.INTERACTIVE_REPORTS_ROOT || resolve(root, '../Interactive-Reports'), 'package.json'));
const { chromium } = require('playwright');
const runtime = await readFile(resolve(root, 'src/AppBlocks/wwwroot/App_Plugins/ApplicationBlocks/runtime.js'), 'utf8');
const styles = await readFile(resolve(root, 'src/AppBlocks/wwwroot/App_Plugins/ApplicationBlocks/runtime.css'), 'utf8');
let browser;
before(async () => { browser = await chromium.launch({ headless: true, ...(existsSync(chromium.executablePath()) ? {} : { executablePath: process.env.BROWSER_PATH || 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe' }) }); });
after(async () => browser?.close());

const block = '0c1d2e3f-4a5b-4c6d-8e7f-901234567890';
const fields = [{ key: 'item', label: 'Item', kind: 'text', required: true, readOnly: false, maxLength: 50 }];
const list = (rows, owner = null) => ({
    owner: { alias: 'orders', key: owner }, dataSource: 'orders.lines', revision: 'tabular:v1', canAdd: true, canUpdate: true, canDelete: true, maxRows: 10, fields, rows,
    newRow: { key: null, version: null, values: { item: null }, capabilities: { create: true } }, antiforgeryToken: 'rows-token',
});
const row = (key, item) => ({ key, version: '1', values: { item }, capabilities: { update: true, delete: true } });

const tabular = owner => `<section class="app-tabular" data-app-tabular data-api="/api/tabular/page/${block}" data-block="${block}" data-owner="${owner}" data-key-parameter="id"
    data-read-only="false" data-max-rows="10" data-empty-message="No lines yet."><h3>Lines</h3>
    <div class="app-tabular-message" role="status" aria-live="polite"></div><div class="app-tabular-errors" role="alert" tabindex="-1" hidden></div>
    <table class="app-tabular-table"><thead><tr><th scope="col" data-column="item">Item *</th><th scope="col" class="app-tabular-actions-head">Actions</th></tr></thead><tbody aria-busy="true"></tbody></table>
    <div class="app-tabular-toolbar" hidden><button type="button" form="" data-tabular-add>Add line</button><button type="button" form="" data-tabular-save>Save lines</button></div></section>`;

/** A page with an order form and its lines, or the lines alone; the server is these routes, and every request is recorded. */
async function mount({ record = { key: null, version: null, values: { customer: '' }, capabilities: { create: true } }, inForm = true, answer } = {}) {
    const page = await browser.newPage({ viewport: { width: 1000, height: 700 } });
    const requests = [], errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.route('**/*', async route => {
        const request = route.request(), url = new URL(request.url()), path = url.pathname;
        if (path.endsWith('runtime.js')) return route.fulfill({ contentType: 'text/javascript', body: runtime });
        if (path.endsWith('runtime.css')) return route.fulfill({ contentType: 'text/css', body: styles });
        if (path.startsWith('/api/')) {
            requests.push({ method: request.method(), path, search: url.search, body: request.postDataJSON?.() ?? null });
            if (path === `/api/tabular/page/${block}/new`) return route.fulfill({ json: list([]) });
            if (path === `/api/tabular/page/${block}` && request.method() === 'GET') return route.fulfill({ json: list([row('1', 'Bolt')], url.searchParams.get('id')) });
            if (path === `/api/tabular/page/${block}`) return route.fulfill({ json: list([row('1', 'Bolt'), row('2', 'Saved alone')], 'page') });
            if (path === '/api/forms/page/form' && request.method() === 'GET') return route.fulfill({ json: { record, definitionVersion: 'v1', antiforgeryToken: 'form-token' } });
            return route.fulfill(answer(request.postDataJSON()));
        }
        return route.fulfill({ contentType: 'text/html; charset=utf-8', body: `<!doctype html><html><head><meta charset="utf-8"><title>Order</title>
            <link rel="stylesheet" href="/App_Plugins/ApplicationBlocks/runtime.css"></head><body><main class="application-blocks">
            ${inForm ? `<form class="app-form" data-app-form data-api="/api/forms/page/form" data-key-parameter="id" data-return="/" novalidate>
            <div class="app-form-message" role="status"></div><div class="app-form-errors" tabindex="-1" hidden></div>
            <fieldset class="app-form-body" disabled><label for="customer">Customer</label><input id="customer" name="customer" data-field="customer">
            ${tabular('form')}<div class="app-form-actions"><button type="submit" data-save>Save order</button></div></fieldset></form>` : tabular('page')}
            </main><script type="module" src="/App_Plugins/ApplicationBlocks/runtime.js"></script></body></html>` });
    });
    await page.goto('http://localhost/order/');
    return { page, requests, errors };
}

test('a new order takes lines that go with its own save, and the answer shows them saved', async () => {
    const { page, requests, errors } = await mount({
        answer: body => ({ json: {
            record: { key: '7', version: '1', values: body.values, capabilities: { update: true, delete: true } },
            details: [{ block, list: list([row('11', 'Bolt')], '7') }],
        } }),
    });
    try {
        await page.getByRole('button', { name: 'Add line' }).waitFor();
        assert.equal(await page.getByRole('button', { name: 'Save lines' }).isVisible(), false);
        await page.locator('#customer').fill('Initech');
        await page.getByRole('button', { name: 'Add line' }).click();
        await page.locator('tbody [data-column="item"] input').fill('Bolt');
        await page.getByRole('button', { name: 'Save order' }).click();
        await page.waitForFunction(() => document.querySelector('.app-form-message').textContent === 'Saved.');
        const saves = requests.filter(request => request.method === 'POST');
        assert.equal(saves.length, 1);
        assert.equal(saves[0].path, '/api/forms/page/form');
        const detail = saves[0].body.details[0];
        assert.equal(detail.block, block);
        assert.equal(detail.revision, 'tabular:v1');
        assert.match(detail.submissionKey, /^[0-9a-f-]{36}$/);
        assert.deepEqual(detail.changes, [{ operation: 'create', values: { item: 'Bolt' } }]);
        // The rows come from the answer, not from another request.
        await page.waitForFunction(() => document.querySelector('tbody tr[data-row-key="11"]'));
        assert.deepEqual(requests.filter(request => request.method === 'GET').map(request => request.path), ['/api/forms/page/form', `/api/tabular/page/${block}/new`]);
        assert.equal(new URL(page.url()).searchParams.get('id'), '7');
        assert.equal(await page.getByRole('button', { name: 'Save lines' }).isVisible(), false);

        // Nothing pending: the next save of the order sends no rows.
        await page.locator('#customer').fill('Initech Two');
        await page.getByRole('button', { name: 'Save order' }).click();
        await page.waitForFunction(() => document.querySelectorAll('tbody tr').length === 1 && document.querySelector('.app-form-message').textContent === 'Saved.');
        const second = requests.filter(request => request.method !== 'GET').at(-1);
        assert.equal(second.method, 'PUT');
        assert.equal(second.body.details, undefined);
        assert.deepEqual(errors, []);
    } finally { await page.close(); }
});

test('a refused line is marked where it is, every value stays, and the retry reuses its submission key', async () => {
    let attempts = 0;
    const { page, requests, errors } = await mount({
        answer: () => ++attempts === 1
            ? { status: 422, json: { status: 422, block, index: 1, errors: { item: ['Use a part number.'] } } }
            : { json: { record: { key: '7', version: '1', values: { customer: 'Initech' }, capabilities: { update: true } }, details: [{ block, list: list([row('11', 'Bolt'), row('12', 'Nut')], '7') }] } },
    });
    try {
        await page.getByRole('button', { name: 'Add line' }).waitFor();
        await page.locator('#customer').fill('Initech');
        for (const item of ['Bolt', 'Nut']) {
            await page.getByRole('button', { name: 'Add line' }).click();
            await page.locator('tbody [data-column="item"] input').last().fill(item);
        }
        await page.getByRole('button', { name: 'Save order' }).click();
        await page.locator('.app-tabular-errors').getByText('Row 2, Item: Use a part number.').waitFor();
        assert.equal(await page.locator('tbody tr').nth(1).getAttribute('class'), 'app-tabular-row--new app-tabular-row--invalid');
        assert.equal(await page.locator('tbody tr').nth(1).locator('input').getAttribute('aria-invalid'), 'true');
        assert.match(await page.locator('.app-form-errors').textContent(), /Nothing was saved/);
        assert.equal(await page.locator('tbody tr').nth(1).locator('input').inputValue(), 'Nut');
        await page.getByRole('button', { name: 'Save order' }).click();
        await page.waitForFunction(() => document.querySelector('.app-form-message').textContent === 'Saved.');
        const [first, retry] = requests.filter(request => request.method === 'POST').map(request => request.body);
        assert.equal(retry.submissionKey, first.submissionKey);
        assert.equal(retry.details[0].submissionKey, first.details[0].submissionKey);
        assert.deepEqual(errors, []);
    } finally { await page.close(); }
});

test('on its own, or in a form the caller cannot save, the block saves its rows itself', async () => {
    for (const options of [{ inForm: false }, { record: { key: '4', version: '1', values: { customer: 'Acme' }, capabilities: { update: false } } }]) {
        const { page, requests, errors } = await mount({ ...options, answer: () => ({ status: 500, json: {} }) });
        try {
            const save = page.getByRole('button', { name: 'Save lines' });
            await save.waitFor();
            await page.locator('tbody [data-column="item"] input').fill('Bolt M8');
            await save.click();
            await page.locator('.app-tabular-message').getByText('Saved.').waitFor();
            const posted = requests.filter(request => request.method === 'POST');
            assert.deepEqual(posted.map(request => request.path), [`/api/tabular/page/${block}`]);
            assert.deepEqual(posted[0].body.changes, [{ operation: 'update', key: '1', version: '1', values: { item: 'Bolt M8' } }]);
            assert.deepEqual(errors, []);
        } finally { await page.close(); }
    }
});
