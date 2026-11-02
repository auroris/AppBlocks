import { backofficeRoute } from "./backoffice-harness.mjs";
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
const assets = process.env.UMBRACO_ASSETS || resolve(process.env.USERPROFILE || process.env.HOME, '.nuget/packages/umbraco.cms.staticassets/18.3.0/staticwebassets');
let browser;
before(async () => { browser = await chromium.launch({ headless: true, ...(existsSync(chromium.executablePath()) ? {} : { executablePath: process.env.BROWSER_PATH || 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe' }) }); });
after(async () => browser?.close());

async function mount({ value = '', readonly = false, fail = false } = {}) {
    const page = await browser.newPage();
    const errors = [], requests = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.route('**/*', async route => {
        if (await backofficeRoute(route, assets, "test-token")) return;
        const path = new URL(route.request().url()).pathname;
        const js = body => route.fulfill({ contentType: 'text/javascript', body });
        if (path === '/sub/editor') return route.fulfill({ contentType: 'text/html', body: `<script type="importmap">{"imports":{ "@umbraco-cms/backoffice/http-client":"/http-client.js", "@umbraco-cms/backoffice/repository":"/repository.js",
            "@umbraco-cms/backoffice/lit-element":"/umb.js", "@umbraco-cms/backoffice/external/lit":"/lit.js",
            "@umbraco-cms/backoffice/event":"/umb.js", "@umbraco-cms/backoffice/auth":"/umb.js", "@umbraco-cms/backoffice/document":"/umb.js"
        }}</script><script type="module">
            import '/sub/App_Plugins/AppBlocks/table-picker.js';
            const editor = document.createElement('appblocks-table-picker');
            editor.value = ${JSON.stringify(value)}; editor.readonly = ${readonly};
            window.changes = []; editor.addEventListener('change', () => window.changes.push(editor.value));
            document.body.append(editor);
        </script>` });
        if (path === '/lit.js') return js(await readFile(resolve(assets, 'umbraco/backoffice/external/lit/index.js'), 'utf8'));
        if (path === '/umb.js') return js(`import {LitElement} from '/lit.js';
            export class UmbLitElement extends LitElement {
                consumeContext(token, callback) { queueMicrotask(() => callback(token === 'auth' ? {getLatestToken: async () => 'test-token'} : {data: {}, getData: () => ({values:[{alias:'connection', value:window.connection || 'Business'}]})})); }
                observe(data, callback) { window.changeConnection = value => { window.connection=value; callback(); }; callback(); }
            }
            export const UMB_AUTH_CONTEXT='auth', UMB_DOCUMENT_WORKSPACE_CONTEXT='document';
            export class UmbChangeEvent extends Event { constructor() { super('change', {bubbles:true}); } }`);
        if (path.includes('/App_Plugins/')) return js(await readFile(resolve(root, 'src/AppBlocks/wwwroot', path.slice('/sub/'.length)), 'utf8'));
        requests.push({url: route.request().url(), headers: route.request().headers()});
        return route.fulfill({ status: fail ? 422 : 200, contentType: 'application/json', body: JSON.stringify(fail ? {detail:'Connection unavailable'} : [{name:'sales.Orders', value:'"sales"."Orders"'}]) });
    });
    await page.goto('http://app.test/sub/editor');
    await page.waitForFunction(() => document.querySelector('appblocks-table-picker')?.loading === false);
    return {page, errors, requests};
}

test('row source selects a table and stores a SELECT as the same plain string', async () => {
    const {page, errors, requests} = await mount();
    await page.getByLabel('Database table', {exact:true}).selectOption({label:'sales.Orders'});
    assert.equal(await page.getByLabel('Table name or SELECT').inputValue(), '"sales"."Orders"');
    const sql = 'SELECT id, title\nFROM "sales"."Orders"\nWHERE owner = :USER_KEY';
    await page.getByLabel('Table name or SELECT').fill(sql);
    assert.equal(await page.evaluate(() => window.changes.at(-1)), sql);
    assert.ok(requests.some(request => request.url.includes('connection=Business')));
    assert.ok(requests.every(request => request.headers.authorization === 'Bearer test-token'));
    await page.evaluate(() => window.changeConnection('Other'));
    await page.waitForFunction(() => document.querySelector('appblocks-table-picker')?.loading === false);
    assert.equal(await page.getByLabel('Table name or SELECT').inputValue(), sql);
    assert.deepEqual(errors, []);
    await page.close();
    const reopened = await mount({value:sql});
    assert.equal(await reopened.page.getByLabel('Table name or SELECT').inputValue(), sql);
    assert.deepEqual(await reopened.page.evaluate(() => window.changes), []);
    await reopened.page.close();
});

test('row source supports 20,000 characters without truncating a saved SELECT', async () => {
    const sql = 'SELECT 1 /*' + 'x'.repeat(19986) + ' */';
    assert.equal(sql.length, 20000);
    const {page} = await mount({value:sql});
    const editor = page.getByLabel('Table name or SELECT');
    assert.equal(await editor.getAttribute('maxlength'), '20000');
    assert.equal(await editor.inputValue(), sql);
    await editor.fill(sql.replace('SELECT 1', 'SELECT 2'));
    assert.equal((await page.evaluate(() => window.changes.at(-1))).length, 20000);
    await page.close();
});

test('metadata failure preserves the saved source and still permits SQL editing', async () => {
    const {page, errors} = await mount({value:'SELECT id FROM Orders', fail:true});
    assert.match(await page.getByRole('alert').textContent(), /Connection unavailable/);
    assert.equal(await page.getByLabel('Table name or SELECT').inputValue(), 'SELECT id FROM Orders');
    await page.getByLabel('Table name or SELECT').fill('SELECT title FROM Orders');
    assert.equal(await page.evaluate(() => window.changes.at(-1)), 'SELECT title FROM Orders');
    assert.deepEqual(errors, []);
    await page.close();
});

test('read-only row sources cannot change through either input', async () => {
    const {page} = await mount({value:'SELECT id FROM Orders', readonly:true});
    assert.ok(await page.getByLabel('Database table', {exact:true}).isDisabled());
    assert.ok(await page.getByLabel('Table name or SELECT').isDisabled());
    assert.deepEqual(await page.evaluate(() => window.changes), []);
    await page.close();
});
