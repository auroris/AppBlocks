import assert from 'node:assert/strict';
import { test, before, after } from 'node:test';
import { createRequire } from 'node:module';
import { readFile, mkdir } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../', import.meta.url));
const require = createRequire(resolve(process.env.INTERACTIVE_REPORTS_ROOT || resolve(root, '../Interactive-Reports'), 'package.json'));
const { chromium } = require('playwright');
const runtime = await readFile(resolve(root, 'src/AppBlocks/wwwroot/App_Plugins/ApplicationBlocks/runtime.js'), 'utf8');
const styles = await readFile(resolve(root, 'src/AppBlocks/wwwroot/App_Plugins/ApplicationBlocks/runtime.css'), 'utf8');
let browser;
before(async () => { browser = await chromium.launch({ headless: true, ...(existsSync(chromium.executablePath()) ? {} : { executablePath: process.env.BROWSER_PATH || 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe' }) }); });
after(async () => browser?.close());

// Block.cshtml's color field markup; ColorPickerTests verifies the real Razor output and server validation.
async function mount({ value = null, key = null, defaultValue, required = false, readOnly = false, writable = true, reject = false } = {}) {
    const page = await browser.newPage({ viewport: { width: 720, height: 480 } });
    const errors = [], posted = [];
    page.on('pageerror', error => errors.push(error.message));
    const record = values => ({ key, version: '1', values, capabilities: { create: writable, update: writable } });
    await page.route('**/*', async route => {
        const url = new URL(route.request().url());
        if (url.pathname.endsWith('/runtime.js')) return route.fulfill({ contentType: 'text/javascript', body: runtime });
        if (url.pathname.startsWith('/api/forms/')) {
            if (route.request().method() === 'GET') return route.fulfill({ json: { record: record({ color: value }), definitionVersion: '1', antiforgeryToken: 'token' } });
            posted.push(route.request().postDataJSON());
            if (reject) return route.fulfill({ status: 422, json: { errors: { color: ['Choose a different color.'] } } });
            key = '1';
            return route.fulfill({ json: { record: record(posted.at(-1).values) } });
        }
        return route.fulfill({ contentType: 'text/html', body: `<!doctype html><html lang="en"><meta name="viewport" content="width=device-width"><title>Color picker</title>
            <style>body { font-family: system-ui; padding: 1rem; margin: 0; } ${styles}</style>
            <div class="application-blocks"><form class="app-form" data-app-form data-api="/api/forms/page/form" data-return="/" novalidate>
                <h2>Appearance</h2><div class="app-form-message" role="status" aria-live="polite"></div><div class="app-form-errors" role="alert" tabindex="-1" hidden></div>
                <fieldset class="app-form-body" disabled><div class="app-form-field">
                    <label id="color-label" for="color">Accent color${required ? ' *' : ''}</label>
                    <div class="app-color" data-app-color>
                        <input type="color" data-color-picker aria-label="Choose a color for Accent color" aria-describedby="color-help color-error">
                        <input type="text" id="color" name="color" data-field="color" data-kind="text" data-readonly="${readOnly}" ${readOnly ? 'readonly' : ''} ${required ? 'required' : ''}
                            ${defaultValue ? `data-default="${defaultValue}"` : ''} pattern="#[0-9a-fA-F]{6}" title="Enter a color in #RRGGBB format, such as #2a5d8f."
                            placeholder="#RRGGBB" spellcheck="false" autocomplete="off" aria-describedby="color-help color-error">
                        <button type="button" data-color-clear aria-label="Clear color for Accent color">Clear</button>
                    </div><small id="color-help">Choose a color or enter its hex value.</small><span id="color-error" class="app-field-error"></span>
                </div><div class="app-form-actions"><button type="submit" data-save>Save</button></div></fieldset>
            </form></div><script type="module" src="/App_Plugins/ApplicationBlocks/runtime.js"></script></html>` });
    });
    await page.goto('http://localhost/colors/');
    await page.waitForFunction(() => !document.querySelector('.app-form-body').disabled);
    return { page, errors, posted, field: page.locator('#color'), picker: page.locator('[data-color-picker]'), clear: page.locator('[data-color-clear]') };
}

async function save(page) {
    await page.getByRole('button', { name: 'Save', exact: true }).click();
    await page.getByRole('status').getByText('Saved.', { exact: true }).waitFor();
}

test('empty stays empty, picking and typing synchronize, and Clear saves null', async () => {
    const { page, errors, posted, field, picker, clear } = await mount();
    assert.equal(await field.inputValue(), '');
    assert.equal(await page.locator('[data-app-color]').getAttribute('data-empty'), 'true');
    assert.equal(await clear.isDisabled(), true);
    await save(page);
    assert.equal(posted[0].values.color, null);
    await picker.fill('#2a5d8f');
    assert.equal(await field.inputValue(), '#2a5d8f');
    await field.fill('#AAbbCC');
    assert.equal(await picker.inputValue(), '#aabbcc');
    await save(page);
    assert.deepEqual(posted[1].values, { color: '#AAbbCC' });
    assert.deepEqual(posted[1].formData, {});
    await clear.click();
    assert.equal(await field.inputValue(), '');
    assert.equal(await field.evaluate(element => element === document.activeElement), true);
    await save(page);
    assert.equal(posted[2].values.color, null);
    assert.deepEqual(errors, []);
    await page.close();
});

test('defaults fill new empty records and leave existing or initialized values alone', async () => {
    for (const [options, expected] of [
        [{ defaultValue: '#123456' }, '#123456'],
        [{ value: '#abcdef', defaultValue: '#123456' }, '#abcdef'],
        [{ key: '1', defaultValue: '#123456' }, ''],
        [{ key: '1', value: '#abcdef', defaultValue: '#123456' }, '#abcdef'],
    ]) {
        const { page, field, picker } = await mount(options);
        assert.equal(await field.inputValue(), expected);
        if (expected) assert.equal(await picker.inputValue(), expected);
        await page.close();
    }
});

test('required and malformed colors cannot submit and invalid saved values remain visible', async () => {
    const { page, field, clear, posted } = await mount({ value: 'legacy-red', required: true });
    assert.equal(await field.inputValue(), 'legacy-red');
    for (const value of ['', 'red', '#fff', '#12345678', '#zz0011', ' #123456']) {
        await field.fill(value);
        await page.getByRole('button', { name: 'Save', exact: true }).click();
        assert.equal(await field.evaluate(element => element.checkValidity()), false);
        assert.equal(posted.length, 0);
    }
    await field.fill('#123456');
    await clear.click();
    assert.equal(await field.evaluate(element => element.validity.valueMissing), true);
    await field.fill('#123456');
    await save(page);
    assert.equal(posted.length, 1);
    await page.close();
});

test('read-only and unavailable write capabilities disable all color controls', async () => {
    for (const options of [{ readOnly: true }, { writable: false }, { key: '1', writable: false }]) {
        const { page, field, picker, clear, posted } = await mount({ ...options, value: '#abcdef' });
        assert.equal(await field.isDisabled(), true);
        assert.equal(await picker.isDisabled(), true);
        assert.equal(await clear.isDisabled(), true);
        if (options.readOnly) {
            await save(page);
            assert.deepEqual(posted[0].values, {});
        } else assert.equal(await page.getByRole('button', { name: 'Save', exact: true }).count(), 0);
        await page.close();
    }
});

test('server errors are linked to the hex field and preserve the selected color', async () => {
    const { page, field, picker } = await mount({ value: '#123456', reject: true });
    await page.getByRole('button', { name: 'Save', exact: true }).click();
    await page.getByRole('alert').getByText('Choose a different color.').waitFor();
    assert.equal(await field.getAttribute('aria-invalid'), 'true');
    assert.equal(await field.inputValue(), '#123456');
    assert.equal(await picker.inputValue(), '#123456');
    assert.equal(await page.locator('.app-form-errors a').getAttribute('href'), '#color');
    await page.close();
});

test('color controls fit a phone viewport and retain accessible labels', async () => {
    const { page, errors } = await mount({ value: '#2a5d8f' });
    await page.setViewportSize({ width: 320, height: 480 });
    assert.equal(await page.getByLabel('Accent color', { exact: true }).count(), 1);
    assert.equal(await page.getByLabel('Choose a color for Accent color', { exact: true }).count(), 1);
    assert.equal(await page.getByRole('button', { name: 'Clear color for Accent color' }).count(), 1);
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    await mkdir(resolve(root, 'artifacts/color-picker'), { recursive: true });
    await page.screenshot({ path: resolve(root, 'artifacts/color-picker/mobile.png'), fullPage: true });
    await page.setViewportSize({ width: 720, height: 480 });
    await page.screenshot({ path: resolve(root, 'artifacts/color-picker/desktop.png'), fullPage: true });
    assert.deepEqual(errors, []);
    await page.close();
});
