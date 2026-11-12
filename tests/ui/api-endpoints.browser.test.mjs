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

// A real site answers: AppBlocks.Tests' block site, built beforehand, with its orders. The endpoint is created and published through the
// management API as the backoffice saves it, and the route editor and Test button are the shipped elements, talking to that site with an
// administrator's token. Only Umbraco's workspace around them is a stand-in, holding the document's values.
const root = fileURLToPath(new URL('../../', import.meta.url));
const require = createRequire(resolve(process.env.INTERACTIVE_REPORTS_ROOT || resolve(root, '../Interactive-Reports'), 'package.json'));
const { chromium } = require('playwright');
const pluginRoot = resolve(root, 'src/AppBlocks/wwwroot');
const assets = resolve(process.env.UMBRACO_ASSETS || resolve(process.env.USERPROFILE || process.env.HOME, '.nuget/packages/umbraco.cms.staticassets/18.3.0/staticwebassets'), 'umbraco/backoffice');
const host = resolve(root, 'tests/AppBlocks.Tests/bin/Debug/net10.0', process.platform === 'win32' ? 'AppBlocks.Tests.exe' : 'AppBlocks.Tests');
const skipSite = existsSync(host) ? false : `build tests/AppBlocks.Tests first (${host} is missing)`;

let browser, site, control, settings;
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
    if (settings) return settings;
    control = resolve(process.env.API_TEMP || tmpdir(), `appblocks-api-${process.pid}.json`);
    await rm(control, { force: true });
    site = spawn(host, ['-class', 'AppBlocks.Tests.ApiEndpointBrowserHost', '-explicit', 'only', '-noLogo'], { cwd: resolve(host, '..'), stdio: 'ignore', env: { ...process.env, APPBLOCKS_API_HOST: control } });
    for (let waited = 0; waited < 180; waited++) {
        if (site.exitCode !== null) throw new Error(`The site host stopped with exit code ${site.exitCode}.`);
        if (existsSync(control) && (await stat(control)).size > 0) return settings = JSON.parse(await readFile(control, 'utf8'));
        await new Promise(done => setTimeout(done, 1000));
    }
    throw new Error('The site host did not start in time.');
}

async function management(path, method, body) {
    const response = await fetch(`${settings.address}/umbraco/management/api/v1/${path}`, {
        method, body: body && JSON.stringify(body), headers: {
            authorization: `Bearer ${settings.token}`, 'content-type': 'application/json',
            cookie: settings.cookies.map(cookie => `${cookie.name}=${cookie.value}`).join('; '),
        },
    });
    assert.ok(response.ok, `${method} ${path}: ${response.status} ${await response.clone().text()}`);
    return response;
}

const values = {
    alias: 'browser.lines', route: '', access: 'Public', queryParameters: 'item', orderBy: 'LINE_ID DESC',
    sql: 'SELECT LINE_ID, ITEM, QUANTITY FROM ORDER_LINES WHERE ORDER_ID = :PARAM.id AND (:PARAM.item IS NULL OR ITEM = :PARAM.item)',
};

/**
 * The endpoint's editors in a page of the site's own origin, around a workspace that holds the document's values. With
 * administrator false, the site answers that the User is not an AppBlocks administrator, as it does for an ordinary editor.
 */
async function open(document, { administrator = true } = {}) {
    const context = await browser.newContext();
    // The backoffice's cookies, as a browser signed in to it holds them; the endpoint itself is called without them.
    await context.addCookies(settings.cookies.map(cookie => ({ name: cookie.name, value: cookie.value, url: settings.address + cookie.path, httpOnly: cookie.httpOnly })));
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.route('**/*', async route => {
        if (await backofficeRoute(route, assets, settings.token, '')) return;
        const path = new URL(route.request().url()).pathname;
        const js = body => route.fulfill({ contentType: 'text/javascript', body });
        if (path === '/__endpoint-editor') return route.fulfill({ contentType: 'text/html', body: `<script type="importmap">{"imports":{ "@umbraco-cms/backoffice/http-client":"/http-client.js",
            "@umbraco-cms/backoffice/lit-element":"/umb.js", "@umbraco-cms/backoffice/external/lit":"/lit.js", "@umbraco-cms/backoffice/event":"/umb.js",
            "@umbraco-cms/backoffice/auth":"/umb.js", "@umbraco-cms/backoffice/document":"/umb.js", "@umbraco-cms/backoffice/entity":"/umb.js"
        }}</script><script type="module">
            import '/App_Plugins/DataSourceAuthoring/api-route.js';
            import '/App_Plugins/DataSourceAuthoring/api-endpoint-test.js';
            const document_ = ${JSON.stringify(document)};
            const listeners = [];
            window.workspace = {
                data: {}, observe: callback => listeners.push(callback),
                getData: () => ({ unique: document_.unique, values: Object.entries(document_.values).map(([alias, value]) => ({ alias, value })) }),
            };
            const route = document.createElement('appblocks-api-route');
            route.value = document_.values.route;
            route.addEventListener('change', () => { document_.values.route = route.value; listeners.forEach(listener => listener(window.workspace.getData())); });
            document.body.append(route, document.createElement('appblocks-api-endpoint-test'));
            window.ready = true;
        </script>` });
        if (path === '/lit.js') return js(await readFile(resolve(assets, 'external/lit/index.js'), 'utf8'));
        if (path === '/umb.js') return js(`import { LitElement } from '/lit.js';
            export const UMB_AUTH_CONTEXT = 'auth', UMB_DOCUMENT_WORKSPACE_CONTEXT = 'document', UMB_PARENT_ENTITY_CONTEXT = 'parent';
            export class UmbLitElement extends LitElement {
                consumeContext(token, callback) { queueMicrotask(() => callback(token === 'auth' ? { getLatestToken: async () => ${JSON.stringify(settings.token)} }
                    : token === 'document' ? window.workspace : { getParent: () => ({ unique: ${JSON.stringify(settings.root)} }) })); }
                observe(source, callback) { window.workspace.observe(callback); callback(window.workspace.getData()); }
            }
            export class UmbChangeEvent extends Event { constructor() { super('change', { bubbles: true }); } }`);
        if (path.startsWith('/App_Plugins/')) return js(await readFile(resolve(pluginRoot, path.slice(1)), 'utf8'));
        if (!administrator && path.endsWith('/management/api/v1/appblocks/administrator'))
            return route.fulfill({ contentType: 'application/json', body: JSON.stringify({ allowed: false }) });
        return route.continue();
    });
    await page.goto(`${settings.address}/__endpoint-editor`);
    await page.waitForFunction(() => window.ready);
    return { page, errors };
}

/** Clicks Test and waits for its new answer: the status and the items it shows. */
async function test_(page) {
    // A click clears the last answer and marks the element busy before its request goes out.
    await page.getByRole('button', { name: 'Test endpoint' }).click();
    await page.waitForFunction(async () => {
        const element = document.querySelector('appblocks-api-endpoint-test');
        return !element.busy && (element.result || element.error) && await element.updateComplete;
    });
    return { status: await page.getByTestId('api-status').textContent(), items: JSON.parse(await page.getByTestId('api-json').textContent()).items };
}

test('an endpoint created and published in the backoffice answers its route, and Test shows its JSON before and after', { skip: skipSite }, async () => {
    await startSite();
    assert.equal((await (await management('api-endpoints/settings', 'GET')).json()).basePath, '/api/data');
    const id = crypto.randomUUID();
    const { page, errors } = await open({ unique: id, values: { ...values } });

    // The route editor shows the full URL and the parameter it binds.
    await page.waitForFunction(() => { const route = document.querySelector('appblocks-api-route'); return route.basePath || route.error; });
    assert.equal(await page.evaluate(() => document.querySelector('appblocks-api-route').error), undefined, errors.join('; '));
    await page.getByLabel('Route').fill('orders/:id/lines');
    assert.equal(await page.getByTestId('api-url').textContent(), `${settings.address}/api/data/orders/:id/lines`);
    await page.getByText('Binds :PARAM.id from the path.').waitFor();

    // Test reads the unsaved values of a document not created yet, as the signed-in administrator, with a sample for each parameter.
    await page.getByRole('button', { name: 'Test endpoint' }).click();
    assert.match(await page.getByRole('alert').first().textContent(), /Enter a sample value for :id/);
    await page.getByLabel(':id').fill('4');
    assert.deepEqual(await test_(page), { status: '200', items: [{ lineId: 2, item: 'Sprocket', quantity: 1 }, { lineId: 1, item: 'Bearing', quantity: 2 }] });
    assert.equal(await page.getByText('GET /api/data/orders/4/lines answered').count(), 1);

    // Create and publish it as the backoffice does: a document of the API endpoint type under the API endpoints root.
    await management('document', 'POST', {
        id, parent: { id: settings.root }, documentType: { id: settings.type }, template: null,
        values: Object.entries({ ...values, route: 'orders/:id/lines' }).map(([alias, value]) => ({ alias, value, culture: null, segment: null })),
        variants: [{ culture: null, segment: null, name: 'Browser lines' }],
    });
    await management(`document/${id}/publish`, 'PUT', { publishSchedules: [{ culture: null }] });

    // Test now answers for the saved document, and a query parameter narrows it.
    await page.getByLabel('?item=').fill('Bearing');
    assert.deepEqual(await test_(page), { status: '200', items: [{ lineId: 1, item: 'Bearing', quantity: 2 }] });

    // The published route answers anyone, as JSON that no cache keeps.
    const answer = await page.evaluate(async () => {
        const response = await fetch('/api/data/orders/5/lines', { credentials: 'omit' });
        return { status: response.status, type: response.headers.get('content-type'), cache: response.headers.get('cache-control'), body: await response.json() };
    });
    assert.deepEqual(answer, { status: 200, type: 'application/json; charset=utf-8', cache: 'no-store', body: { items: [{ lineId: 4, item: 'Oil', quantity: 1 }, { lineId: 3, item: 'Chain', quantity: 4 }] } });
    assert.deepEqual(errors, []);
    await page.context().close();
});

test('an editor who is not an AppBlocks administrator is told why there is no Test button', { skip: skipSite }, async () => {
    await startSite();
    const { page, errors } = await open({ unique: crypto.randomUUID(), values: { ...values, route: 'orders/:id/lines' } }, { administrator: false });
    await page.getByTestId('test-needs-administrator').waitFor();
    assert.match(await page.getByTestId('test-needs-administrator').textContent(), /AppBlocks administrators/);
    assert.equal(await page.getByRole('button', { name: 'Test endpoint' }).count(), 0);
    assert.deepEqual(errors, []);
    await page.context().close();
});
