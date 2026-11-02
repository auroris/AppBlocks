import { backofficeRoute } from "./backoffice-harness.mjs";
import assert from 'node:assert/strict';
import { test, before, after } from 'node:test';
import { createRequire } from 'node:module';
import { readFile, rm, stat } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { spawn } from 'node:child_process';
import { tmpdir } from 'node:os';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// A real site renders the block: the host below serves AppBlocks.Tests' Anti-spam site, built beforehand, with stand-ins for the
// verification services. Only the third-party scripts are faked here, so nothing reaches the network. Set APPBLOCKS_ANTISPAM_REAL=1
// to also load the real widgets with the test site keys each service publishes, and verify them for real.
const root = fileURLToPath(new URL('../../', import.meta.url));
const require = createRequire(resolve(process.env.INTERACTIVE_REPORTS_ROOT || resolve(root, '../Interactive-Reports'), 'package.json'));
const { chromium } = require('playwright');
const pluginRoot = resolve(root, 'src/AppBlocks/wwwroot');
const assets = resolve(process.env.UMBRACO_ASSETS || resolve(process.env.USERPROFILE || process.env.HOME, '.nuget/packages/umbraco.cms.staticassets/18.3.0/staticwebassets'), 'umbraco/backoffice');
const host = resolve(root, 'tests/AppBlocks.Tests/bin/Debug/net10.0', process.platform === 'win32' ? 'AppBlocks.Tests.exe' : 'AppBlocks.Tests');
const real = process.env.APPBLOCKS_ANTISPAM_REAL === '1';
const skipSite = existsSync(host) ? false : `build tests/AppBlocks.Tests first (${host} is missing)`;

let browser, site, address, control;
before(async () => { browser = await chromium.launch({ headless: true, ...(existsSync(chromium.executablePath()) ? {} : { executablePath: process.env.BROWSER_PATH || 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe' }) }); });
after(async () => {
    await browser?.close();
    if (site) {
        await rm(control, { force: true });
        const exited = new Promise(done => site.once('exit', done));
        const late = setTimeout(() => site.kill(), 30000);
        await exited;
        clearTimeout(late);
    }
});

async function startSite() {
    if (address) return address;
    control = resolve(process.env.ANTISPAM_TEMP || tmpdir(), `appblocks-antispam-${process.pid}.address`);
    await rm(control, { force: true });
    site = spawn(host, ['-class', 'AppBlocks.Tests.AntiSpamBrowserHost', '-explicit', 'only', '-noLogo'], { cwd: resolve(host, '..'), stdio: 'ignore', env: { ...process.env, APPBLOCKS_ANTISPAM_HOST: control } });
    for (let waited = 0; waited < 180; waited++) {
        if (site.exitCode !== null) throw new Error(`The site host stopped with exit code ${site.exitCode}.`);
        if (existsSync(control) && (await stat(control)).size > 0) { address = (await readFile(control, 'utf8')).trim(); return address; }
        await new Promise(done => setTimeout(done, 1000));
    }
    throw new Error('The site host did not start in time.');
}

// Stands in for each service's script, with the same three calls the runtime makes of the real ones. A test sets window.nextToken to decide what the widget hands over.
const fakeWidget = name => `(() => {
    window.widgetCalls = window.widgetCalls ?? [];
    const widgets = [];
    window.${name} = {
        render(element, options) {
            window.widgetCalls.push({ call: 'render', api: '${name}', options: JSON.parse(JSON.stringify(options)), hasExpiry: typeof options['expired-callback'] === 'function', inForm: !!element.closest('form') });
            const control = document.createElement('input');
            control.type = 'hidden'; control.name = element.dataset.tokenField; control.value = window.nextToken ?? '';
            element.append(control);
            return widgets.push(control) - 1;
        },
        reset(id) {
            window.widgetCalls.push({ call: 'reset', api: '${name}', id });
            widgets[id].value = window.nextToken ?? '';
        },
    };
})();`;
const fakeExecute = `window.grecaptcha = { widgetCalls: [], ready(run) { run(); }, execute(key, options) { window.executed = { key, ...options }; return Promise.resolve(window.nextToken ?? 'good'); } };`;
const scripts = {
    'https://standin.example.test/api.js?render=explicit': fakeWidget('standin'),
    'https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit': fakeWidget('turnstile'),
    'https://www.google.com/recaptcha/api.js?render=explicit': fakeWidget('grecaptcha'),
    'https://js.hcaptcha.com/1/api.js?render=explicit&recaptchacompat=off': fakeWidget('hcaptcha'),
    'https://www.google.com/recaptcha/api.js?render=recaptcha-three-key': fakeExecute,
};

async function open(path, { blocked = false, nextToken = '' } = {}) {
    const base = await startSite();
    const page = await browser.newPage();
    const errors = [], writes = [], outside = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.addInitScript(token => { window.nextToken = token; }, nextToken);
    await page.route('**/*', async route => {
        if (await backofficeRoute(route, assets, "a-token")) return;
        const request = route.request(), url = new URL(request.url());
        if (url.origin === new URL(base).origin) {
            // The test site serves no static files of its own, so the runtime comes straight from the source.
            if (url.pathname.startsWith('/App_Plugins/')) return route.fulfill({ contentType: url.pathname.endsWith('.css') ? 'text/css' : 'text/javascript', body: await readFile(resolve(pluginRoot, url.pathname.slice(1)), 'utf8') });
            if (request.method() !== 'GET') writes.push({ method: request.method(), url: url.pathname, body: request.postDataJSON() });
            return route.continue();
        }
        outside.push(url.href);
        if (!blocked && scripts[url.href]) return route.fulfill({ contentType: 'text/javascript', body: scripts[url.href] });
        return route.abort();
    });
    await page.goto(new URL(path, base).href);
    return { page, errors, writes, outside };
}

const ready = (page, form = page.locator('[data-app-form]').first()) => form.locator('.app-form-body:not([disabled])').waitFor();
const named = (form, name) => form.locator(`input[name="${name}"]`);

test('the block sits inside the form, its widget renders with the block\'s options, and a token gets the form through', { skip: skipSite }, async () => {
    const { page, errors, writes, outside } = await open('/standin/', { nextToken: 'good' });
    const form = page.locator('[data-app-form]');
    await ready(page, form);
    assert.equal(await form.locator('[data-app-antispam]').count(), 1);
    await page.waitForFunction(() => window.widgetCalls?.some(call => call.call === 'render'));
    const render = await page.evaluate(() => window.widgetCalls.find(call => call.call === 'render'));
    assert.equal(render.inForm, true);
    assert.equal(render.options.sitekey, 'standin-site-key');
    assert.equal(render.options.theme, 'dark');
    assert.equal(render.hasExpiry, true);
    assert.deepEqual(outside, ['https://standin.example.test/api.js?render=explicit']);

    await form.getByLabel('Your name').fill('Pat');
    await form.getByRole('button', { name: 'Save' }).click();
    await form.getByRole('status').getByText('Saved.').waitFor();
    assert.equal(writes.length, 1);
    assert.equal(writes[0].body.formData['standin-token'], 'good');
    assert.deepEqual(writes[0].body.values.name, 'Pat');
    assert.equal(writes[0].body.values['standin-token'], undefined);
    // Whatever the answer, that token has been seen, so the widget gets a new one for the next message.
    await page.waitForFunction(() => window.widgetCalls.some(call => call.call === 'reset'));
    assert.deepEqual(errors, []);
    await page.close();
});

test('a refused token shows the visitor a message, keeps what they typed and asks the widget for another', { skip: skipSite }, async () => {
    const { page, errors, writes } = await open('/standin/', { nextToken: 'bad' });
    const form = page.locator('[data-app-form]');
    await ready(page, form);
    await page.waitForFunction(() => window.widgetCalls?.length);
    await form.getByLabel('Your name').fill('Pat');
    await form.getByRole('button', { name: 'Save' }).click();
    await form.getByRole('alert').getByText('We could not confirm this came from a person').waitFor();
    assert.equal(await form.getByLabel('Your name').inputValue(), 'Pat');
    assert.equal(await page.evaluate(() => window.widgetCalls.filter(call => call.call === 'reset').length), 1);
    // An outage reads differently: it is not the visitor's doing.
    await page.evaluate(() => { window.nextToken = 'outage'; window.standin.reset(0); });
    await form.getByRole('button', { name: 'Save' }).click();
    await form.getByRole('alert').getByText('We cannot check submissions just now').waitFor();
    assert.equal(writes.length, 2);
    assert.deepEqual(errors, []);
    await page.close();
});

test('with no token yet the visitor is asked to finish the check and nothing is sent, and the server refuses anything that skips the runtime', { skip: skipSite }, async () => {
    const { page, writes } = await open('/standin/');
    const form = page.locator('[data-app-form]');
    await ready(page, form);
    await page.waitForFunction(() => window.widgetCalls?.length);
    await form.getByLabel('Your name').fill('Pat');
    await form.getByRole('button', { name: 'Save' }).click();
    await form.getByRole('alert').getByText('Complete the anti-spam check').waitFor();
    assert.equal(writes.length, 0);
    // A hand-made request from this very page, past the runtime.
    const status = await page.evaluate(async () => {
        const url = document.querySelector('[data-app-form]').dataset.api;
        const loaded = await (await fetch(url)).json();
        const response = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json', RequestVerificationToken: loaded.antiforgeryToken },
            body: JSON.stringify({ values: { name: 'Pat' }, definitionVersion: loaded.definitionVersion }) });
        return { status: response.status, body: await response.json() };
    });
    assert.equal(status.status, 422);
    assert.match(status.body.errors[''][0], /could not confirm/);
    await page.close();
});

test('a blocked script leaves the form usable and says why it cannot send', { skip: skipSite }, async () => {
    const { page, writes } = await open('/standin/', { blocked: true });
    const form = page.locator('[data-app-form]');
    await ready(page, form);
    await form.getByLabel('Your name').fill('Pat');
    await form.getByRole('button', { name: 'Save' }).click();
    await form.getByRole('alert').getByText('Complete the anti-spam check').waitFor();
    // Once the script has had its chance, the visitor is told it is not coming, rather than to finish something they cannot see.
    await new Promise(done => setTimeout(done, 15500));
    await form.getByRole('button', { name: 'Save' }).click();
    await form.getByRole('alert').getByText('The anti-spam check could not load').waitFor();
    assert.equal(writes.length, 0);
    assert.equal(await form.getByLabel('Your name').inputValue(), 'Pat');
    await page.close();
});

test('the honeypot is out of sight and out of reach, a person passes without noticing, and a bot that fills it is refused', { skip: skipSite }, async () => {
    const { page, errors, writes, outside } = await open('/honeypot/');
    const form = page.locator('[data-app-form]');
    await ready(page, form);
    const trap = form.locator('.app-antispam-hp input');
    assert.equal(await trap.count(), 1);
    assert.equal(await trap.getAttribute('tabindex'), '-1');
    assert.equal(await trap.getAttribute('autocomplete'), 'off');
    assert.equal(await form.locator('.app-antispam-hp').getAttribute('aria-hidden'), 'true');
    const box = await trap.boundingBox();
    assert.ok(!box || box.x + box.width < 0, 'The honeypot should be off the page.');
    // Tabbing through the form never lands on it.
    await form.getByLabel('Your name').focus();
    await page.keyboard.press('Tab');
    assert.equal(await page.evaluate(() => document.activeElement?.classList.contains('app-antispam-hp') || document.activeElement?.closest('.app-antispam-hp') !== null), false);
    assert.deepEqual(outside, []);

    const name = await trap.getAttribute('name');
    await form.getByLabel('Your name').fill('Pat');
    await form.getByRole('button', { name: 'Save' }).click();
    await form.getByRole('status').getByText('Saved.').waitFor();
    assert.equal(writes.at(-1).body.formData[name], '');

    await trap.evaluate(input => { input.value = 'https://spam.example'; });
    await form.getByLabel('Your name').fill('Bot');
    await form.getByRole('button', { name: 'Save' }).click();
    await form.getByRole('alert').getByText('We could not confirm this came from a person').waitFor();
    assert.deepEqual(errors, []);
    await page.close();
});

test('each form on a page gets its own widget from one copy of each script, and a real adapter\'s token reaches the server', { skip: skipSite }, async () => {
    const { page, errors, outside } = await open('/widgets/', { nextToken: 'a-token' });
    await page.waitForFunction(() => window.widgetCalls?.filter(call => call.call === 'render').length === 4);
    const renders = await page.evaluate(() => window.widgetCalls.filter(call => call.call === 'render'));
    assert.deepEqual(renders.map(call => [call.api, call.options.sitekey]).sort(), [
        ['grecaptcha', 'recaptcha-two-key'], ['hcaptcha', 'hcaptcha-key'], ['turnstile', '0xTurnstileOne'], ['turnstile', '0xTurnstileTwo'],
    ]);
    assert.equal(renders.find(call => call.options.sitekey === '0xTurnstileOne').options.size, 'compact');
    assert.equal(renders.find(call => call.options.sitekey === 'recaptcha-two-key').options.theme, 'dark');
    assert.equal(new Set(outside).size, outside.length, 'A script was requested twice.');
    assert.equal(outside.length, 3);
    assert.deepEqual(errors, []);
    await page.close();
});

test('reCAPTCHA v3 shows nothing, takes its token as the form is sent and holds it apart from the fields', { skip: skipSite }, async () => {
    const { page, errors, writes } = await open('/threes/', { nextToken: 'a-v3-token' });
    const form = page.locator('[data-app-form]');
    await ready(page, form);
    assert.equal(await form.locator('[data-app-antispam]').isHidden(), true);
    await page.waitForFunction(() => window.grecaptcha);
    await form.getByLabel('Your name').fill('Pat');
    await form.getByRole('button', { name: 'Save' }).click();
    await page.waitForFunction(() => window.executed);
    assert.deepEqual(await page.evaluate(() => window.executed), { key: 'recaptcha-three-key', action: 'appblocks_form' });
    await form.locator('.app-form-body:not([disabled])').waitFor();
    assert.equal(writes[0].body.formData['g-recaptcha-response'], 'a-v3-token');
    assert.equal(writes[0].body.values['g-recaptcha-response'], undefined);
    assert.deepEqual(errors, []);
    await page.close();
});

// The real widgets and the real verification, with the test keys each service publishes. Only when asked, since it needs the network.
for (const [service, path, field] of [['turnstile', '/real-turnstile/', 'cf-turnstile-response'], ['recaptchaV2', '/real-recaptchav2/', 'g-recaptcha-response'], ['hcaptcha', '/real-hcaptcha/', 'h-captcha-response']]) {
    test(`the real ${service} widget renders in the form with its test site key and its token verifies for real`, { skip: skipSite || (real ? false : 'set APPBLOCKS_ANTISPAM_REAL=1 to reach the real services') }, async () => {
        const base = await startSite();
        const page = await browser.newPage();
        const errors = [];
        page.on('pageerror', error => errors.push(error.message));
        await page.route(new URL('/App_Plugins/**', base).href, async route => {
        if (await backofficeRoute(route, assets, "a-token")) return;
            const url = new URL(route.request().url());
            return route.fulfill({ contentType: url.pathname.endsWith('.css') ? 'text/css' : 'text/javascript', body: await readFile(resolve(pluginRoot, url.pathname.slice(1)), 'utf8') });
        });
        await page.goto(new URL(path, base).href);
        const form = page.locator('[data-app-form]');
        await ready(page, form);
        // reCAPTCHA's and hCaptcha's test keys still want the box ticked, inside their frame.
        if (service !== 'turnstile') {
            const frame = page.frameLocator(service === 'hcaptcha' ? 'iframe[src*="frame=checkbox"]' : 'iframe[title="reCAPTCHA"]');
            await frame.locator(service === 'hcaptcha' ? '#checkbox' : '#recaptcha-anchor').click({ timeout: 30000 });
        }
        await page.waitForFunction(name => [...document.querySelector('[data-app-form]').elements].some(control => control.name === name && control.value), field, { timeout: 30000 });
        await form.getByLabel('Your name').fill('Pat');
        await form.getByRole('button', { name: 'Save' }).click();
        await form.getByRole('status').getByText('Saved.').waitFor({ timeout: 15000 });
        await page.close();
    });
}

// The settings screen, with only the dashboard's own code and a server that answers as told.
async function mountDashboard(services, onSave) {
    const page = await browser.newPage();
    const errors = [], saves = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.route('**/*', async route => {
        if (await backofficeRoute(route, assets, "a-token")) return;
        const url = new URL(route.request().url()), path = url.pathname;
        const js = body => route.fulfill({ contentType: 'text/javascript', body });
        if (path === '/sub/dashboard') return route.fulfill({ contentType: 'text/html', body: `<script type="importmap">{"imports":{ "@umbraco-cms/backoffice/http-client":"/http-client.js", "@umbraco-cms/backoffice/repository":"/repository.js",
            "@umbraco-cms/backoffice/lit-element":"/umb.js", "@umbraco-cms/backoffice/auth":"/umb.js", "@umbraco-cms/backoffice/components":"/components.js",
            "@umbraco-cms/backoffice/external/lit":"/vendor/lit/index.js", "@umbraco-cms/backoffice/external/marked":"/vendor/marked/index.js",
            "@umbraco-cms/backoffice/external/dompurify":"/vendor/dompurify/index.js"
        }}</script><script type="module" src="/workspace-harness.js"></script><appblocks-dashboard></appblocks-dashboard>` });
        if (path.startsWith('/vendor/')) return js(await readFile(resolve(assets, 'external', path.slice('/vendor/'.length)), 'utf8'));
        if (path === '/umb.js') return js(`import {LitElement} from '/vendor/lit/index.js';
            export class UmbLitElement extends LitElement { consumeContext(token, callback) { queueMicrotask(() => callback({getLatestToken: async () => 'a-token'})); } }
            export const UMB_AUTH_CONTEXT='auth';`);
        if (path === '/components.js') return js(`customElements.define('uui-tab', class extends HTMLElement { connectedCallback() { this.setAttribute('role', 'tab'); } });`);
        if (path.includes('/App_Plugins/')) return js(await readFile(resolve(pluginRoot, path.slice('/sub/'.length)), 'utf8'));
        if (path.endsWith('/anti-spam')) {
            if (route.request().method() === 'POST') { saves.push(route.request().postDataJSON()); services = onSave(saves.at(-1), services); }
            return route.fulfill({ contentType: 'application/json', body: JSON.stringify(services) });
        }
        return route.fulfill({ contentType: 'application/json', body: JSON.stringify(path.endsWith('/forms') ? { forms: [], problems: [] } : []) });
    });
    await page.goto('http://localhost/sub/dashboard');
    await page.waitForFunction(() => document.querySelector('appblocks-dashboard')?.loading === false);
    return { page, errors, saves };
}

test('the settings screen shows whether a secret is set and what is wrong, never a value, and takes new keys', async () => {
    const services = [
        { alias: 'turnstile', name: 'Cloudflare Turnstile', secret: 'none', siteKey: null, siteKeyManaged: false, problem: 'No secret key is set.', problemSince: '2026-10-31T00:00:00Z' },
        { alias: 'hcaptcha', name: 'hCaptcha', secret: 'saved', siteKey: 'public-site-key', siteKeyManaged: false, problem: null, problemSince: null },
        { alias: 'recaptchaV2', name: 'Google reCAPTCHA v2', secret: 'configuration', siteKey: 'configured-key', siteKeyManaged: true, problem: null, problemSince: null },
    ];
    const { page, errors, saves } = await mountDashboard(services, (save, current) => current.map(row => row.alias === save.service ? { ...row, secret: 'saved', problem: null, siteKey: save.siteKey || row.siteKey } : row));
    await page.getByRole('tab', { name: 'Anti-spam' }).click();
    const row = alias => page.locator(`tr[data-service="${alias}"]`);
    await row('turnstile').waitFor();
    assert.match(await row('turnstile').textContent(), /Not set/);
    assert.match(await row('turnstile').getByRole('alert').textContent(), /No secret key is set/);
    assert.match(await row('hcaptcha').textContent(), /Set here, encrypted/);
    assert.match(await row('hcaptcha').textContent(), /public-site-key/);
    assert.match(await row('recaptchaV2').textContent(), /Set in site configuration/);
    // Everything configuration holds, so there is nothing to change here.
    assert.equal(await row('recaptchaV2').getByRole('button').count(), 0);

    await row('hcaptcha').getByRole('button', { name: 'Set keys' }).click();
    const secret = page.getByLabel('Secret key');
    assert.equal(await secret.getAttribute('type'), 'password');
    assert.equal(await secret.inputValue(), '');
    assert.match(await secret.getAttribute('placeholder'), /Leave blank/);
    await page.getByRole('button', { name: 'Remove the saved secret key' }).waitFor();
    await page.getByRole('button', { name: 'Cancel' }).click();

    await row('turnstile').getByRole('button', { name: 'Set keys' }).click();
    await page.getByLabel('Site key').fill('new-site-key');
    await page.getByLabel('Secret key').fill('a-new-secret');
    await page.getByRole('button', { name: 'Save keys' }).click();
    await page.getByText('Cloudflare Turnstile keys saved.').waitFor();
    assert.deepEqual(saves, [{ service: 'turnstile', siteKey: 'new-site-key', secretKey: 'a-new-secret' }]);
    assert.doesNotMatch(await page.locator('appblocks-dashboard').evaluate(element => element.shadowRoot.innerHTML), /a-new-secret/);
    assert.match(await row('turnstile').textContent(), /new-site-key/);
    assert.deepEqual(errors, []);
    await page.close();
});
