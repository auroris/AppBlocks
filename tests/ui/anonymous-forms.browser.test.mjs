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
const runtime = await readFile(resolve(root, 'src/AppBlocks/wwwroot/App_Plugins/ApplicationBlocks/runtime.js'), 'utf8');
let browser;
before(async () => { browser = await chromium.launch({ headless: true, ...(existsSync(chromium.executablePath()) ? {} : { executablePath: process.env.BROWSER_PATH || 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe' }) }); });
after(async () => browser?.close());

const api = '/api/forms/page/form';
const blank = { key: null, version: null, values: { name: null }, capabilities: { create: true } };

/** The markup Block.cshtml renders for a form, a stand-in captcha widget's script, and a server that answers as told. */
async function mount({ anonymous = true, loadStatus = 200, submit = () => ({ status: 200, body: { status: 200, record: blank } }), extra = '', address = '/contact/', origin = 'http://localhost' } = {}) {
    const page = await browser.newPage();
    const errors = [], posted = [];
    let token = 'server-token';
    page.on('pageerror', error => errors.push(error.message));
    await page.route('**/*', async route => {
        const request = route.request();
        const url = new URL(request.url());
        if (url.pathname.endsWith('/runtime.js')) return route.fulfill({ contentType: 'text/javascript', body: runtime });
        if (url.pathname === '/contact/') return route.fulfill({ contentType: 'text/html', body: `<!doctype html><title>Contact</title>
            <form class="app-form" data-app-form data-api="${api}" data-key-parameter="id" data-return="/thanks/" ${anonymous ? 'data-anonymous="true"' : ''} novalidate>
                <div class="app-form-message" role="status" aria-live="polite"></div>
                <div class="app-form-errors" role="alert" tabindex="-1" hidden></div>
                <input type="hidden" name="__RequestVerificationToken" value="page-token">
                <fieldset class="app-form-body" disabled>
                    <input id="f1" name="name" data-field="name" data-kind="text" data-readonly="false" type="text"><span id="f1-error"></span>
                    ${extra}
                    <div class="app-form-actions"><button type="submit" name="save" value="1" data-save>Save</button><button type="button" data-delete hidden>Delete</button></div>
                </fieldset>
            </form>
            <script>
                // Stands in for a captcha widget: once its script has run it puts a hidden token control inside the form, under the name reCAPTCHA uses.
                addEventListener('DOMContentLoaded', () => {
                    const control = document.createElement('input');
                    control.type = 'hidden'; control.name = 'g-recaptcha-response'; control.value = 'x'.repeat(4096);
                    document.querySelector('[data-app-form] .app-form-body')?.append(control);
                });
            </script>
            <script type="module" src="/App_Plugins/ApplicationBlocks/runtime.js"></script>` });
        if (url.pathname.startsWith(api)) {
            if (request.method() === 'GET') {
                if (loadStatus !== 200) return route.fulfill({ status: loadStatus, contentType: 'application/json', body: JSON.stringify({ status: loadStatus }) });
                return route.fulfill({ contentType: 'application/json', body: JSON.stringify({ record: blank, definitionVersion: 'v1', antiforgeryToken: token }) });
            }
            posted.push({ method: request.method(), body: request.postDataJSON(), headers: request.headers() });
            const answer = submit(posted.length);
            return route.fulfill({ status: answer.status, contentType: 'application/json', body: answer.body === undefined ? '' : JSON.stringify(answer.body) });
        }
        if (url.pathname.startsWith('/api/attachments/')) return route.fulfill({ status: 403, contentType: 'application/json', body: JSON.stringify({ detail: 'You do not have access to these files.' }) });
        return route.fulfill({ status: 404, body: '' });
    });
    await page.goto(origin + address);
    return { page, errors, posted };
}

const ready = page => page.waitForFunction(() => !document.querySelector('.app-form-body').disabled);
const save = page => page.getByRole('button', { name: 'Save' }).click();

test('a widget\'s hidden token reaches the server through the runtime, apart from the fields', async () => {
    const { page, errors, posted } = await mount({ extra: `
        <input type="text" name="website" value="">
        <input type="text" name="topic" value="Sales">
        <input type="password" name="secret" value="hunter2">
        <input type="file" name="upload">
        <input type="checkbox" name="consent" value="yes" checked>
        <input type="checkbox" name="newsletter" value="yes">
        <input type="radio" name="contact-by" value="mail"><input type="radio" name="contact-by" value="phone" checked>
        <select name="colour"><option value="red" selected>Red</option></select>
        <select name="many" multiple><option value="a" selected>A</option><option value="b" selected>B</option></select>
        <textarea name="comment">Hi</textarea>
        <input type="text" name="off" value="no" disabled>
        <input type="text" name="elsewhere" value="no" form="">
        <input type="text" value="no name">
        <button type="button" name="press" value="no">Press</button>
        <input type="checkbox" name="twice" value="1" checked><input type="checkbox" name="twice" value="2" checked>` });
    await ready(page);
    await page.locator('#f1').fill('Pat');
    await save(page);
    await page.getByRole('status').getByText('Saved.').waitFor();
    assert.deepEqual(errors, []);
    assert.equal(posted.length, 1);
    const { body, headers } = posted[0];
    // The fields alone, as validation and strict binding expect them.
    assert.deepEqual(body.values, { name: 'Pat' });
    assert.equal(headers.requestverificationtoken, 'server-token');
    assert.equal(body.definitionVersion, 'v1');
    assert.match(body.submissionKey, /^[0-9a-f-]{36}$/);
    assert.deepEqual(body.formData, {
        'g-recaptcha-response': 'x'.repeat(4096), website: '', topic: 'Sales', consent: 'yes', 'contact-by': 'phone', colour: 'red', many: ['a', 'b'], comment: 'Hi', twice: ['1', '2'],
    });
    await page.close();
});

test('a create-only form stays where it is, ready for another message, with nothing to come back to in its address', async () => {
    const { page, errors, posted } = await mount();
    await ready(page);
    await page.locator('#f1').fill('Pat');
    await save(page);
    await page.getByRole('status').getByText('Saved.').waitFor();
    assert.equal(new URL(page.url()).search, '');
    assert.equal(await page.locator('#f1').inputValue(), '');
    assert.equal(await page.locator('#f1').isDisabled(), false);
    await page.locator('#f1').fill('Sam');
    await save(page);
    await page.waitForFunction(() => document.querySelector('.app-form-body').disabled === false && document.querySelector('#f1').value === '');
    assert.equal(posted.length, 2);
    assert.notEqual(posted[0].body.submissionKey, posted[1].body.submissionKey);
    assert.deepEqual(errors, []);
    await page.close();
});

test('a form works on a plain HTTP public site, where the browser offers no crypto.randomUUID', async () => {
    const { page, errors, posted } = await mount({ origin: 'http://public.example.test' });
    assert.deepEqual(await page.evaluate(() => [isSecureContext, typeof crypto.randomUUID]), [false, 'undefined']);
    await ready(page);
    for (const name of ['Pat', 'Sam']) {
        await page.locator('#f1').fill(name);
        await save(page);
        await page.waitForFunction(() => document.querySelector('.app-form-body').disabled === false && document.querySelector('#f1').value === '');
    }
    assert.deepEqual(errors, []);
    assert.equal(posted.length, 2);
    for (const { body } of posted) assert.match(body.submissionKey, /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
    assert.notEqual(posted[0].body.submissionKey, posted[1].body.submissionKey);
    await page.close();
});

test('a class\'s validation message shows as usual and the values stay', async () => {
    const { page, posted } = await mount({ submit: () => ({ status: 422, body: { status: 422, errors: { '': ['Please confirm you are not a robot.'] } } }) });
    await ready(page);
    await page.locator('#f1').fill('Pat');
    await save(page);
    await page.getByRole('alert').getByText('Please confirm you are not a robot.').waitFor();
    assert.equal(await page.locator('#f1').inputValue(), 'Pat');
    assert.equal(await page.locator('.app-block-signin').count(), 0);
    assert.equal(posted.length, 1);
    await page.close();
});

test('a class refusing an anonymous visitor on the first load leaves the notice and no form', async () => {
    const { page, errors } = await mount({ loadStatus: 403 });
    await page.getByRole('status').getByText('Sign in to use this form.').waitFor();
    assert.equal(await page.locator('[data-app-form]').count(), 0);
    assert.equal(await page.getByRole('alert').count(), 0);
    assert.deepEqual(errors, []);
    await page.close();
});

test('a class refusing an anonymous visitor on submit shows the notice, not an error, and keeps what they typed', async () => {
    for (const body of [{ status: 403 }, undefined]) {
        const { page, errors } = await mount({ submit: () => ({ status: 403, body }) });
        await ready(page);
        await page.locator('#f1').fill('Pat');
        await save(page);
        await page.locator('.app-form-message .app-block-signin').getByText('Sign in to use this form.').waitFor();
        assert.equal(await page.locator('.app-form-errors').isHidden(), true);
        assert.equal(await page.locator('#f1').inputValue(), 'Pat');
        assert.deepEqual(errors, []);
        await page.close();
    }
});

test('someone signed in still gets the plain refusal, on load and on submit', async () => {
    const refused = await mount({ anonymous: false, loadStatus: 403 });
    await refused.page.getByRole('alert').getByText('You do not have access to this form.').waitFor();
    assert.equal(await refused.page.locator('[data-app-form]').count(), 1);
    assert.equal(await refused.page.locator('.app-block-signin').count(), 0);
    await refused.page.close();
    for (const body of [{ status: 403 }, undefined]) {
        const { page } = await mount({ anonymous: false, submit: () => ({ status: 403, body }) });
        await ready(page);
        await save(page);
        await page.getByRole('alert').getByText('This operation is not permitted.').waitFor();
        await page.close();
    }
});

test('an antiforgery refusal with no body says what to do instead of failing to read it', async () => {
    const { page, errors } = await mount({ submit: () => ({ status: 400 }) });
    await ready(page);
    await page.locator('#f1').fill('Pat');
    await save(page);
    await page.getByRole('alert').getByText('The operation failed. Reload the page if the record has changed.').waitFor();
    assert.equal(await page.locator('#f1').inputValue(), 'Pat');
    assert.deepEqual(errors, []);
    await page.close();
});

test('an Attachments block whose record refuses an anonymous visitor shows the notice', async () => {
    const page = await browser.newPage();
    await page.route('**/*', async route => {
        const url = new URL(route.request().url());
        if (url.pathname.endsWith('/runtime.js')) return route.fulfill({ contentType: 'text/javascript', body: runtime });
        if (url.pathname === '/files/') return route.fulfill({ contentType: 'text/html', body: `<!doctype html><title>Files</title>
            <section class="app-attachments" data-app-attachments data-api="/api/attachments/page/block" data-owner="page" data-key-parameter="id" data-read-only="false" data-anonymous="true">
                <div class="app-attachments-message" role="status"></div><div class="app-attachments-errors" role="alert" hidden></div><ul class="app-attachments-list"></ul>
            </section>
            <script type="module" src="/App_Plugins/ApplicationBlocks/runtime.js"></script>` });
        return route.fulfill({ status: 403, contentType: 'application/json', body: JSON.stringify({ detail: 'You do not have access to these files.' }) });
    });
    await page.goto('http://localhost/files/');
    await page.getByRole('status').getByText('Sign in to see the files.').waitFor();
    assert.equal(await page.locator('[data-app-attachments]').count(), 0);
    await page.close();
});
