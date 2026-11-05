import assert from 'node:assert/strict';
import { runtimeAsset } from './runtime-assets.mjs';
import { test, before, after } from 'node:test';
import { createRequire } from 'node:module';
import { readFile, mkdir } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../', import.meta.url));
const require = createRequire(resolve(process.env.INTERACTIVE_REPORTS_ROOT || resolve(root, '../Interactive-Reports'), 'package.json'));
const { chromium } = require('playwright');
let browser;
before(async () => { browser = await chromium.launch({ headless: true, ...(existsSync(chromium.executablePath()) ? {} : { executablePath: process.env.BROWSER_PATH || 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe' }) }); });
after(async () => browser?.close());

const choices = [['a', 'Alpha'], ['b', 'Beta'], ['c', 'Gamma'], ['d', 'Delta'], ['e', 'Epsilon'], ['a,b', 'Comma value'], ['say "yes"', 'Quoted value']];
const escape = value => String(value).replaceAll('&', '&amp;').replaceAll('"', '&quot;').replaceAll('<', '&lt;');
const optionValues = locator => locator.locator('option').evaluateAll(options => options.map(option => option.value));
async function mount({ value = '', key = null, defaultValue, readonly = false, writable = true, required = false, reject = false } = {}) {
    const page = await browser.newPage({ viewport: { width: 1100, height: 760 } });
    const posted = [], errors = [];
    page.on('pageerror', error => errors.push(error.message));
    const record = { key, version: key ? '1' : null, values: { items: value }, capabilities: { create: writable, update: writable } };
    await page.route('**/*', async route => {
        const request = route.request(), path = new URL(request.url()).pathname;
        const asset = await runtimeAsset(path); if (asset) return route.fulfill(asset);
        if (path.startsWith('/api/forms/')) {
            if (request.method() === 'GET') return route.fulfill({ json: { record, definitionVersion: 'v1', antiforgeryToken: 'token' } });
            posted.push(request.postDataJSON());
            return route.fulfill(reject ? { status: 422, json: { errors: { items: ['Select available choices.'] } } } : { json: { record: { ...record, key: '1', values: request.postDataJSON().values } } });
        }
        return route.fulfill({ contentType: 'text/html; charset=utf-8', body: `<!doctype html><html><head><meta charset="utf-8"><title>Shuttle</title>
            <link rel="stylesheet" href="/App_Plugins/ApplicationBlocks/runtime.css"><style>body{font-family:system-ui;margin:24px;background:#f6f7f9}main{max-width:960px;margin:auto}</style></head>
            <body><main class="application-blocks"><form class="app-form" data-app-form data-api="/api/forms/page/form" data-key-parameter="id" novalidate>
            <h2>Ordered selection</h2><div class="app-form-message" role="status"></div><div class="app-form-errors" tabindex="-1" hidden></div>
            <fieldset class="app-form-body" disabled><div class="app-form-field"><label id="items-label" for="items-available">Items</label>
            <div class="app-shuttle" data-app-shuttle data-required="${required}" role="group" aria-labelledby="items-label">
              <input type="hidden" id="items" name="items" data-field="items" data-kind="shuttle" data-readonly="${readonly}" ${defaultValue === undefined ? '' : `data-default="${escape(defaultValue)}"`}>
              <div class="app-shuttle-list"><label for="items-available">Available</label><select id="items-available" data-shuttle-available multiple size="8" aria-describedby="items-help items-error">${choices.map(([value, label]) => `<option value="${escape(value)}">${label}</option>`).join('')}</select></div>
              <div class="app-shuttle-actions">${[['add', 'Add →'], ['add-all', 'Add all →'], ['remove', '← Remove'], ['remove-all', '← Remove all']].map(([action, label]) => `<button type="button" data-shuttle-action="${action}">${label}</button>`).join('')}</div>
              <div class="app-shuttle-list"><label for="items-selected">Selected (in order)</label><select id="items-selected" data-shuttle-selected multiple size="8" aria-describedby="items-help items-error"></select></div>
              <div class="app-shuttle-actions">${['top', 'up', 'down', 'bottom'].map(action => `<button type="button" data-shuttle-action="${action}">${action[0].toUpperCase() + action.slice(1)}</button>`).join('')}</div>
              <span class="app-shuttle-status" role="status" aria-live="polite"></span>
            </div><small id="items-help">Select items, add them, then arrange the selected list in the order you want.</small><span id="items-error" class="app-field-error"></span></div>
            <div class="app-form-actions"><button type="submit" data-save>Save</button></div></fieldset></form></main>
            <script type="module" src="/App_Plugins/ApplicationBlocks/runtime.js"></script></body></html>` });
    });
    await page.goto('http://localhost/shuttle/');
    await page.waitForFunction(() => !document.querySelector('.app-form-body').disabled);
    const available = page.locator('[data-shuttle-available]'), selected = page.locator('[data-shuttle-selected]');
    const action = name => page.locator(`[data-shuttle-action="${name}"]`);
    return { page, posted, errors, available, selected, action };
}

test('transfer, multi-item ordering, removal and saving use the entire right list in order', async () => {
    const { page, posted, errors, available, selected, action } = await mount();
    try {
        await available.selectOption(['a', 'c', 'e']); await action('add').click();
        assert.deepEqual(await optionValues(selected), ['a', 'c', 'e']);
        await available.selectOption(['b', 'd']); await action('add').click();
        await selected.selectOption(['c', 'e']); await action('down').click();
        assert.deepEqual(await optionValues(selected), ['a', 'b', 'c', 'e', 'd']);
        await action('up').click();
        assert.deepEqual(await optionValues(selected), ['a', 'c', 'e', 'b', 'd']);
        await selected.selectOption(['e', 'd']); await action('top').click();
        assert.deepEqual(await optionValues(selected), ['e', 'd', 'a', 'c', 'b']);
        await action('bottom').click();
        assert.deepEqual(await optionValues(selected), ['a', 'c', 'b', 'e', 'd']);
        await selected.selectOption(['c', 'e']); await action('remove').click();
        assert.deepEqual(await optionValues(selected), ['a', 'b', 'd']);
        assert.deepEqual(await optionValues(available), ['c', 'e', 'a,b', 'say "yes"']);
        await selected.selectOption([]); // Highlighted options are not the submitted selection.
        await page.getByRole('button', { name: 'Save', exact: true }).click();
        await page.waitForFunction(() => document.querySelector('.app-form-message').textContent === 'Saved.');
        assert.equal(posted[0].values.items, 'a,b,d');
        assert.deepEqual(posted[0].formData, {});
        assert.deepEqual(errors, []);
    } finally { await page.close(); }
});

test('all controls, CSV quoting, empty output and save/reload preserve values', async () => {
    const { page, posted, selected, action } = await mount();
    try {
        await action('add-all').click();
        await page.getByRole('button', { name: 'Save', exact: true }).click();
        await page.waitForFunction(() => document.querySelector('.app-form-message').textContent === 'Saved.');
        assert.equal(posted[0].values.items, 'a,b,c,d,e,"a,b","say ""yes"""');
        assert.deepEqual(await optionValues(selected), choices.map(([value]) => value));
        await action('remove-all').click();
        await Promise.all([
            page.waitForResponse(response => response.request().method() === 'PUT'),
            page.getByRole('button', { name: 'Save', exact: true }).click(),
        ]);
        assert.equal(posted[1].values.items, '');
    } finally { await page.close(); }
});

test('defaults apply only to new records, and stored unavailable choices keep their positions', async () => {
    for (const [options, expected] of [[{ defaultValue: 'c,a' }, ['c', 'a']], [{ key: '1', defaultValue: 'c,a' }, []], [{ key: '1', value: 'b,retired,a' }, ['b', 'retired', 'a']]]) {
        const { page, selected, available, action } = await mount(options);
        try {
            assert.deepEqual(await optionValues(selected), expected);
            if (options.value) {
                await selected.selectOption('retired'); await action('remove').click();
                assert.ok(!(await optionValues(available)).includes('retired'));
            }
        } finally { await page.close(); }
    }
});

test('read-only and capability restrictions disable lists and actions', async () => {
    for (const options of [{ readonly: true }, { writable: false }, { key: '1', writable: false }]) {
        const { page, available, selected } = await mount({ value: 'b,a', ...options });
        try {
            assert.ok(await available.isDisabled()); assert.ok(await selected.isDisabled());
            assert.ok(await page.locator('[data-shuttle-action]').evaluateAll(buttons => buttons.every(button => button.disabled)));
            assert.deepEqual(await optionValues(selected), ['b', 'a']);
        } finally { await page.close(); }
    }
});

test('malformed stored CSV is retained and blocked instead of silently saved as an empty list', async () => {
    for (const value of ['a,a', 'a,', '"unclosed', 'a\nb']) {
        const { page, posted, selected } = await mount({ key: '1', value });
        try {
            assert.equal(await page.locator('[data-field]').inputValue(), value);
            assert.ok(await selected.evaluate(element => !element.checkValidity()));
            await page.getByRole('button', { name: 'Save', exact: true }).click();
            assert.equal(posted.length, 0);
        } finally { await page.close(); }
    }
});

test('required validation checks list membership, and server errors target the visible list', async () => {
    const { page, posted, available, selected, action } = await mount({ required: true, reject: true });
    try {
        await page.getByRole('button', { name: 'Save', exact: true }).click();
        assert.equal(posted.length, 0);
        assert.equal(await selected.evaluate(element => element.validationMessage), 'Select at least one item.');
        // Keyboard access uses native list selection plus ordinary buttons.
        await available.focus(); await page.keyboard.press('Home');
        await action('add').focus(); await page.keyboard.press('Enter');
        await selected.selectOption([]);
        await page.getByRole('button', { name: 'Save', exact: true }).click();
        await page.waitForSelector('#items-selected[aria-invalid=true]');
        assert.equal(posted.length, 1);
        assert.equal(await page.locator('.app-form-errors a').getAttribute('href'), '#items-selected');
        assert.equal(await page.locator('#items-error').textContent(), 'Select available choices.');
        assert.ok(await action('remove-all').isEnabled());
    } finally { await page.close(); }
});

test('desktop and mobile layouts fit their viewport', async () => {
    const { page } = await mount({ value: 'c,a,"a,b"' });
    try {
        await mkdir(resolve(root, 'artifacts'), { recursive: true });
        for (const [name, width] of [['desktop', 1100], ['mobile', 390]]) {
            await page.setViewportSize({ width, height: 760 });
            assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
            await page.screenshot({ path: resolve(root, `artifacts/shuttle-${name}.png`), fullPage: true });
        }
    } finally { await page.close(); }
});
