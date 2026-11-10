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
    <div class="app-attachments-upload" hidden><label><span>File</span> <input type="file" form="" data-attachment-file></label>
    <label><span>Label</span> <input type="text" form="" data-attachment-label></label><label><span>Notes</span> <input type="text" form="" data-attachment-notes></label>
    <button type="button" data-attachment-upload>Upload</button></div></section>`;
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
        const entry = { method: request.method(), path, search: url.search, headers: request.headers(), body: request.headers()['content-type']?.includes('json') ? request.postDataJSON() : null };
        requests.push(entry);
        return route.fulfill(await answer(entry));
    });
    await page.goto(`http://localhost${base}/`);
    return { page, requests, errors };
}
const runtime = base => `<script type="module" src="${base}/App_Plugins/ApplicationBlocks/runtime.js"></script>`;

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
        await page.locator('[data-attachment-file]').waitFor({ state: 'visible' });
        await page.locator('[data-attachment-file]').setInputFiles({ name: 'ahead.pdf', mimeType: 'application/pdf', buffer: Buffer.from('%PDF-1.4') });
        await page.getByRole('button', { name: 'Upload' }).click();
        await page.locator('.app-attachment').waitFor();
        assert.equal(await page.locator('a.app-attachment-name').count(), 0, 'a file sent ahead is not served yet');
        assert.equal(await page.getByRole('button', { name: 'Replace' }).count(), 0);
        await page.locator('#title').fill('Report');
        await page.getByRole('button', { name: 'Save' }).click();
        await page.locator('a.app-attachment-name').waitFor();
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
