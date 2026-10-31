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
const markdown = await readFile(resolve(root, 'docs/business-rules.md'), 'utf8');
let browser;
before(async () => { browser = await chromium.launch({ headless: true, ...(existsSync(chromium.executablePath()) ? {} : { executablePath: process.env.BROWSER_PATH || 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe' }) }); });
after(async () => browser?.close());

async function mount({ guide = markdown, failOnce = false } = {}) {
    const page = await browser.newPage();
    const errors = [], guideRequests = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.addInitScript(() => {
        window.copied = '';
        Object.defineProperty(navigator, 'clipboard', { value: { writeText: async text => { window.copied = text; } } });
        // The backoffice intercepts anchor clicks to navigate workspaces.
        window.routed = false;
        document.addEventListener('click', event => {
            if (event.composedPath().some(node => node instanceof HTMLAnchorElement)) window.routed = true;
        });
    });
    await page.route('**/*', async route => {
        const path = new URL(route.request().url()).pathname;
        const js = body => route.fulfill({contentType:'text/javascript', body});
        if (path === '/sub/dashboard') return route.fulfill({contentType:'text/html', body:`<script type="importmap">{"imports":{
            "@umbraco-cms/backoffice/lit-element":"/umb.js", "@umbraco-cms/backoffice/auth":"/umb.js", "@umbraco-cms/backoffice/components":"/components.js",
            "@umbraco-cms/backoffice/external/lit":"/vendor/lit/index.js", "@umbraco-cms/backoffice/external/marked":"/vendor/marked/index.js",
            "@umbraco-cms/backoffice/external/dompurify":"/vendor/dompurify/index.js"
        }}</script><script type="module" src="/sub/App_Plugins/AppBlocks/dashboard.js"></script><appblocks-dashboard></appblocks-dashboard>`});
        if (path.startsWith('/vendor/')) return js(await readFile(resolve(assets, 'external', path.slice('/vendor/'.length)), 'utf8'));
        if (path === '/umb.js') return js(`import {LitElement} from '/vendor/lit/index.js';
            export class UmbLitElement extends LitElement { consumeContext(token, callback) { queueMicrotask(() => callback({getLatestToken: async () => 'guide-token'})); } }
            export const UMB_AUTH_CONTEXT='auth';`);
        if (path === '/components.js') return js(`customElements.define('uui-tab', class extends HTMLElement { connectedCallback() { this.setAttribute('role', 'tab'); } });`);
        if (path.includes('/App_Plugins/')) return js(await readFile(resolve(root, 'src/AppBlocks/wwwroot', path.slice('/sub/'.length)), 'utf8'));
        if (path.endsWith('/guide')) {
            guideRequests.push(route.request().headers());
            if (failOnce && guideRequests.length === 1) return route.fulfill({status:503, contentType:'application/json', body:JSON.stringify({detail:'Please try again.'})});
            return route.fulfill({contentType:'text/markdown', body:guide});
        }
        const data = path.endsWith('/forms') ? {forms:[{name:'Example', pageName:'Page', resource:'example', fields:[], pageKey:'page', formKey:'form'}], problems:[]} : [];
        return route.fulfill({contentType:'application/json', body:JSON.stringify(data)});
    });
    await page.goto('http://localhost/sub/dashboard');
    await page.waitForFunction(() => document.querySelector('appblocks-dashboard')?.loading === false);
    return {page, errors, guideRequests};
}

test('business rules opens inside AppBlocks with every section, exact code, navigation and copy', async () => {
    const {page, errors, guideRequests} = await mount();
    await page.getByRole('tab', {name:'Business rules', exact:true}).click();
    const article = page.getByRole('article', {name:'Business rules for AppBlocks forms'});
    await article.getByRole('heading', {name:'Business rules for AppBlocks forms', exact:true}).waitFor();
    const headings = [...markdown.matchAll(/^## (.+)$/gm)].map(match => match[1].trim());
    for (const heading of headings) {
        await article.getByRole('heading', {name:heading, exact:true}).waitFor();
        assert.equal(await page.getByRole('navigation', {name:'Business rules sections'}).getByRole('link', {name:heading, exact:true}).count(), 1);
    }
    const snippets = [...markdown.matchAll(/```[^\r\n]*\r?\n([\s\S]*?)```/g)].map(match => match[1].replaceAll('\r\n', '\n'));
    assert.equal(await article.locator('pre code').count(), snippets.length);
    for (let i=0; i<snippets.length; i++) assert.equal(await article.locator('pre code').nth(i).textContent(), snippets[i]);
    await article.getByRole('button', {name:'Copy code', exact:true}).first().click();
    assert.equal(await page.evaluate(() => window.copied), snippets[0]);
    await article.getByRole('button', {name:'Copied', exact:true}).waitFor();
    await page.getByText('Code copied to clipboard.', {exact:true}).waitFor();
    await page.getByRole('navigation').getByRole('link', {name:'Ownership and record status', exact:true}).click();
    assert.equal(await page.locator('appblocks-business-rules').evaluate(element => element.shadowRoot.activeElement?.textContent), 'Ownership and record status');
    assert.equal(await page.evaluate(() => window.routed), false, 'Section links stay inside the help page');
    const downloaded = page.waitForEvent('download');
    await page.getByRole('button', {name:'Download Markdown', exact:true}).click();
    const download = await downloaded;
    assert.equal(download.suggestedFilename(), 'business-rules.md');
    assert.equal(await readFile(await download.path(), 'utf8'), markdown);
    assert.equal(guideRequests.length, 1);
    assert.equal(guideRequests[0].authorization, 'Bearer guide-token');
    assert.deepEqual(errors, []);
    await page.setViewportSize({width:390, height:844});
    assert.ok(await article.evaluate(element => element.scrollWidth <= element.clientWidth + 1), 'Code scrolls within the article on narrow screens');
    await page.close();
});

test('getting started and form samples link to the inline help', async () => {
    const {page, errors} = await mount();
    await page.getByRole('tab', {name:'Getting started', exact:true}).click();
    await page.getByRole('button', {name:'Read business rules', exact:true}).click();
    await page.getByRole('heading', {name:'Business rules for AppBlocks forms', exact:true}).waitFor();
    await page.getByRole('tab', {name:'Forms', exact:true}).click();
    await page.getByRole('button', {name:'View sample C#', exact:true}).click();
    await page.getByRole('button', {name:'Read business rules', exact:true}).click();
    await page.getByRole('heading', {name:'Business rules for AppBlocks forms', exact:true}).waitFor();
    assert.deepEqual(errors, []);
    await page.close();
});

test('help loading errors are recoverable without leaving the dashboard', async () => {
    const {page, errors, guideRequests} = await mount({failOnce:true});
    await page.getByRole('tab', {name:'Business rules', exact:true}).click();
    await page.getByRole('alert').getByText(/Please try again/).waitFor();
    await page.getByRole('button', {name:'Retry', exact:true}).click();
    await page.getByRole('heading', {name:'Business rules for AppBlocks forms', exact:true}).waitFor();
    assert.equal(guideRequests.length, 2);
    assert.deepEqual(errors, []);
    await page.close();
});

test('Markdown rendering removes executable HTML and preserves literal code', async () => {
    const guide = '# Help\n\n## Example\n\n<script>window.injected=true</script><img src="x" onerror="window.injected=true">\n\n[unsafe](javascript:alert(1))\n\n```csharp\nvar html = "<script>alert(1)</script>";\n```';
    const {page, errors} = await mount({guide});
    await page.getByRole('tab', {name:'Business rules', exact:true}).click();
    await page.getByRole('heading', {name:'Example', exact:true}).waitFor();
    const article = page.getByRole('article');
    assert.equal(await article.locator('script,[onerror],[href^="javascript:"]').count(), 0);
    assert.equal(await page.evaluate(() => window.injected), undefined);
    assert.equal(await article.locator('pre code').textContent(), 'var html = "<script>alert(1)</script>";\n');
    assert.deepEqual(errors, []);
    await page.close();
});
