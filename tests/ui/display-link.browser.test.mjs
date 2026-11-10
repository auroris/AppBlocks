import assert from 'node:assert/strict';
import { runtimeAsset } from './runtime-assets.mjs';
import { test, before, after } from 'node:test';
import { createRequire } from 'node:module';
import { readFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../', import.meta.url));
const require = createRequire(resolve(process.env.INTERACTIVE_REPORTS_ROOT || resolve(root, '../Interactive-Reports'), 'package.json'));
const { chromium } = require('playwright');
const styles = await readFile(resolve(root, 'src/AppBlocks/wwwroot/App_Plugins/ApplicationBlocks/runtime.css'), 'utf8');
let browser;
before(async () => { browser = await chromium.launch({ headless: true, ...(existsSync(chromium.executablePath()) ? {} : { executablePath: process.env.BROWSER_PATH || 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe' }) }); });
after(async () => browser?.close());

// Block.cshtml's markup for a Display value with a link template: the value in a hidden input and the link beside it.
// DisplayLinkTests verifies the real Razor output and what publishing accepts as a template.
function linked(field, label, template) {
    return `<div class="app-form-field"><label id="${field}-label">${label}</label>
        <input type="hidden" id="${field}" name="${field}" data-field="${field}" data-kind="text" data-readonly="true" data-label="${label}">
        <a id="${field}-link" class="app-display-link" data-display-link="${field}" data-link-template="${template}" aria-labelledby="${field}-label ${field}-link" aria-describedby="${field}-help"></a>
        <small id="${field}-help"></small><span id="${field}-error" class="app-field-error"></span></div>`;
}

async function mount({ values = {}, key = '7', links = [], base = '' } = {}) {
    const page = await browser.newPage({ viewport: { width: 720, height: 480 } });
    const errors = [], posted = [];
    page.on('pageerror', error => errors.push(error.message));
    const record = shown => ({ key, checksum: '1', values: shown, capabilities: { create: true, update: true } });
    await page.route('**/*', async route => {
        const url = new URL(route.request().url());
        const asset = await runtimeAsset(url.pathname); if (asset) return route.fulfill(asset);
        if (url.pathname.startsWith(`${base}/api/forms/`)) {
            if (route.request().method() === 'GET') return route.fulfill({ json: { record: record(values), definitionVersion: '1', antiforgeryToken: 'token' } });
            posted.push(route.request().postDataJSON());
            return route.fulfill({ json: { record: record({ ...values, ...posted.at(-1).values }) } });
        }
        return route.fulfill({ contentType: 'text/html', body: `<!doctype html><html lang="en"><meta name="viewport" content="width=device-width"><title>Display links</title>
            <style>body { font-family: system-ui; padding: 1rem; margin: 0; } ${styles}</style>
            <div class="application-blocks"><form class="app-form" data-app-form data-api="${base}/api/forms/page/form" data-key-parameter="id" data-return="/" novalidate>
                <div class="app-form-message" role="status" aria-live="polite"></div><div class="app-form-errors" role="alert" tabindex="-1" hidden></div>
                <fieldset class="app-form-body" disabled>
                    ${links.map(link => linked(...link)).join('')}
                    <input type="hidden" id="reference" name="reference" data-field="reference" data-kind="text" data-readonly="true" data-label="Reference">
                    <div class="app-form-field"><label for="notes">Notes</label><input id="notes" name="notes" data-field="notes" data-kind="text" data-readonly="false"></div>
                    <div class="app-form-actions"><button type="submit" data-save>Save</button></div>
                </fieldset>
            </form></div><script type="module" src="${base}/App_Plugins/ApplicationBlocks/runtime.js"></script></html>` });
    });
    await page.goto(`http://localhost${base}/contacts/?id=${key ?? ''}`);
    await page.waitForFunction(() => !document.querySelector('.app-form-body').disabled);
    return { page, errors, posted, link: field => page.locator(`[data-display-link="${field}"]`) };
}

const address = locator => locator.getAttribute('href');

test('a template fills its fields from the record, escaped, and a mail address keeps its @', async () => {
    const { page, errors, link } = await mount({
        values: { email: 'jane.o\'neil+rdao@example.org', reference: 'DOA 2026/0042 & more', customerId: 'a/b?c#d', notes: null },
        links: [['email', 'Email the requester', 'mailto:{email}?subject=Request%20{reference}&amp;body=See%20{ reference }'], ['customerId', 'Customer', '../customer/?id={customerId}&amp;from={nowhere}&amp;as={constructor}']],
    });
    assert.equal(await link('email').textContent(), 'jane.o\'neil+rdao@example.org');
    assert.equal(await address(link('email')), 'mailto:jane.o\'neil+rdao@example.org?subject=Request%20DOA%202026%2F0042%20%26%20more&body=See%20DOA%202026%2F0042%20%26%20more');
    // A value stays the one part of the address it was written into, and a name the record lacks leaves nothing, even
    // one every object answers to.
    assert.equal(await address(link('customerId')), '../customer/?id=a%2Fb%3Fc%23d&from=&as=');
    assert.equal(await link('customerId').textContent(), 'a/b?c#d');
    // The link is named by its label and its value, and the keyboard reaches it.
    assert.equal(await page.getByRole('link', { name: 'Email the requester jane.o\'neil+rdao@example.org' }).count(), 1);
    await link('email').focus();
    assert.equal(await page.evaluate(() => document.activeElement.dataset.displayLink), 'email');
    assert.deepEqual(errors, []);
    await page.close();
});

test('a template that is one field takes the whole address from it, when a page may link there', async () => {
    for (const [website, expected] of [
        ['https://example.org/jane?x=1', 'https://example.org/jane?x=1'],
        ['/people/jane', '/people/jane'],
        ['  mailto:jane@example.org  ', 'mailto:jane@example.org'],
        ['javascript:alert(1)', null],
        ['JaVaScRiPt:alert(1)', null],
        ['data:text/html,<script>alert(1)</script>', null],
        ['//example.org/jane', null],
        ['/\\example.org', null],
    ]) {
        const { page, errors, link } = await mount({ values: { website }, links: [['website', 'Website', '{website}']] });
        assert.equal(await address(link('website')), expected, website);
        // Without an address the value is still shown, as plain text.
        assert.equal(await link('website').textContent(), website.trim());
        assert.deepEqual(errors, []);
        await page.close();
    }
});

test('a value that completes a script address is refused, and an empty value links nowhere', async () => {
    const script = await mount({ values: { scheme: 'javascript' }, links: [['scheme', 'Scheme', '{scheme}:alert(1)']] });
    assert.equal(await address(script.link('scheme')), null);
    assert.equal(await script.link('scheme').textContent(), 'javascript');
    await script.page.close();

    const empty = await mount({ values: { email: null, reference: 'DOA-1' }, links: [['email', 'Email', 'mailto:{email}?subject={reference}']] });
    assert.equal(await address(empty.link('email')), null);
    assert.equal(await empty.link('email').textContent(), '');
    // The empty link keeps the height of a line, so the fields beside it stay level.
    assert.ok((await empty.link('email').boundingBox()).height > 20);
    await empty.page.close();
});

test('the link follows the record after a save, is never submitted, and takes the site\'s path base', async () => {
    const { page, errors, posted, link } = await mount({
        base: '/intranet', values: { customerId: '7', reference: 'DOA-1', notes: 'First' },
        links: [['customerId', 'Customer', '/customers/?id={customerId}&amp;ref={reference}']],
    });
    assert.equal(await address(link('customerId')), '/intranet/customers/?id=7&ref=DOA-1');
    await page.locator('#notes').fill('Second');
    await page.getByRole('button', { name: 'Save', exact: true }).click();
    await page.getByRole('status').getByText('Saved.', { exact: true }).waitFor();
    // Only the editable field goes up; the displayed value and the hidden read-only one stay the server's.
    assert.deepEqual(posted[0].values, { notes: 'Second' });
    assert.equal(await address(link('customerId')), '/intranet/customers/?id=7&ref=DOA-1');
    assert.deepEqual(errors, []);
    await page.close();
});
