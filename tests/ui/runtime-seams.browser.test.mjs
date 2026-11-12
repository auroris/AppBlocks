import assert from 'node:assert/strict';
import { runtimeAsset } from './runtime-assets.mjs';
import { test, before, after } from 'node:test';
import { createRequire } from 'node:module';
import { existsSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// What a site can change in the runtime without touching it, and the answers the runtime reads more generously: a page's
// strings and language, the blocks' API root and the Member's antiforgery helper, a new record's files sent ahead of its
// save, sign-in on 401, a delete answered with a body, the server's own words for a refusal, and the schedule's link rule.
const root = fileURLToPath(new URL('../../', import.meta.url));
const require = createRequire(resolve(process.env.INTERACTIVE_REPORTS_ROOT || resolve(root, '../Interactive-Reports'), 'package.json'));
const { chromium } = require('playwright');
let browser;
before(async () => { browser = await chromium.launch({ headless: true, ...(existsSync(chromium.executablePath()) ? {} : { executablePath: process.env.BROWSER_PATH || 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe' }) }); });
after(async () => browser?.close());

const json = (body, status = 200) => ({ status, contentType: 'application/json', body: JSON.stringify(body) });
const record = (key, values = { title: 'Report' }) => ({ key, checksum: key ? '1' : null, values, capabilities: { create: true, update: true, delete: true } });
const formAnswer = rec => json({ record: rec, definitionVersion: 'v1', antiforgeryToken: 'form-token' });
const files = (items, canWrite = true) => ({ items, canWrite, antiforgeryToken: 'files-token' });
const file = (id, size) => ({ id, fileName: `${id}.pdf`, size, purpose: '', label: '', notes: '', uploadedBy: 'ada', uploadedAt: '2026-11-08T14:30:00Z', revisions: [] });

const form = (inner, { anonymous = false } = {}) => `<form class="app-form" data-app-form data-api="/api/forms/page/form" data-key-parameter="id" data-return="/done"
    ${anonymous ? 'data-anonymous="true"' : ''} novalidate><div class="app-form-message" role="status"></div><div class="app-form-errors" role="alert" tabindex="-1" hidden></div>
    <fieldset class="app-form-body" disabled><label for="title">Title</label><input id="title" name="title" data-field="title">${inner}
    <div class="app-form-actions"><button type="submit" data-save>Save</button><button type="button" data-delete hidden>Delete</button></div></fieldset></form>`;
const attachments = (owner, extra = '') => `<section class="app-attachments" data-app-attachments data-api="/api/attachments/page/block" data-owner="${owner}"
    data-key-parameter="id" data-read-only="false" ${extra}><div class="app-attachments-message" role="status"></div>
    <div class="app-attachments-errors" role="alert" tabindex="-1" hidden></div><ul class="app-attachments-list"></ul>
    <button type="button" form="" data-attachment-add hidden><span class="app-attachments-progress" aria-hidden="true" hidden></span><span data-attachment-add-label>Upload</span></button>
    <dialog class="app-attachments-dialog" aria-label="Add files"><div class="app-attachments-dialog-header"><h3>Add files</h3><button type="button" form="" data-attachment-close>Close</button></div>
    <div class="app-attachments-upload-errors" role="alert" hidden></div>
    <div class="app-attachments-upload">
    <label><span>Label</span> <input type="text" form="" data-attachment-label></label><label><span>Notes</span> <input type="text" form="" data-attachment-notes></label>
    <p class="app-attachments-upload-help">Files upload as soon as you add them. These details apply to files added next.</p>
    <div class="app-attachments-dashboard" data-attachment-dashboard></div>
    <button type="button" form="" data-attachment-retry hidden>Retry failed uploads</button></div></dialog></section>`;
const tabular = `<section class="app-tabular" data-app-tabular data-api="/api/tabular/page/rows" data-block="rows" data-owner="page" data-key-parameter="id"
    data-read-only="false" data-max-rows="10"><div class="app-tabular-message" role="status"></div><div class="app-tabular-errors" role="alert" tabindex="-1" hidden></div>
    <table class="app-tabular-table"><thead><tr><th scope="col" class="app-tabular-actions-head">Actions</th></tr></thead><tbody></tbody></table>
    <div class="app-tabular-toolbar" hidden><button type="button" form="" data-tabular-add>Add row</button><button type="button" form="" data-tabular-save>Save rows</button></div></section>`;

/** A page with the given body and head under the path base, whose API is the answer function; every API request is recorded. */
async function mount(body, answer, { head = '', lang = 'en', base = '', timezoneId } = {}) {
    const page = await browser.newPage({ viewport: { width: 1000, height: 700 }, timezoneId });
    const requests = [], errors = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('dialog', dialog => dialog.accept());
    await page.route('**/*', async route => {
        const request = route.request(), url = new URL(request.url()), path = url.pathname;
        const asset = await runtimeAsset(path); if (asset) return route.fulfill(asset);
        if (path === `${base}/` || path === `${base}/done`) return route.fulfill({ contentType: 'text/html; charset=utf-8', body: `<!doctype html><html lang="${lang}"><head>
            <meta charset="utf-8"><title>Page</title>${head}<link rel="stylesheet" href="${base}/App_Plugins/ApplicationBlocks/runtime.css"></head>
            <body><main class="application-blocks">${path.endsWith('/done') ? '<p>Done</p>' : body}</main></body></html>` });
        const entry = { method: request.method(), path, search: url.search, headers: request.headers(), raw: request.postData(), body: request.headers()['content-type']?.includes('json') ? request.postDataJSON() : null };
        requests.push(entry);
        return route.fulfill(await answer(entry));
    });
    await page.goto(`http://localhost${base}/`);
    return { page, requests, errors };
}
const runtime = base => `<script type="module" src="${base}/App_Plugins/ApplicationBlocks/runtime.js"></script>`;

async function openUploader(page, index = 0) {
    const block = page.locator('[data-app-attachments]').nth(index);
    if (!await block.locator('dialog').evaluate(dialog => dialog.open)) await block.locator('[data-attachment-add]').click();
    await block.locator('.uppy-Dashboard').waitFor();
}
async function closeUploader(page) {
    const close = page.locator('dialog[open] [data-attachment-close]');
    if (await close.count()) await close.click();
}

test('an unchanged date-time keeps its seconds and offset, while an edit or clear uses the control', async () => {
    for (const [timezoneId, original, shown, typed, edited] of [
        ['UTC', '2026-11-09T14:30:45.678+00:00', '2026-11-09T14:30', '2026-11-09T10:45', '2026-11-09T10:45:00.000Z'],
        // The second 01:30 at the autumn clock change must not become the first 01:30. The change of 2024 is used because it
        // is settled: from 2026 the time-zone data keeps Edmonton at -06:00 all year, so a date there no longer has two 01:30s.
        ['America/Edmonton', '2024-11-03T01:30:45.678-07:00', '2024-11-03T01:30', '2024-11-09T10:45', '2024-11-09T17:45:00.000Z'],
    ]) {
        const { page, requests, errors } = await mount(form('<input id="when" type="datetime-local" data-field="when">') + runtime(''),
            ({ method, body }) => formAnswer(record('7', method === 'GET' ? { title: 'Report', when: original } : body.values)), { timezoneId });
        try {
            await page.locator('#title').fill('Changed title');
            assert.equal(await page.locator('#when').inputValue(), shown);
            await page.getByRole('button', { name: 'Save', exact: true }).click();
            await page.getByText('Saved.', { exact: true }).waitFor();
            assert.equal(requests.at(-1).body.values.when, original);

            await page.locator('#when').fill(typed);
            await page.getByRole('button', { name: 'Save', exact: true }).click();
            await page.getByText('Saved.', { exact: true }).waitFor();
            assert.equal(requests.at(-1).body.values.when, edited);

            await page.locator('#title').fill('Another title');
            await page.getByRole('button', { name: 'Save', exact: true }).click();
            await page.getByText('Saved.', { exact: true }).waitFor();
            assert.equal(requests.at(-1).body.values.when, edited, 'the saved answer becomes the new baseline');

            await page.locator('#when').fill('');
            await page.getByRole('button', { name: 'Save', exact: true }).click();
            await page.getByText('Saved.', { exact: true }).waitFor();
            assert.equal(requests.at(-1).body.values.when, null);
            assert.deepEqual(errors, []);
        } finally { await page.close(); }
    }
});

test('a new form preserves the precision of its date-time default and leaves an empty date-time null', async () => {
    const original = '2026-11-09T14:30:45.678+00:00';
    const { page, requests } = await mount(form(`<input type="datetime-local" data-field="when" data-default="${original}">
        <input type="datetime-local" data-field="empty">`) + runtime(''),
        ({ method, body }) => formAnswer(record(method === 'GET' ? null : '7', method === 'GET' ? { title: '', when: null, empty: null } : body.values)), { timezoneId: 'UTC' });
    try {
        await page.locator('#title').fill('New report');
        await page.getByRole('button', { name: 'Save', exact: true }).click();
        await page.getByText('Saved.', { exact: true }).waitFor();
        assert.deepEqual(requests.at(-1).body.values, { title: 'New report', when: original, empty: null });
    } finally { await page.close(); }
});

test('a page\'s strings replace the English by id, and sizes and dates follow its language', async () => {
    const strings = { saved: 'Enregistré.', sizeKilobytes: '{size} Ko', sizeMegabytes: '{size} Mo', noRows: 5, unknownId: 'ignored' };
    const { page, errors } = await mount(attachments('page') + runtime(''), ({ path }) => path.startsWith('/api/attachments')
        ? json(files([file('small', 1536), file('large', 1572864)])) : json({}, 404),
        { lang: 'fr-CA', head: `<script type="application/json" data-appblocks-strings>${JSON.stringify(strings)}</script>` });
    try {
        await page.locator('.app-attachment').nth(1).waitFor();
        const meta = await page.locator('.app-attachment-meta').allTextContents();
        assert.match(meta[0], /^2 Ko · ada, /);
        assert.match(meta[1], /^1,5 Mo · ada, /);
        assert.match(meta[0], /2026/);
        assert.equal(await page.getByRole('button', { name: 'Remove' }).count(), 2, 'an id the page leaves out keeps its English');
        assert.deepEqual(errors, []);
    } finally { await page.close(); }
});

test('a broken strings table or an unknown language leaves the blocks working in English', async () => {
    const { page, errors } = await mount(attachments('page') + runtime(''), () => json(files([])),
        { lang: 'not a language!', head: '<script type="application/json" data-appblocks-strings>{ not json</script>' });
    try {
        await page.getByText('No files yet.').waitFor();
        assert.deepEqual(errors, []);
    } finally { await page.close(); }
});

test('a delete answered with a body but no record goes back, as no content does', async () => {
    const { page, requests } = await mount(form('') + runtime(''), ({ method }) => method === 'GET' ? formAnswer(record('7')) : json({ record: null }));
    try {
        await page.getByRole('button', { name: 'Delete' }).click();
        await page.waitForURL('http://localhost/done');
        assert.equal(requests.at(-1).method, 'DELETE');
    } finally { await page.close(); }
});

test('a refusal without its own words shows the server\'s detail, and the statuses with words of their own keep them', async () => {
    let status = 422;
    const { page } = await mount(form('') + runtime(''), ({ method }) => method === 'GET' ? formAnswer(record('7'))
        : json(status === 422 ? { title: 'Unprocessable', detail: 'The form definition is not valid.' } : { title: 'Too Many Requests' }, status));
    try {
        await page.getByRole('button', { name: 'Save' }).waitFor({ state: 'visible' });
        await page.getByRole('button', { name: 'Save' }).click();
        await page.locator('.app-form-errors').getByText('The form definition is not valid.').waitFor();
        status = 429;
        await page.getByRole('button', { name: 'Save' }).click();
        await page.locator('.app-form-errors').getByText('That was too many tries. Wait a minute and try again.').waitFor();
    } finally { await page.close(); }
});

test('a 401 asks a signed-in caller whose session ended to sign in, on a form\'s save and on each block\'s load', async () => {
    const { page } = await mount(form('') + runtime(''), ({ method }) => method === 'GET' ? formAnswer(record('7')) : json({}, 401));
    try {
        await page.getByRole('button', { name: 'Save' }).waitFor({ state: 'visible' });
        await page.locator('#title').fill('Changed');
        await page.getByRole('button', { name: 'Save' }).click();
        await page.locator('.app-form-message').getByText('Sign in to use this form.').waitFor();
        assert.equal(await page.locator('#title').inputValue(), 'Changed');
    } finally { await page.close(); }
    const blocks = await mount(attachments('page') + tabular + runtime(''), () => json({}, 401));
    try {
        await blocks.page.getByRole('status').getByText('Sign in to see the files.').waitFor();
        await blocks.page.getByRole('status').getByText('Sign in to see the rows.').waitFor();
        assert.equal(await blocks.page.locator('[data-app-attachments], [data-app-tabular]').count(), 0);
    } finally { await blocks.page.close(); }
});

test('a tabular save refused for the whole batch shows the messages among its errors', async () => {
    const list = { owner: { alias: 'orders', key: 'page' }, revision: 'r1', canAdd: true, canUpdate: true, canDelete: true, maxRows: 10, antiforgeryToken: 'rows-token',
        fields: [{ key: 'item', label: 'Item', kind: 'text', required: false, readOnly: false }],
        rows: [], newRow: { key: null, checksum: null, values: { item: null }, capabilities: { create: true } } };
    const { page } = await mount(tabular + runtime(''), ({ method }) => method === 'GET' ? json(list) : json({ errors: { x: ["'x' is not a field of the data source."] } }, 400));
    try {
        await page.getByRole('button', { name: 'Add row' }).click();
        await page.locator('tbody input').fill('Bolt');
        await page.getByRole('button', { name: 'Save rows' }).click();
        await page.locator('.app-tabular-errors').getByText("'x' is not a field of the data source.").waitFor();
    } finally { await page.close(); }
});

test('tabular saves disable editing and row actions until success or failure, preserving deleted rows on failure', async () => {
    for (const succeeds of [true, false]) {
        let finishSave;
        const held = new Promise(resolve => { finishSave = resolve; });
        const row = (key, item) => ({ key, checksum: null, values: { item }, capabilities: { update: true, delete: true } });
        const list = { owner: { alias: 'orders', key: 'page' }, revision: 'r1', canAdd: true, canUpdate: true, canDelete: true, maxRows: 10,
            antiforgeryToken: 'rows-token', fields: [{ key: 'item', label: 'Item', kind: 'text', required: false, readOnly: false }],
            rows: [row('1', 'Original'), row('2', 'Remove me')], newRow: { key: null, values: { item: '' }, capabilities: { create: true } } };
        const { page, requests, errors } = await mount(tabular + runtime(''), async ({ method }) => {
            if (method === 'GET') return json(list);
            await held;
            return succeeds ? json({ ...list, rows: [row('1', 'Submitted')] }) : json({ index: 0, errors: { item: ['Try again.'] } }, 422);
        });
        try {
            await page.locator('tbody input').first().fill('Submitted');
            await page.getByRole('button', { name: 'Delete', exact: true }).nth(1).click();
            const sent = page.waitForRequest(request => request.method() === 'POST');
            await page.getByRole('button', { name: 'Save rows', exact: true }).click();
            await sent;
            for (const control of await page.locator('tbody input, tbody button, [data-tabular-add], [data-tabular-save]').all())
                assert.equal(await control.isDisabled(), true, 'nothing in the submitted table can change while saving');
            assert.equal(await page.locator('tbody input').first().inputValue(), 'Submitted');
            assert.equal(requests.at(-1).body.changes[0].values.item, 'Submitted');
            finishSave();
            await page.getByText(succeeds ? 'Saved.' : 'Row 1, Item: Try again.', { exact: true }).waitFor();
            assert.equal(await page.locator('tbody input').first().isEnabled(), true);
            assert.equal(await page.locator('[data-tabular-save]').isEnabled(), true);
            assert.equal(await page.locator('[data-tabular-add]').isEnabled(), true);
            if (!succeeds) {
                assert.equal(await page.locator('tbody input').nth(1).isDisabled(), true, 'a deleted row stays disabled');
                await page.getByRole('button', { name: 'Undo', exact: true }).click();
                assert.equal(await page.locator('tbody input').nth(1).isEnabled(), true);
            }
            assert.deepEqual(errors, []);
        } finally { finishSave(); await page.close(); }
    }
});

test('a new record\'s files go up under the form\'s submission key and join the record when the form saves it', async () => {
    let pending = [];
    const { page, requests, errors } = await mount(form(attachments('form', 'data-new-uploads="allowed"')) + runtime(''), ({ method, path, search }) => {
        if (path === '/api/forms/page/form') return method === 'GET' ? formAnswer(record(null, { title: '' })) : formAnswer(record('7'));
        if (/^\/api\/attachments\/page\/block\/new\/[\w-]+$/.test(path)) {
            if (method === 'POST') pending = [file('ahead', 2048)];
            return json(files(pending));
        }
        if (/\/new\/[\w-]+\/ahead$/.test(path) && method === 'DELETE') { pending = []; return { status: 204 }; }
        if (path.endsWith('/adopt')) return json(files([file('ahead', 2048)]));
        if (path === '/api/attachments/page/block' && search.includes('id=7')) return json(files([file('ahead', 2048)]));
        return json({}, 404);
    });
    try {
        await openUploader(page);
        await page.locator('.uppy-Dashboard-input').first().setInputFiles({ name: 'ahead.pdf', mimeType: 'application/pdf', buffer: Buffer.from('%PDF-1.4') });
        await page.locator('.app-attachment').waitFor();
        assert.match(await page.locator('a.app-attachment-name').getAttribute('href'), /\/new\/[^/]+\/ahead$/);
        assert.equal(await page.getByRole('button', { name: 'Replace' }).count(), 0);
        await closeUploader(page);
        await page.locator('#title').fill('Report');
        await page.getByRole('button', { name: 'Save' }).click();
        await page.waitForFunction(() => document.querySelector('a.app-attachment-name')?.href.includes('id=7'));
        const save = requests.find(r => r.method === 'POST' && r.path === '/api/forms/page/form');
        const upload = requests.find(r => r.method === 'POST' && r.path.includes('/new/'));
        const adopt = requests.find(r => r.path.endsWith('/adopt'));
        assert.equal(upload.path, `/api/attachments/page/block/new/${save.body.submissionKey}`);
        assert.equal(upload.headers.requestverificationtoken, 'files-token');
        assert.equal(adopt.path, `/api/attachments/page/block/new/${save.body.submissionKey}/adopt`);
        assert.deepEqual(adopt.body, { key: '7' });
        assert.ok(requests.indexOf(adopt) < requests.findIndex(r => r.path === '/api/attachments/page/block' && r.search.includes('id=7')), 'the record\'s list is read after its files joined it');
        assert.deepEqual(errors, []);
    } finally { await page.close(); }
});

test('without the server\'s leave a new record\'s files wait for its save, as before', async () => {
    for (const extra of ['data-new-uploads="save-first"', '']) {
        const { page, requests } = await mount(form(attachments('form', extra)) + runtime(''), ({ path }) => path === '/api/forms/page/form' ? formAnswer(record(null, { title: '' })) : json({}, 404));
        try {
            await page.getByText('Save the record before adding files.').waitFor();
            assert.equal(requests.filter(r => r.path.startsWith('/api/attachments')).length, 0);
        } finally { await page.close(); }
    }
});

test('files upload immediately, drain the queue in the background, and the progress button reopens the same dialog', async () => {
    let finishFirst, finishSecond;
    const first = new Promise(resolve => { finishFirst = resolve; });
    const second = new Promise(resolve => { finishSecond = resolve; });
    const stored = [];
    const { page, requests, errors } = await mount(form(attachments('form', 'data-new-uploads="allowed"')) + runtime(''), async ({ method, path, raw }) => {
        if (path.includes('/forms/')) return formAnswer(record(method === 'GET' ? null : '7'));
        if (path.endsWith('/adopt')) return json(files(stored));
        if (method === 'POST') {
            const name = raw.includes('first.pdf') ? 'first' : 'second';
            await (name === 'first' ? first : second);
            const item = file(name, 1000); stored.push(item); return json(item, 201);
        }
        return json(files(stored));
    }, { head: '<script>const send = XMLHttpRequest.prototype.send; window.uploadRequests = []; XMLHttpRequest.prototype.send = function(...args) { window.uploadRequests.push(this); return send.apply(this, args); };</script>' });
    try {
        await page.getByRole('button', { name: 'Upload', exact: true }).click();
        await page.locator('.uppy-Dashboard').waitFor();
        const sentFirst = page.waitForRequest(r => r.method() === 'POST' && r.url().includes('/attachments/'));
        await page.locator('.uppy-Dashboard-input').first().setInputFiles({ name: 'first.pdf', mimeType: 'application/pdf', buffer: Buffer.alloc(1000) });
        await sentFirst;
        await closeUploader(page);
        const button = page.locator('[data-attachment-add]');
        assert.equal(await button.isEnabled(), true);
        assert.equal(await button.locator('.app-attachments-progress').isVisible(), true);
        await page.evaluate(() => window.uploadRequests[0].upload.dispatchEvent(new ProgressEvent('progress', { lengthComputable: true, loaded: 500, total: 1000 })));
        await page.waitForFunction(() => document.querySelector('[data-attachment-add-label]').textContent === 'Uploading 50%');
        assert.equal(await button.locator('.app-attachments-progress').evaluate(circle => circle.style.getPropertyValue('--upload-progress')), '50%');
        const warning = page.waitForEvent('dialog');
        await page.getByRole('button', { name: 'Save', exact: true }).click();
        assert.match((await warning).message(), /Wait for that to finish/);
        assert.equal(requests.filter(r => r.path.includes('/forms/') && r.method !== 'GET').length, 0);
        await button.click();
        await page.locator('.uppy-DashboardContent-addMore').click();
        await page.locator('.uppy-Dashboard-input').first().setInputFiles({ name: 'second.pdf', mimeType: 'application/pdf', buffer: Buffer.alloc(1000) });
        assert.equal(await page.locator('.uppy-Dashboard-Item').count(), 2);
        await closeUploader(page);
        const sentSecond = page.waitForRequest(r => r.method() === 'POST' && r.postData().includes('second.pdf'));
        finishFirst(); await sentSecond;
        assert.equal(await page.locator('dialog').isVisible(), false);
        assert.equal(await button.locator('.app-attachments-progress').isVisible(), true);
        finishSecond();
        await page.locator('.app-attachment').nth(1).waitFor();
        await page.waitForFunction(() => document.querySelector('[data-attachment-add-label]').textContent === 'Upload');
        assert.equal(await button.locator('.app-attachments-progress').isVisible(), false);
        assert.equal(requests.filter(r => r.path.includes('/forms/') && r.method !== 'GET').length, 0, 'completion never submits the form automatically');
        await page.getByRole('button', { name: 'Save', exact: true }).click();
        await page.waitForFunction(() => document.querySelector('a.app-attachment-name')?.href.includes('id=7'));
        assert.equal(requests.filter(r => r.path.endsWith('/adopt')).length, 1);
        assert.deepEqual(errors, []);
    } finally { finishFirst(); finishSecond(); await page.close(); }
});

test('immediate uploads snapshot metadata and antiforgery, new files do not retry failures, and explicit retries preserve metadata', async () => {
    let reject = true;
    const stored = [];
    const { page, requests, errors } = await mount(form(attachments('form')) + runtime(''), ({ method, path, raw }) => {
        if (path.includes('/forms/')) return formAnswer(record('7'));
        if (method === 'POST') {
            const name = raw.includes('second.pdf') ? 'second' : raw.includes('third.pdf') ? 'third' : 'first';
            if (name === 'second' && reject) return json({ detail: 'The document was refused.' }, 503);
            const item = file(name, 8); stored.push(item); return json(item, 201);
        }
        return json(files(stored));
    });
    try {
        await page.evaluate(() => { window.changed = []; document.addEventListener('app-attachments:changed', event => window.changed.push(event.detail)); });
        await openUploader(page);
        await page.locator('[data-attachment-label]').fill('Evidence');
        await page.locator('[data-attachment-notes]').fill('For review');
        await page.locator('.uppy-Dashboard-input').first().setInputFiles(['first', 'second'].map(name => ({ name: name + '.pdf', mimeType: 'application/pdf', buffer: Buffer.from(name) })));
        await page.getByRole('button', { name: 'Retry failed uploads', exact: true }).waitFor();
        await page.waitForFunction(() => !document.querySelector('[data-attachment-retry]').disabled);
        const uploads = () => requests.filter(r => r.method === 'POST' && r.path.includes('/attachments/'));
        assert.equal(uploads().length, 2);
        for (const request of uploads()) {
            assert.equal(request.headers.requestverificationtoken, 'files-token');
            assert.match(request.raw, /name="label"\r\n\r\nEvidence/);
            assert.match(request.raw, /name="notes"\r\n\r\nFor review/);
            assert.ok(request.search.includes('id=7'));
        }
        await page.locator('[data-attachment-label]').fill('New files');
        await page.locator('.uppy-DashboardContent-addMore').click();
        await page.locator('.uppy-Dashboard-input').first().setInputFiles({ name: 'third.pdf', mimeType: 'application/pdf', buffer: Buffer.from('third') });
        await page.locator('.app-attachment', { hasText: 'third.pdf' }).waitFor();
        await page.waitForFunction(() => !document.querySelector('[data-attachment-retry]').disabled);
        assert.equal(uploads().length, 3, 'adding a new file does not retry the failed file');
        assert.match(uploads()[2].raw, /name="label"\r\n\r\nNew files/);
        await closeUploader(page);
        const button = page.locator('[data-attachment-add]');
        assert.equal(await button.textContent(), '!Upload failed');
        assert.equal(await button.locator('.app-attachments-progress').getAttribute('data-state'), 'failed');
        const warning = page.waitForEvent('dialog');
        await page.getByRole('button', { name: 'Save', exact: true }).click();
        assert.match((await warning).message(), /Finish uploading or remove them/);
        await button.click();
        await page.locator('.app-attachments-upload-errors').getByText('The document was refused.').waitFor();
        assert.equal(await page.locator('.uppy-Dashboard-Item').count(), 1);
        reject = false;
        await page.getByRole('button', { name: 'Retry failed uploads', exact: true }).click();
        await page.locator('.app-attachment').nth(2).waitFor();
        await closeUploader(page);
        assert.equal(uploads().length, 4);
        assert.match(uploads()[3].raw, /second.pdf/);
        assert.match(uploads()[3].raw, /name="label"\r\n\r\nEvidence/, 'retry keeps the metadata captured when the file was added');
        assert.equal(await button.textContent(), 'Upload');
        assert.equal(await button.locator('.app-attachments-progress').isVisible(), false);
        assert.deepEqual(await page.evaluate(() => window.changed), Array.from({ length: 3 }, () => ({ id: null, operation: 'upload' })));
        await page.getByRole('button', { name: 'Save', exact: true }).click();
        await page.getByText('Saved.', { exact: true }).waitFor();
        assert.deepEqual(errors, []);
    } finally { await page.close(); }
});

test('removing a failed file from either attachment block clears its save guard', async () => {
    const blocks = attachments('form', 'data-new-uploads="allowed"') + attachments('form', 'data-new-uploads="allowed"').replace('/page/block', '/page/other');
    const { page, requests, errors } = await mount(form(blocks) + runtime(''), ({ path, method }) => path.includes('/forms/')
        ? formAnswer(record(method === 'GET' ? null : '7')) : method === 'POST' ? json({ detail: 'Refused.' }, 422) : json(files([])));
    try {
        await openUploader(page, 1);
        await page.locator('[data-app-attachments]').nth(1).locator('.uppy-Dashboard-input').first()
            .setInputFiles({ name: 'queued.pdf', mimeType: 'application/pdf', buffer: Buffer.from('file') });
        await page.waitForFunction(() => document.querySelectorAll('[data-attachment-add-label]')[1].textContent === 'Upload failed');
        const dialog = page.waitForEvent('dialog');
        await closeUploader(page);
        await page.getByRole('button', { name: 'Save', exact: true }).click();
        await dialog;
        assert.equal(requests.filter(r => r.method === 'POST').length, 1);
        await openUploader(page, 1);
        await page.locator('.uppy-Dashboard-Item-action--remove').click();
        await closeUploader(page);
        await page.getByRole('button', { name: 'Save', exact: true }).click();
        await page.getByText('Saved.', { exact: true }).waitFor();
        assert.equal(requests.filter(r => r.method === 'POST').length, 2);
        assert.deepEqual(errors, []);
    } finally { await page.close(); }
});

test('the attachment list renames and removes files without loading Uppy or showing revisions', async () => {
    let current = { ...file('old', 8), label: 'Keep this label', revisions: [file('older', 4)] };
    const { page, requests, errors } = await mount(attachments('page') + runtime(''), ({ method, body }) => {
        if (method === 'PATCH') { current = { ...current, fileName: body.fileName }; return json(current); }
        if (method === 'DELETE') { current = null; return { status: 204 }; }
        return json(files(current ? [current] : []));
    });
    try {
        await page.locator('.app-attachment').waitFor();
        assert.equal(await page.locator('.uppy-Dashboard').count(), 0);
        assert.equal(await page.locator('dialog').isVisible(), false);
        assert.equal(await page.getByRole('button', { name: 'Replace' }).count(), 0);
        assert.equal(await page.locator('.app-attachment-revisions').count(), 0);
        assert.match(await page.locator('a.app-attachment-name').getAttribute('href'), /\/old$/);
        await page.evaluate(() => { window.fileChanges = []; document.addEventListener('app-attachments:changed', event => window.fileChanges.push(event.detail)); });
        page.removeAllListeners('dialog');
        let name = null;
        page.on('dialog', dialog => dialog.type() === 'prompt' ? name === null ? dialog.dismiss() : dialog.accept(name) : dialog.accept());
        await page.getByRole('button', { name: 'Rename', exact: true }).click();
        name = 'old.pdf';
        await page.getByRole('button', { name: 'Rename', exact: true }).click();
        assert.equal(requests.filter(r => r.method === 'PATCH').length, 0, 'cancel and unchanged names do nothing');
        name = '  renamed.pdf  ';
        await page.getByRole('button', { name: 'Rename', exact: true }).click();
        await page.getByRole('link', { name: 'renamed.pdf', exact: true }).waitFor();
        const renamed = requests.find(r => r.method === 'PATCH');
        assert.equal(renamed.path, '/api/attachments/page/block/old');
        assert.deepEqual(renamed.body, { fileName: 'renamed.pdf' });
        assert.equal(renamed.headers.requestverificationtoken, 'files-token');
        assert.match(await page.locator('.app-attachment-meta').textContent(), /Keep this label/);
        await page.getByRole('button', { name: 'Remove', exact: true }).click();
        await page.getByText('No files yet.').waitFor();
        assert.deepEqual(await page.evaluate(() => window.fileChanges), [{ id: 'old', operation: 'rename' }, { id: 'old', operation: 'remove' }]);
        assert.deepEqual(errors, []);
    } finally { await page.close(); }
});

test('pending files can be renamed and a refused rename preserves the file and blocks Save only while working', async () => {
    let finishRename;
    const held = new Promise(resolve => { finishRename = resolve; });
    const { page, requests, errors } = await mount(form(attachments('form', 'data-new-uploads="allowed"')) + runtime(''), async ({ method, path }) => {
        if (path.includes('/forms/')) return formAnswer(record(null));
        if (method === 'PATCH') { await held; return json({ detail: 'Use an accepted filename.' }, 422); }
        return json(files([file('pending', 8)]));
    });
    try {
        await page.locator('.app-attachment').waitFor();
        await page.evaluate(() => { window.prompt = () => 'wrong.exe'; });
        const sent = page.waitForRequest(request => request.method() === 'PATCH');
        await page.getByRole('button', { name: 'Rename', exact: true }).click();
        await sent;
        assert.equal(await page.locator('[data-attachment-add]').isDisabled(), true);
        const warning = page.waitForEvent('dialog');
        await page.getByRole('button', { name: 'Save', exact: true }).click();
        assert.match((await warning).message(), /Wait for that to finish/);
        assert.equal(requests.filter(request => request.path.includes('/forms/') && request.method !== 'GET').length, 0);
        const rename = requests.find(request => request.method === 'PATCH');
        assert.match(rename.path, /\/new\/[^/]+\/pending$/);
        assert.deepEqual(rename.body, { fileName: 'wrong.exe' });
        finishRename();
        await page.locator('.app-attachments-errors').getByText('Use an accepted filename.').waitFor();
        assert.equal(await page.getByRole('link', { name: 'pending.pdf' }).count(), 1);
        assert.equal(await page.getByRole('button', { name: 'Rename', exact: true }).isEnabled(), true);
        assert.deepEqual(errors, []);
    } finally { finishRename(); await page.close(); }
});

test('the upload dialog traps focus, Escape keeps the queue, and Enter in metadata does not submit the form', async () => {
    const { page, requests, errors } = await mount(form(attachments('form', 'data-new-uploads="allowed"')) + runtime(''), ({ path, method }) =>
        path.includes('/forms/') ? formAnswer(record(null)) : method === 'POST' ? json({ detail: 'Refused.' }, 422) : json(files([])));
    try {
        await openUploader(page);
        await page.locator('[data-attachment-label]').fill('Evidence');
        await page.locator('[data-attachment-label]').press('Enter');
        assert.equal(requests.filter(request => request.method !== 'GET').length, 0);
        await page.locator('.uppy-Dashboard-input').first().setInputFiles({ name: 'queued.pdf', mimeType: 'application/pdf', buffer: Buffer.from('file') });
        await page.waitForFunction(() => document.querySelector('[data-attachment-add-label]').textContent === 'Upload failed');
        await page.locator('[data-attachment-label]').focus();
        await page.keyboard.press('Tab');
        assert.equal(await page.evaluate(() => !!document.activeElement.closest('dialog')), true);
        await page.keyboard.press('Escape');
        assert.equal(await page.locator('dialog').isVisible(), false);
        assert.equal(await page.locator('[data-attachment-add]').evaluate(button => button === document.activeElement), true);
        await openUploader(page);
        assert.equal(await page.locator('.uppy-Dashboard-Item').count(), 1);
        assert.equal(await page.locator('[data-attachment-label]').inputValue(), 'Evidence');
        assert.deepEqual(errors, []);
    } finally { await page.close(); }
});

test('attachments cannot queue a new upload while the form save is in progress', async () => {
    let finishSave;
    const held = new Promise(resolve => { finishSave = resolve; });
    const { page, requests, errors } = await mount(form(attachments('form', 'data-new-uploads="allowed"')) + runtime(''), async ({ path, method }) => {
        if (path.includes('/forms/')) {
            if (method !== 'GET') await held;
            return formAnswer(record(method === 'GET' ? null : '7'));
        }
        return json(files([]));
    });
    try {
        await openUploader(page);
        await closeUploader(page);
        const sent = page.waitForRequest(r => r.method() === 'POST' && r.url().includes('/forms/'));
        await page.getByRole('button', { name: 'Save', exact: true }).click();
        await sent;
        assert.equal(await page.locator('[data-app-attachments]').evaluate(root => root.inert), true);
        const document = { name: 'later.pdf', mimeType: 'application/pdf', buffer: Buffer.from('file') };
        assert.equal(await page.locator('[data-attachment-add]').isDisabled(), true);
        await page.locator('.uppy-Dashboard-input').first().setInputFiles(document);
        assert.equal(await page.locator('.uppy-Dashboard-Item').count(), 0);
        assert.equal(requests.filter(r => r.path.includes('/attachments/') && r.method !== 'GET').length, 0);
        finishSave();
        await page.waitForFunction(() => !document.querySelector('[data-attachment-add]').disabled);
        await openUploader(page);
        const sentFile = page.waitForRequest(r => r.method() === 'POST' && r.url().includes('/attachments/'));
        await page.locator('.uppy-Dashboard-input').first().setInputFiles(document);
        await sentFile;
        assert.deepEqual(errors, []);
    } finally { finishSave(); await page.close(); }
});

test('cancelling an active Uppy upload releases the Save guard', async () => {
    let finishUpload;
    const held = new Promise(resolve => { finishUpload = resolve; });
    const { page, requests, errors } = await mount(form(attachments('form', 'data-new-uploads="allowed"')) + runtime(''), async ({ path, method }) => {
        if (path.includes('/forms/')) return formAnswer(record(method === 'GET' ? null : '7'));
        if (method === 'POST') { await held; return json(file('cancelled', 8)); }
        return json(files([]));
    });
    try {
        await openUploader(page);
        const sent = page.waitForRequest(r => r.method() === 'POST' && r.url().includes('/attachments/'));
        await page.locator('.uppy-Dashboard-input').first().setInputFiles({ name: 'cancelled.pdf', mimeType: 'application/pdf', buffer: Buffer.from('file') });
        await sent;
        await page.getByRole('button', { name: 'Cancel', exact: true }).first().click();
        finishUpload();
        await page.waitForFunction(() => !document.querySelector('[data-attachment-add]').disabled);
        await closeUploader(page);
        await page.getByRole('button', { name: 'Save', exact: true }).click();
        await page.getByText('Saved.', { exact: true }).waitFor();
        assert.equal(requests.filter(r => r.method === 'POST' && r.path.includes('/forms/')).length, 1);
        assert.deepEqual(errors, []);
    } finally { finishUpload(); await page.close(); }
});

test('Uppy honours upload limits and add-only permissions and fits mobile and desktop widths', async () => {
    const { page, requests, errors } = await mount(attachments('page') + runtime(''), ({ method }) => method === 'POST' ? json({ detail: 'Refused.' }, 422) : json({ ...files([file('existing', 8)], false), canAdd: true, maxFileSize: 10, accept: '.pdf' }));
    try {
        await openUploader(page);
        await page.locator('.uppy-Dashboard-input').first().setInputFiles({ name: 'large.pdf', mimeType: 'application/pdf', buffer: Buffer.alloc(11) });
        assert.equal(await page.locator('.uppy-Dashboard-Item').count(), 0);
        await openUploader(page);
        await page.locator('.uppy-Dashboard-input').first().setInputFiles({ name: 'wrong.txt', mimeType: 'text/plain', buffer: Buffer.from('file') });
        assert.equal(await page.locator('.uppy-Dashboard-Item').count(), 0);
        await openUploader(page);
        assert.equal(requests.filter(r => r.method !== 'GET').length, 0);
        await page.locator('.uppy-Dashboard-input').first().setInputFiles({ name: 'fine.pdf', mimeType: 'application/pdf', buffer: Buffer.from('file') });
        await page.locator('.uppy-Dashboard-Item').waitFor();
        assert.equal(await page.locator('.app-attachment-actions').count(), 0);
        await page.locator('.app-attachments-upload-errors').getByText('Refused.').waitFor();
        assert.equal(requests.filter(r => r.method !== 'GET').length, 1);
        for (const width of [1100, 390]) {
            await page.setViewportSize({ width, height: 800 });
            assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `fits ${width}px viewport`);
            assert.ok(await page.locator('dialog').evaluate(dialog => dialog.scrollWidth <= dialog.clientWidth), `dialog fits ${width}px viewport`);
        }
        assert.deepEqual(errors, []);
    } finally { await page.close(); }
    const readOnly = await mount(attachments('page').replace('data-read-only="false"', 'data-read-only="true"') + runtime(''), () => json(files([file('existing', 8)])));
    try {
        await readOnly.page.locator('.app-attachment').waitFor();
        assert.equal(await readOnly.page.locator('.uppy-Dashboard').count(), 0);
        assert.equal(await readOnly.page.locator('.app-attachment-actions').count(), 0);
        assert.deepEqual(readOnly.errors, []);
    } finally { await readOnly.page.close(); }
});



test('the uploader shows the page\'s translations and the block\'s own size limit', async () => {
    const strings = {
        uploaderDropOrBrowse: 'Déposez des fichiers ici ou {browse}', uploaderBrowse: 'parcourez', uploaderRemoveFile: 'Retirer le fichier',
        uploaderFileSelected: '{count} fichier choisi', fileTooLarge: 'Le fichier dépasse les {size} Mo de ce bloc.',
        uploaderUploadFailed: 'Échec du téléversement',
    };
    const { page, errors } = await mount(attachments('page') + runtime(''), ({ method }) => method === 'POST' ? json({ detail: 'Refusé.' }, 422) : json({ ...files([]), maxFileSize: 2 * 1048576 }),
        { lang: 'fr-CA', head: `<script type="application/json" data-appblocks-strings>${JSON.stringify(strings)}</script>` });
    try {
        await openUploader(page);
        const title = page.locator('.uppy-Dashboard-AddFiles-title');
        await title.waitFor();
        assert.equal((await title.textContent()).replace(/\s+/g, ' ').trim(), 'Déposez des fichiers ici ou parcourez');
        await openUploader(page);
        await page.locator('.uppy-Dashboard-input').first().setInputFiles({ name: 'large.pdf', mimeType: 'application/pdf', buffer: Buffer.alloc(3 * 1048576) });
        await page.getByText('Le fichier dépasse les 2 Mo de ce bloc.').first().waitFor();
        await openUploader(page);
        await page.locator('.uppy-Dashboard-input').first().setInputFiles({ name: 'small.pdf', mimeType: 'application/pdf', buffer: Buffer.from('file') });
        await page.waitForFunction(() => document.querySelector('[data-attachment-add-label]').textContent === 'Échec du téléversement');
        assert.equal(await page.getByRole('button', { name: 'Retirer le fichier' }).count(), 1);
        assert.deepEqual(errors, []);
    } finally { await page.close(); }
});

test('a record can be deleted once its background upload finishes, but not while it is under way', async () => {
    let finishUpload;
    const held = new Promise(resolve => { finishUpload = resolve; });
    const { page, requests, errors } = await mount(form(attachments('form')) + runtime(''), async ({ method, path }) => {
        if (path.startsWith('/api/forms/')) return method === 'DELETE' ? { status: 204 } : formAnswer(record('7'));
        if (method === 'POST') { await held; return json(file('sent', 8), 201); }
        return json(files(requests.some(r => r.method === 'POST' && r.path.includes('/attachments/')) ? [file('sent', 8)] : []));
    });
    const dialogs = [];
    page.on('dialog', dialog => dialogs.push(dialog.message()));
    try {
        const deletes = () => requests.filter(r => r.method === 'DELETE');
        await openUploader(page);
        const sent = page.waitForRequest(r => r.method() === 'POST' && r.url().includes('/attachments/'));
        await page.locator('.uppy-Dashboard-input').first().setInputFiles({ name: 'sent.pdf', mimeType: 'application/pdf', buffer: Buffer.from('sent') });
        await sent;
        const working = page.waitForEvent('dialog');
        await closeUploader(page);
        await page.getByRole('button', { name: 'Delete', exact: true }).click();
        assert.match((await working).message(), /Wait for that to finish/);
        assert.equal(deletes().length, 0);
        finishUpload();
        await page.locator('.app-attachment', { hasText: 'sent.pdf' }).waitFor();
        await page.waitForFunction(() => !document.querySelector('[data-attachment-add]').disabled);
        await closeUploader(page);
        await page.getByRole('button', { name: 'Delete', exact: true }).click();
        await page.getByText('Done', { exact: true }).waitFor();
        assert.equal(deletes().length, 1, 'deletion is allowed once the upload finishes');
        assert.equal(dialogs.length, 2, 'the wait, then the deletion\'s own confirmation');
        assert.deepEqual(errors, []);
    } finally { finishUpload(); await page.close(); }
});

test('an unavailable uploader shows its error in the dialog while the saved file list remains usable', async () => {
    const map = { imports: { 'http://localhost/App_Plugins/ApplicationBlocks/uppy.js': 'http://localhost/missing/uppy.js' } };
    const { page, errors } = await mount(attachments('page') + runtime(''), ({ path }) => path.startsWith('/missing/') ? { status: 404, body: '' } : json(files([file('existing', 8)])),
        { head: `<script type="importmap">${JSON.stringify(map)}</script>` });
    try {
        await page.locator('[data-attachment-add]').click();
        const problem = page.locator('.app-attachments-upload-errors');
        await problem.getByText('The file uploader could not load. Reload the page to try again.').waitFor();
        await closeUploader(page);
        await page.getByRole('link', { name: 'existing.pdf' }).waitFor();
        assert.equal(await page.getByRole('button', { name: 'Rename', exact: true }).isEnabled(), true);
        await page.locator('[data-attachment-add]').click();
        assert.equal(await problem.textContent(), 'The file uploader could not load. Reload the page to try again.');
        assert.deepEqual(errors, []);
    } finally { await page.close(); }
});

test('the API root comes from the page, and a Member\'s changes carry the antiforgery token fetched once', async () => {
    for (const [head, expected] of [['<meta name="appblocks-api-root" content="/site/services/">', '/site/services'], ['', '/site/api']]) {
        const { page, requests } = await mount(`<script type="module">import * as api from "/site/App_Plugins/ApplicationBlocks/data-api.js"; window.api = api;</script>`,
            ({ path }) => path.endsWith('/antiforgery/token') ? json({ token: 'member-token', headerName: 'X-Member-Token' }) : json({ ok: true }), { head, base: '/site' });
        try {
            await page.waitForFunction(() => window.api);
            assert.equal(await page.evaluate(() => window.api.apiRoot), expected);
            await page.evaluate(async () => {
                await window.api.memberRequest('/things', { method: 'POST', body: { a: 1 } });
                await window.api.memberRequest('/things', { method: 'delete' });
                await window.api.memberRequest('/things');
            });
            const tokens = requests.filter(r => r.path.endsWith('/antiforgery/token'));
            assert.deepEqual(tokens.map(r => r.path), [`${expected}/antiforgery/token`]);
            const things = requests.filter(r => r.path === '/site/things');
            assert.deepEqual(things.map(r => [r.method, r.headers['x-member-token'] ?? null]), [['POST', 'member-token'], ['DELETE', 'member-token'], ['GET', null]]);
        } finally { await page.close(); }
    }
});

test('a site\'s tokens win over the stylesheet\'s defaults wherever they load, the form\'s frame is tokens, and rich text keeps the site\'s box model', async () => {
    const head = `<style>:root { --app-accent: rgb(1, 2, 3); } .flat { --app-form-padding: 0; --app-form-border: 0; --app-form-shadow: none; }</style>`;
    const body = `<div class="app-form" id="card"></div><div class="flat"><div class="app-form" id="flat"></div></div>
        <div class="ah-rich-text"><p id="text" style="width: 100px; padding: 10px">Text</p></div><div class="app-form-message" id="own" style="width: 100px; padding: 10px"></div>`;
    const { page } = await mount(body, () => json({}, 404), { head });
    try {
        await page.locator('#card').waitFor({ state: 'attached' });
        const styles = await page.evaluate(() => {
            const style = id => getComputedStyle(document.getElementById(id));
            return {
                accent: getComputedStyle(document.documentElement).getPropertyValue('--app-accent').trim(),
                surface: getComputedStyle(document.documentElement).getPropertyValue('--app-surface').trim(),
                card: [style('card').paddingTop, style('card').borderTopWidth, style('card').boxShadow !== 'none'],
                flat: [style('flat').paddingTop, style('flat').borderTopWidth, style('flat').boxShadow],
                text: style('text').boxSizing, own: style('own').boxSizing,
            };
        });
        assert.deepEqual(styles, { accent: 'rgb(1, 2, 3)', surface: '#fff', card: ['28px', '1px', true], flat: ['0px', '0px', 'none'], text: 'content-box', own: 'border-box' });
    } finally { await page.close(); }
});

test('a schedule bar links only to a path on the site or an http, https, mailto or tel address', async () => {
    const { page } = await mount(`<script type="module">import { scheduleBar } from "/App_Plugins/ApplicationBlocks/schedule.js"; window.scheduleBar = scheduleBar;</script>`, () => json({}, 404));
    try {
        await page.waitForFunction(() => window.scheduleBar);
        const shapes = await page.evaluate(() => ['/rooms/1', 'https://example.com/a', 'mailto:a@example.com', 'tel:+15551234', 'javascript:alert(1)', ' JaVaScRiPt:alert(1)',
            '//evil.example', '/\\evil.example', '/\t/evil.example', 'data:text/html,x', 'rooms/1', './a', '#top'].map(link => {
            const bar = window.scheduleBar({ label: 'Bar', link }).firstElementChild;
            return [link, bar.tagName, bar.getAttribute('href')];
        }));
        for (const [link, tag, href] of shapes.slice(0, 4)) assert.deepEqual([tag, href], ['A', link], link);
        for (const [link, tag, href] of shapes.slice(4)) assert.deepEqual([tag, href], ['SPAN', null], link);
        const popover = await page.evaluate(() => {
            const fragment = window.scheduleBar({ label: 'Bar', description: 'More', link: 'javascript:alert(1)', closeLabel: 'Fermer' });
            return { links: fragment.querySelectorAll('a').length, close: fragment.querySelector('.app-schedule-popover__close').getAttribute('aria-label') };
        });
        assert.deepEqual(popover, { links: 0, close: 'Fermer' });
    } finally { await page.close(); }
});
