import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { readFile, readdir, mkdir, writeFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomBytes, createHash } from 'node:crypto';

const root = fileURLToPath(new URL('../../', import.meta.url));
const site = resolve(process.env.APPBLOCKS_CLEAN_SITE || resolve(root, 'artifacts/clean-install/site'));
const url = process.env.APPBLOCKS_CLEAN_URL || 'http://localhost:5187';
assert.ok(['localhost', '127.0.0.1'].includes(new URL(url).hostname), 'Use a disposable local site.');
const require = createRequire(resolve(process.env.INTERACTIVE_REPORTS_ROOT || resolve(root, '../Interactive-Reports'), 'package.json'));
const { request, chromium } = require('playwright');
const settings = JSON.parse(await readFile(resolve(site, 'appsettings.Development.json'), 'utf8'));
const unattended = existsSync(resolve(site, 'test-account.json'))
  ? JSON.parse(await readFile(resolve(site, 'test-account.json'), 'utf8')) : settings.Umbraco.CMS.Unattended;
const assets = JSON.parse(await readFile(resolve(site, 'obj/project.assets.json'), 'utf8'));
const libraries = Object.keys(assets.libraries);
assert.ok(!libraries.some(name => /usync|UmbracoAppHost|StarterKit/i.test(name)), 'No non-AppBlocks CMS plugins.');
assert.ok(Object.values(assets.libraries).every(library => library.type === 'package'), 'Consume NuGet packages, not project references.');
const companions = libraries.some(name => name.startsWith('AppBlocks.InteractiveReports/'));
const documents = libraries.some(name => name.startsWith('AppBlocks.Documents/'));
const output = resolve(site, '../results');
const stage = process.env.APPBLOCKS_CLEAN_STAGE || (companions ? 'companions' : 'core');
await mkdir(output, { recursive: true });
const results = [];
function passed(message) { results.push(message); console.log('PASS ' + message); }
function key(name) {
  const bytes = createHash('sha256').update('UmbracoAppHost.Blocks.v1:standalone-demo:' + name).digest().subarray(0, 16);
  return [Buffer.from(bytes.subarray(0, 4)).reverse().toString('hex'), Buffer.from(bytes.subarray(4, 6)).reverse().toString('hex'), Buffer.from(bytes.subarray(6, 8)).reverse().toString('hex'), bytes.subarray(8, 10).toString('hex'), bytes.subarray(10).toString('hex')].join('-');
}

const anonymous = await request.newContext({ baseURL: url });
const security = '/umbraco/management/api/v1/security/back-office';
const login = await anonymous.post(security + '/login', { data: { username: unattended.UnattendedUserEmail, password: unattended.UnattendedUserPassword } });
assert.ok(login.ok(), `Administrator login: ${login.status()} ${await login.text()}`);
const verifier = randomBytes(32).toString('base64url');
const redirect = url + '/umbraco/oauth_complete';
const authorization = await anonymous.get(security + '/authorize?' + new URLSearchParams({
  client_id: 'umbraco-back-office', response_type: 'code', redirect_uri: redirect,
  code_challenge: createHash('sha256').update(verifier).digest('base64url'), code_challenge_method: 'S256', state: 'clean-install',
}), { maxRedirects: 0 });
assert.equal(authorization.status(), 302, await authorization.text());
const code = new URL(authorization.headers().location).searchParams.get('code');
const exchange = await anonymous.post(security + '/token', { form: { grant_type: 'authorization_code', client_id: 'umbraco-back-office', redirect_uri: redirect, code, code_verifier: verifier } });
assert.ok(exchange.ok(), 'Obtain management API token');
const token = (await exchange.json()).access_token;
const admin = await request.newContext({ baseURL: url, storageState: await anonymous.storageState(), extraHTTPHeaders: { Authorization: 'Bearer ' + token } });
await anonymous.dispose();
const publicClient = await request.newContext({ baseURL: url });
passed('Fresh Umbraco administrator can sign in');

const packages = ['AppBlocks', ...(companions ? ['AppBlocks.InteractiveReports'] : []), ...(documents ? ['AppBlocks.Documents'] : [])];
for (const name of packages) {
  let count = 0;
  for (const [folder, endpoint] of [['ContentTypes', 'document-type'], ['DataTypes', 'data-type']]) {
    for (const file of await readdir(resolve(root, 'src', name, 'Schema', folder)).catch(() => [])) {
      if (!file.endsWith('.config')) continue;
      const xml = await readFile(resolve(root, 'src', name, 'Schema', folder, file), 'utf8');
      const match = xml.match(/<(?:ContentType|DataType)\b[^>]*\bKey=["']([^"']+)["']/);
      assert.ok(match, 'Schema key: ' + file);
      const key = match[1];
      const response = await admin.get(`/umbraco/management/api/v1/${endpoint}/${key}`);
      assert.ok(response.ok(), `${name}/${file}: ${response.status()} ${await response.text()}`);
      const definition = await response.json();
      assert.equal(definition.id, key);
      const folderPath = xml.match(/<Folder>([^<]+)<\/Folder>/)?.[1];
      assert.ok(folderPath === 'AppBlocks' || folderPath?.startsWith('AppBlocks/'), `${name}/${file}: AppBlocks folder`);
      const ancestors = await admin.get(`/umbraco/management/api/v1/tree/${endpoint}/ancestors?descendantId=${key}`);
      assert.ok(ancestors.ok(), `${name}/${file}: folder tree ${ancestors.status()}`);
      const folderNames = (await ancestors.json()).filter(item => item.id !== key).map(item => item.name);
      assert.deepEqual(folderNames.sort(), folderPath.split('/').map(name => decodeURIComponent(name.replaceAll('+', ' '))).sort(), `${name}/${file}: backoffice folders`);
      count++;
    }
  }
  passed(`${name}: all ${count} embedded schema definitions installed automatically in AppBlocks backoffice folders`);
}
const connections = await admin.get('/umbraco/management/api/v1/appblocks/connections');
assert.ok(connections.ok(), await connections.text());
assert.ok((await connections.json()).some(item => item.name === 'AppBlocksDb' && item.isDefault && item.provider === 'sqlite'));
passed('Default separate AppBlocks SQLite connection is available');
const tables = await admin.get('/umbraco/management/api/v1/appblocks/tables?connection=AppBlocksDb');
assert.ok(tables.ok(), await tables.text());
const connectionTest = await admin.post('/umbraco/management/api/v1/appblocks/connections/test', { data: { name: 'AppBlocksDb', provider: 'sqlite' } });
assert.ok(connectionTest.ok(), await connectionTest.text());
passed('The default application database opens, lists tables and passes connection testing');
for (const path of ['/umbraco/management/api/v1/appblocks/connections', '/umbraco/management/api/v1/appblocks/forms']) {
  assert.ok([401, 403].includes((await publicClient.get(path)).status()), 'Anonymous management request denied: ' + path);
}
passed('Management endpoints reject anonymous access');
for (const name of packages) {
  const base = resolve(root, 'src', name, 'wwwroot');
  for (const file of await readdir(base, { recursive: true })) {
    if (!/\.(js|css|json)$/.test(file)) continue;
    const response = await publicClient.get('/' + file.replaceAll('\\', '/'));
    assert.ok(response.ok(), `Packaged asset ${file}: ${response.status()}`);
    assert.equal(await response.text(), await readFile(resolve(base, file), 'utf8'), `Packaged asset matches source: ${file}`);
  }
}
passed('Every packaged JavaScript, CSS and extension manifest is served and matches source');

const contentFiles = Object.entries(Object.values(assets.targets)[0])
  .filter(([name]) => name.startsWith('AppBlocks')).flatMap(([, library]) => Object.keys(library.contentFiles ?? {}));
assert.deepEqual(contentFiles, ['contentFiles/any/net10.0/oracle/Oracle.ManagedDataAccess.dll'], 'The only file the packages add to a site project');
assert.ok(existsSync(resolve(site, 'bin/Debug/net10.0/oracle/Oracle.ManagedDataAccess.dll')), 'The bundled Oracle driver reaches the site');
assert.ok(!existsSync(resolve(site, 'bin/Debug/net10.0/Oracle.ManagedDataAccess.dll')), 'The Oracle driver stays out of the folder Umbraco scans');
passed('The packages add only the Oracle driver to a site, in its own folder');
if (settings.AppBlocksDemo?.SeedContent) {
  // The probe runs from the started notification, which can finish after the site first answers.
  for (let attempt = 0; attempt < 30 && !existsSync(resolve(site, 'oracle-probe.txt')); attempt++) await new Promise(done => setTimeout(done, 1000));
  const probe = await readFile(resolve(site, 'oracle-probe.txt'), 'utf8');
  assert.match(probe, /^Oracle\.ManagedDataAccess\.Client\.OracleException/, 'The driver loads and reports the closed port: ' + probe);
  passed('The packaged Oracle driver loads and attempts a connection');
}

if (settings.AppBlocksDemo?.SeedContent) {
  const path = '/a-normal-content-page/';
  const publicPage = await publicClient.get(path);
  assert.ok(publicPage.ok(), await publicPage.text());
  assert.match(await publicPage.text(), /Sign in to use this form/);
  const privatePage = await admin.get(path);
  assert.ok(privatePage.ok(), await privatePage.text());
  assert.match(await privatePage.text(), /data-app-form/);
  for (const prefix of ['main', 'side']) {
    const endpoint = `/api/forms/${key('page')}/${key(prefix + '-form')}`;
    assert.equal((await publicClient.get(endpoint + `?${prefix}Id=1`)).status(), 403);
    const loaded = await admin.get(endpoint + `?${prefix}Id=1`);
    assert.ok(loaded.ok(), await loaded.text());
    const original = await loaded.json();
    const headers = { RequestVerificationToken: original.antiforgeryToken };
    const body = { definitionVersion: original.definitionVersion, version: original.record.version, values: { title: original.record.values.title } };
    assert.equal((await admin.put(endpoint + '/1', { data: body })).status(), 400, 'CSRF protection');
    assert.equal((await admin.put(endpoint + '/1', { headers, data: { ...body, definitionVersion: 'old' } })).status(), 409, 'Stale definition protection');
    assert.equal((await admin.put(endpoint + '/1', { headers, data: { ...body, values: { title: null } } })).status(), 422, 'Required field validation');
    const changed = await admin.put(endpoint + '/1', { headers, data: { ...body, values: { title: 'Clean install update' } } });
    assert.ok(changed.ok(), await changed.text());
    const updated = (await changed.json()).record;
    assert.equal(updated.values.title, 'Clean install update');
    assert.equal((await admin.put(endpoint + '/1', { headers, data: body })).status(), 409, 'Optimistic concurrency protection');
    const restored = await admin.put(endpoint + '/1', { headers, data: { ...body, version: updated.version } });
    assert.ok(restored.ok(), await restored.text());
    passed(`Native ${prefix === 'main' ? 'Block List' : 'Block Grid'} form reads and updates; validation, CSRF and concurrency checks work`);
    if (companions) {
      const report = `/api/reports/page-${key('page').replaceAll('-', '')}-${key(prefix + '-report').replaceAll('-', '')}`;
      assert.equal((await publicClient.get(report + '/default')).status(), 401);
      const response = await admin.get(report + '/default');
      assert.ok(response.ok(), await response.text());
      assert.match(await response.text(), /A task from a normal content page/);
      const schemaResponse = await admin.get(report + '/schema');
      assert.ok(schemaResponse.ok(), await schemaResponse.text());
      const state = (await schemaResponse.json()).defaultState;
      const query = await admin.post(report + '/query', { data: state });
      assert.ok(query.ok(), await query.text());
      const saved = await admin.post(report + '/saved', { data: { title: 'Clean installation view ' + randomBytes(4).toString('hex'), state } });
      assert.ok(saved.ok(), await saved.text());
      const savedId = (await saved.json()).id;
      const reopened = await admin.get(report + '/' + savedId);
      assert.ok(reopened.ok(), await reopened.text());
      assert.match(await reopened.text(), /Clean installation view/);
      const deleted = await admin.delete('/api/reports/' + savedId);
      assert.ok(deleted.ok(), await deleted.text());
      passed(`${prefix} report executes its block-owned SQL source and denies anonymous access`);
      passed(`${prefix} report queries, saves, reopens and deletes a view in the default application database`);
    }
  }
  const inventory = await admin.get('/umbraco/management/api/v1/appblocks/forms');
  assert.equal((await inventory.json()).forms.filter(form => form.pageKey === key('page')).length, 2);
  const sample = await admin.get(`/umbraco/management/api/v1/appblocks/forms/${key('page')}/${key('main-form')}/sample`);
  assert.ok(sample.ok()); assert.match(await sample.text(), /JsonPropertyName\("title"\)/);
  passed('Forms inventory discovers both native forms and generates their C# samples');
  const attachments = `/api/attachments/${key('page')}/${key('files')}`;
  const listed = await admin.get(attachments + '?mainId=1');
  assert.ok(listed.ok(), await listed.text());
  const attachmentState = await listed.json();
  assert.ok(attachmentState.canWrite);
  const attachmentHeaders = { RequestVerificationToken: attachmentState.antiforgeryToken };
  const multipart = { file: { name: 'clean-install.txt', mimeType: 'text/plain', buffer: Buffer.from('Clean installation upload') } };
  assert.equal((await publicClient.get(attachments + '?mainId=1')).status(), 403);
  assert.equal((await admin.post(attachments + '?mainId=1', { multipart })).status(), 400);
  const uploaded = await admin.post(attachments + '?mainId=1', { headers: attachmentHeaders, multipart });
  assert.ok(uploaded.ok(), await uploaded.text());
  const file = await uploaded.json();
  const download = await admin.get(`${attachments}/${file.id}?mainId=1`);
  assert.ok(download.ok(), await download.text());
  assert.equal(await download.text(), 'Clean installation upload');
  assert.match(download.headers()['content-disposition'], /attachment/);
  const removed = await admin.delete(`${attachments}/${file.id}?mainId=1`, { headers: attachmentHeaders });
  assert.ok(removed.ok(), await removed.text());
  assert.equal((await admin.get(`${attachments}/${file.id}?mainId=1`)).status(), 404);
  passed('Attachments upload, download and removal work in the default database with authorization and CSRF protection');
  if (documents) {
    const endpoint = '/api/paper-reports/' + key('document');
    assert.equal((await publicClient.get(endpoint)).status(), 403);
    const document = await admin.get(endpoint);
    assert.ok(document.ok(), await document.text());
    assert.match(document.headers()['content-type'], /text\/html/);
    assert.match(await document.text(), /Clean installation document/);
    assert.match(await document.text(), /A task from a normal content page/);
    assert.match(document.headers()['cache-control'], /no-store/);
    passed('Documents render self-contained authorized HTML without an external PDF service');
  }
}

const browser = await chromium.launch({ headless: true, ...(existsSync(chromium.executablePath()) ? {} : { executablePath: process.env.BROWSER_PATH || 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe' }) });
const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
page.setDefaultTimeout(15000);
page.setDefaultNavigationTimeout(20000);
try {
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('response', async response => {
    // The reports library deliberately probes this opt-in diagnostic endpoint, which is disabled by default.
    if (response.status() >= 400 && new URL(response.url()).pathname.startsWith('/api/') && !new URL(response.url()).pathname.endsWith('/whoami'))
      console.log(`HTTP ${response.status()} ${response.request().method()} ${new URL(response.url()).pathname}: ${(await response.text()).slice(0, 1000)}`);
  });
  await page.goto(url + '/umbraco');
  await page.getByRole('textbox').nth(0).fill(unattended.UnattendedUserEmail);
  await page.getByRole('textbox').nth(1).fill(unattended.UnattendedUserPassword);
  await page.getByRole('button', { name: 'Login', exact: true }).click();
  await page.locator('umb-backoffice').waitFor();
  await page.goto(url + '/umbraco/section/settings/workspace/appblocks');
  await page.locator('appblocks-dashboard').waitFor();
  await page.getByRole('tab', { name: 'Connections', exact: true }).click();
  await page.getByRole('row').filter({ hasText: 'AppBlocks database' }).waitFor();
  await page.screenshot({ path: resolve(output, stage + '-dashboard.png'), fullPage: true });
  if (settings.AppBlocksDemo?.SeedContent) {
    const response = await page.goto(url + '/a-normal-content-page/?mainId=1&sideId=1');
    assert.match((await response.allHeaders())['set-cookie'] || '', /\.AspNetCore\.Antiforgery\./,
      'The first page response establishes the antiforgery cookie before parallel form loads');
    const field = page.locator('[data-app-form]').first().locator('[data-field="title"]');
    await field.waitFor();
    await page.waitForFunction(() => document.querySelector('[data-app-form] [data-field="title"]')?.value === 'A task from a normal content page');
    await field.fill('Browser edit from clean installation');
    await page.locator('[data-app-form]').first().locator('[data-save]').click();
    await page.locator('[data-app-form]').first().getByText('Saved.', { exact: true }).waitFor();
    await page.reload();
    await page.waitForFunction(() => document.querySelector('[data-app-form] [data-field="title"]')?.value === 'Browser edit from clean installation');
    await field.fill('A task from a normal content page');
    await page.locator('[data-app-form]').first().locator('[data-save]').click();
    await page.locator('[data-app-form]').first().getByText('Saved.', { exact: true }).waitFor();
    await page.reload();
    if (companions) await page.locator('interactive-report').first().getByRole('cell', { name: 'A task from a normal content page', exact: true }).waitFor();
    await page.screenshot({ path: resolve(output, 'native-blocks.png'), fullPage: true });
    await page.getByRole('link', { name: 'Open AppBlocks' }).click();
    await page.locator('appblocks-dashboard').waitFor();
    passed('Browser form edits persist across reload; both native reports render');
  }
  assert.deepEqual(errors, [], 'Backoffice JavaScript errors');
  passed('Real backoffice sign-in and AppBlocks Connections dashboard render without JavaScript errors');
} catch (error) {
  await page.screenshot({ path: resolve(output, 'failure.png'), fullPage: true });
  console.log(await page.locator('body').ariaSnapshot());
  throw error;
} finally { await browser.close(); }

await writeFile(resolve(output, stage + '.json'), JSON.stringify({ url, packages, results }, null, 2));
await admin.dispose();
await publicClient.dispose();
