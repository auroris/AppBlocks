import assert from 'node:assert/strict';
import { test, before, after } from 'node:test';
import { createRequire } from 'node:module';
import { existsSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { runtimeAsset } from './runtime-assets.mjs';

// data-api.js on a page of a site served under the path base /site, against answers routed here as the endpoints give them.
const root = fileURLToPath(new URL('../../', import.meta.url));
const require = createRequire(resolve(process.env.INTERACTIVE_REPORTS_ROOT || resolve(root, '../Interactive-Reports'), 'package.json'));
const { chromium } = require('playwright');
let browser, context, page;
const requests = [];
before(async () => {
    browser = await chromium.launch({ headless: true, ...(existsSync(chromium.executablePath()) ? {} : { executablePath: process.env.BROWSER_PATH || 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe' }) });
    context = await browser.newContext();
    await context.addCookies([{ name: 'session', value: 'member-7', url: 'http://localhost/' }]);
    page = await context.newPage();
    await page.route('**/*', async route => {
        const request = route.request(), url = new URL(request.url());
        const asset = await runtimeAsset(url.pathname);
        if (asset) return route.fulfill(asset);
        if (url.pathname === '/site/orders') return route.fulfill({ contentType: 'text/html', body: '<!doctype html><title>Orders</title><script type="module">import * as api from "/site/App_Plugins/ApplicationBlocks/data-api.js"; window.api = api;</script>' });
        if (!url.pathname.startsWith('/site/api/data/')) return route.fulfill({ status: 404, body: '' });
        const headers = await request.allHeaders();
        requests.push({ method: request.method(), path: url.pathname, search: url.search, cookie: headers.cookie ?? null, accept: headers.accept ?? null,
            contentType: headers['content-type'] ?? null, token: headers.requestverificationtoken ?? null, body: request.postData() });
        switch (url.pathname.slice('/site/api/data/'.length)) {
            case 'orders': return route.fulfill({ contentType: 'application/json; charset=utf-8', body: JSON.stringify({ items: [{ orderId: 4, status: 'open' }] }) });
            case 'echo': return route.fulfill({ json: { received: request.postData() } });
            case 'missing': return route.fulfill({ status: 404, contentType: 'application/problem+json', body: JSON.stringify({ status: 404, title: 'Not Found' }) });
            case 'refused': return route.fulfill({ status: 403, body: '' });
            case 'invalid': return route.fulfill({ status: 422, json: { errors: { '': [], name: ['Enter a name.'] } } });
            case 'explained': return route.fulfill({ status: 409, json: { title: 'Conflict', detail: 'The order was changed elsewhere.' } });
            case 'nothing': return route.fulfill({ status: 204, body: '' });
            case 'html': return route.fulfill({ contentType: 'text/html', body: '<p>Sign in</p>' });
            case 'down': return route.abort('connectionrefused');
            case 'slow': await new Promise(done => setTimeout(done, 2000)); return route.fulfill({ json: {} }).catch(() => {});
        }
        return route.fulfill({ status: 404, body: '' });
    });
    await page.goto('http://localhost/site/orders');
    await page.waitForFunction(() => window.api);
});
after(async () => browser?.close());

test('the path base comes from where the module is served, and addresses resolve against it', async () => {
    const result = await page.evaluate(() => ({
        pathBase: api.pathBase,
        rooted: api.siteUrl('/api/data/orders').href,
        absolute: api.siteUrl('https://example.com/api/data/x').href,
        protocolRelative: api.siteUrl('//example.com/x').href,
        object: api.siteUrl(new URL('http://localhost/site/api/forms/p/b')).href,
        query: api.withQuery('/api/data/orders', { status: 'open', tag: ['a', 'b'], skip: null, none: undefined, page: 2 }).href,
        replaced: api.withQuery('/api/data/orders?status=closed&keep=1', { status: 'open' }).href,
    }));
    assert.deepEqual(result, {
        pathBase: '/site',
        rooted: 'http://localhost/site/api/data/orders',
        absolute: 'https://example.com/api/data/x',
        protocolRelative: 'http://example.com/x',
        object: 'http://localhost/site/api/forms/p/b',
        query: 'http://localhost/site/api/data/orders?status=open&tag=a&tag=b&page=2',
        replaced: 'http://localhost/site/api/data/orders?keep=1&status=open',
    });
});

test('a GET sends the visitor\'s cookies and resolves with the rows', async () => {
    requests.length = 0;
    const rows = await page.evaluate(() => api.request(api.withQuery('/api/data/orders', { status: 'open' })));
    assert.deepEqual(rows, { items: [{ orderId: 4, status: 'open' }] });
    assert.deepEqual(requests, [{ method: 'GET', path: '/site/api/data/orders', search: '?status=open', cookie: 'session=member-7', accept: 'application/json', contentType: null, token: null, body: null }]);
});

test('a body goes as JSON unless it is form data or text, and headers are added', async () => {
    requests.length = 0;
    const answers = await page.evaluate(async () => {
        const form = new FormData(); form.append('note', 'hello');
        return [
            await api.request('/api/data/echo', { method: 'POST', body: { name: 'Ada', count: 2 }, headers: { RequestVerificationToken: 'token-1' } }),
            await api.request('/api/data/echo', { method: 'PUT', body: form }),
            await api.request('/api/data/echo', { method: 'POST', body: 'plain' }),
            await api.request('/api/data/nothing', { method: 'DELETE' }),
        ];
    });
    assert.deepEqual(answers.map(answer => answer?.received ?? answer), ['{"name":"Ada","count":2}', answers[1].received, 'plain', null]);
    assert.match(answers[1].received, /name="note"\r\n\r\nhello/);
    assert.deepEqual(requests.map(entry => [entry.method, entry.contentType?.split(';')[0] ?? null, entry.token]), [
        ['POST', 'application/json', 'token-1'], ['PUT', 'multipart/form-data', null], ['POST', 'text/plain', null], ['DELETE', null, null]]);
});

test('a refusal rejects with its status and problem, and errorText says what the endpoint said', async () => {
    const results = await page.evaluate(async () => {
        const outcome = async path => {
            try { await api.request(path); return { resolved: true }; }
            catch (error) { return { type: error instanceof api.ApiError ? 'ApiError' : error.name, status: error.status, problem: error.problem, message: error.message, text: api.errorText(error), fallback: api.errorText(error, 'The orders could not be loaded.') }; }
        };
        return {
            missing: await outcome('/api/data/missing'), refused: await outcome('/api/data/refused'), invalid: await outcome('/api/data/invalid'),
            explained: await outcome('/api/data/explained'), down: await outcome('/api/data/down'), html: await outcome('/api/data/html'),
        };
    });
    assert.deepEqual(results.missing, { type: 'ApiError', status: 404, problem: { status: 404, title: 'Not Found' }, message: 'Not Found', text: 'Not Found', fallback: 'Not Found' });
    assert.deepEqual(results.refused, { type: 'ApiError', status: 403, problem: {}, message: 'The request failed with status 403.',
        text: 'The request could not be completed.', fallback: 'The orders could not be loaded.' });
    assert.equal(results.invalid.text, 'Enter a name.');
    assert.equal(results.invalid.status, 422);
    assert.deepEqual([results.explained.message, results.explained.text], ['The order was changed elsewhere.', 'The order was changed elsewhere.']);
    // No answer at all, or an answer that is not JSON, is not an ApiError; the reader gets the fallback.
    assert.equal(results.down.type, 'TypeError');
    assert.deepEqual([results.down.text, results.down.fallback], ['The request could not be completed.', 'The orders could not be loaded.']);
    assert.equal(results.html.type, 'SyntaxError');
    assert.equal(results.html.fallback, 'The orders could not be loaded.');
});

test('a signal aborts the request', async () => {
    const name = await page.evaluate(async () => {
        const controller = new AbortController();
        const pending = api.request('/api/data/slow', { signal: controller.signal });
        controller.abort();
        try { await pending; return 'resolved'; } catch (error) { return error.name; }
    });
    assert.equal(name, 'AbortError');
});
