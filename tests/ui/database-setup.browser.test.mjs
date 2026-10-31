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

const uploads = {
    alias: 'uploads', title: 'File uploads', connection: 'AppBlocks database (AppBlocksDb)', provider: 'sqlServer', state: 'action',
    summary: 'The tables are missing and this account is not allowed to create them.', lost: 'Without them, Attachments blocks show a notice.',
    tables: [{ name: 'APP_ATTACHMENT', state: 'missing', required: true }, { name: 'APP_ATTACHMENT_LINK', state: 'missing', required: true }],
    grantText: 'Run this in the database as its owner.', grantSql: 'GRANT CREATE TABLE TO [site];\n', hasScript: true,
};
const saved = {
    alias: 'saved-views', title: 'Saved report views', connection: 'AppBlocks database (AppBlocksDb)', provider: 'sqlServer', state: 'ready',
    summary: 'The tables are in place and this account can use them.', lost: 'Without them, people cannot save views.',
    tables: [{ name: 'IR_SAVED_REPORTS', state: 'ready', required: true }, { name: 'IR_ADMINISTRATORS', state: 'missing', required: false }],
    grantText: 'Run this in the database as its owner.', grantSql: 'GRANT CREATE TABLE TO [site];\n', hasScript: true,
};
const unsupported = { ...uploads, provider: 'postgres', state: 'unsupported', summary: 'File uploads run on SQLite and SQL Server only. Set Attachments:Connection.', grantText: null, grantSql: null, hasScript: false };

async function mountDashboard({ features = [uploads, saved], search = '', forbidden = false } = {}) {
    const page = await browser.newPage();
    const errors = [], calls = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.route('**/*', async route => {
        const url = new URL(route.request().url()), path = url.pathname;
        const js = body => route.fulfill({contentType:'text/javascript', body});
        if (path === '/sub/dashboard') return route.fulfill({contentType:'text/html', body:`<script type="importmap">{"imports":{
            "@umbraco-cms/backoffice/lit-element":"/umb.js", "@umbraco-cms/backoffice/auth":"/umb.js", "@umbraco-cms/backoffice/components":"/components.js",
            "@umbraco-cms/backoffice/external/lit":"/vendor/lit/index.js", "@umbraco-cms/backoffice/external/marked":"/vendor/marked/index.js",
            "@umbraco-cms/backoffice/external/dompurify":"/vendor/dompurify/index.js"
        }}</script><script type="module" src="/sub/App_Plugins/AppBlocks/dashboard.js"></script><appblocks-dashboard></appblocks-dashboard>`});
        if (path.startsWith('/vendor/')) return js(await readFile(resolve(assets, 'external', path.slice('/vendor/'.length)), 'utf8'));
        if (path === '/umb.js') return js(`import {LitElement} from '/vendor/lit/index.js';
            export class UmbLitElement extends LitElement { consumeContext(token, callback) { queueMicrotask(() => callback({getLatestToken: async () => 'setup-token'})); } }
            export const UMB_AUTH_CONTEXT='auth';`);
        if (path === '/components.js') return js(`customElements.define('uui-tab', class extends HTMLElement { connectedCallback() { this.setAttribute('role', 'tab'); } });`);
        if (path.includes('/App_Plugins/')) return js(await readFile(resolve(root, 'src/AppBlocks/wwwroot', path.slice('/sub/'.length)), 'utf8'));
        if (path.includes('/database-setup')) {
            calls.push({ path, authorization: route.request().headers().authorization, method: route.request().method() });
            if (forbidden) return route.fulfill({status:403, contentType:'application/json', body:'{}'});
            if (path.endsWith('/script')) return route.fulfill({contentType:'text/plain', body:'-- AppBlocks: File uploads\nCREATE TABLE dbo.APP_ATTACHMENT;\n'});
            return route.fulfill({contentType:'application/json', body:JSON.stringify(features)});
        }
        const data = path.endsWith('/forms') ? {forms:[], problems:[]} : [];
        return route.fulfill({contentType:'application/json', body:JSON.stringify(data)});
    });
    await page.goto('http://localhost/sub/dashboard' + search);
    await page.waitForFunction(() => document.querySelector('appblocks-dashboard')?.loading === false);
    return {page, errors, calls};
}

test('the setup tab shows each feature, both remedies, and a script to view and download', async () => {
    const {page, errors, calls} = await mountDashboard();
    assert.equal(calls.length, 0, 'The databases are not looked at until the tab is opened');
    await page.getByRole('tab', {name:'Database setup', exact:true}).click();
    const box = page.locator('[data-feature="uploads"]');
    await box.getByRole('heading', {name:'File uploads', exact:true}).waitFor();
    assert.equal(calls[0].path, '/sub/umbraco/management/api/v1/appblocks/database-setup');
    assert.equal(calls[0].authorization, 'Bearer setup-token');
    assert.equal(calls[0].method, 'GET');
    await box.getByText('AppBlocks database (AppBlocksDb)', {exact:true}).waitFor();
    await box.getByText('SQL Server', {exact:true}).waitFor();
    await box.getByText('Needs setup', {exact:true}).waitFor();
    await box.getByText('APP_ATTACHMENT_LINK', {exact:true}).waitFor();
    assert.equal(await box.getByText('Not found', {exact:true}).count(), 2);
    await box.getByText('Without them, Attachments blocks show a notice.', {exact:true}).waitFor();

    // Needing setup, both remedies are open.
    await box.getByRole('heading', {name:'Option 1: let AppBlocks create the tables', exact:true}).waitFor();
    assert.match(await box.locator('pre code').first().textContent(), /GRANT CREATE TABLE TO \[site\]/);
    await box.getByRole('button', {name:'View script', exact:true}).click();
    await box.getByLabel('Script', {exact:true}).waitFor();
    assert.match(await box.getByLabel('Script', {exact:true}).textContent(), /CREATE TABLE dbo\.APP_ATTACHMENT/);
    assert.equal(calls.at(-1).path, '/sub/umbraco/management/api/v1/appblocks/database-setup/uploads/script');
    const downloaded = page.waitForEvent('download');
    await box.getByRole('button', {name:'Download script', exact:true}).click();
    assert.equal((await downloaded).suggestedFilename(), 'appblocks-uploads-sqlServer.sql');

    // A ready feature keeps them folded away, and says what its optional table is for.
    const ready = page.locator('[data-feature="saved-views"]');
    await ready.getByText('Ready', {exact:true}).waitFor();
    await ready.getByText('Used only by the saved report administration page.', {exact:true}).waitFor();
    assert.equal(await ready.locator('details').evaluate(element => element.open), false);
    assert.deepEqual(errors, []);
    await page.close();
});

test('an unsupported upload store shows why and no remedies it cannot use', async () => {
    const {page, errors} = await mountDashboard({ features:[unsupported] });
    await page.getByRole('tab', {name:'Database setup', exact:true}).click();
    const box = page.locator('[data-feature="uploads"]');
    await box.getByText('Not supported here', {exact:true}).waitFor();
    await box.getByText('PostgreSQL', {exact:true}).waitFor();
    await box.getByText(/Set Attachments:Connection/).waitFor();
    assert.equal(await box.getByRole('button', {name:'View script', exact:true}).count(), 0);
    assert.equal(await box.locator('details').count(), 0);
    assert.deepEqual(errors, []);
    await page.close();
});

test('the tab opens from the address the notices link to, and refreshes', async () => {
    const {page, errors, calls} = await mountDashboard({ search:'?tab=setup' });
    await page.locator('[data-feature="uploads"]').getByRole('heading', {name:'File uploads', exact:true}).waitFor();
    assert.equal(calls.length, 1);
    await page.getByRole('button', {name:'Refresh'}).or(page.locator('uui-button[label="Refresh"]')).first().click();
    for (let i = 0; i < 100 && calls.length < 2; i++) await page.waitForTimeout(50);
    assert.equal(calls.length, 2);
    assert.deepEqual(errors, []);
    await page.close();
});

test('someone who is not an administrator gets the screen\'s refusal, not a broken page', async () => {
    const {page, errors} = await mountDashboard({ forbidden:true });
    await page.getByRole('tab', {name:'Database setup', exact:true}).click();
    await page.getByRole('alert').getByText(/Only an Umbraco administrator/).waitFor();
    assert.deepEqual(errors, []);
    await page.close();
});

async function mountBlock(answer) {
    const page = await browser.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.route('**/*', async route => {
        const path = new URL(route.request().url()).pathname;
        if (path === '/page') return route.fulfill({contentType:'text/html', body:`<section data-app-attachments data-api="/api/attachments/p/b" data-owner="page" data-read-only="false" data-max-size="1000">
            <div class="app-attachments-message" role="status"></div><div class="app-attachments-errors" role="alert" hidden></div><ul class="app-attachments-list" aria-busy="true"></ul>
            <div class="app-attachments-upload" hidden><input type="file" data-attachment-file><button type="button" data-attachment-upload>Upload</button></div></section>
            <script type="module" src="/App_Plugins/ApplicationBlocks/runtime.js"></script>`});
        if (path.endsWith('/runtime.js')) return route.fulfill({contentType:'text/javascript', body:await readFile(resolve(root, 'src/AppBlocks/wwwroot/App_Plugins/ApplicationBlocks/runtime.js'), 'utf8')});
        if (path.startsWith('/api/attachments')) return route.fulfill({contentType:'application/json', body:JSON.stringify(answer)});
        return route.fulfill({status:404, body:''});
    });
    await page.goto('http://localhost/page');
    return {page, errors};
}

const message = "File storage isn't set up on this site, so files can't be shown or added here yet.";

test('without file storage a visitor gets a plain notice, no controls and no pointer', async () => {
    const {page, errors} = await mountBlock({ owner:{alias:'a', key:'1'}, canWrite:false, purposes:[], maxFileSize:1000, accept:null, items:[], storage:{ message, setupUrl:null } });
    await page.getByRole('status').getByText(message, {exact:true}).waitFor();
    assert.equal(await page.locator('.app-attachments-errors').isVisible(), false, 'It is a notice, not an error');
    assert.equal(await page.locator('.app-attachments-upload').isVisible(), false);
    assert.equal(await page.locator('[data-app-attachments] a').count(), 0);
    assert.deepEqual(errors, []);
    await page.close();
});

test('without file storage an administrator also gets the way to the setup screen', async () => {
    const setupUrl = '/umbraco/section/settings/workspace/appblocks?tab=setup';
    const {page, errors} = await mountBlock({ owner:{alias:'a', key:'1'}, canWrite:false, purposes:[], maxFileSize:1000, accept:null, items:[], storage:{ message, setupUrl } });
    const link = page.getByRole('status').getByRole('link', {name:'Open database setup', exact:true});
    await link.waitFor();
    assert.equal(await link.getAttribute('href'), setupUrl);
    assert.equal(await page.locator('.app-attachments-errors').isVisible(), false);
    assert.deepEqual(errors, []);
    await page.close();
});

test('with file storage the block is what it always was', async () => {
    const {page, errors} = await mountBlock({ owner:{alias:'a', key:'1'}, canWrite:true, purposes:[], maxFileSize:1000, accept:null, items:[], antiforgeryToken:'t' });
    await page.getByRole('status').getByText('No files yet.', {exact:true}).waitFor();
    assert.equal(await page.locator('.app-attachments-upload').isVisible(), true);
    assert.deepEqual(errors, []);
    await page.close();
});
