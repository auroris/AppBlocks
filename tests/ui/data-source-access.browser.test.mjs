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
const plugin = resolve(root, 'src/AppBlocks/wwwroot/App_Plugins/DataSourceAuthoring');
const sqlDataSource = '6a79805a-4170-811d-64a8-83ee1422bade';
let browser, page;

before(async () => {
    browser = await chromium.launch({ headless: true, ...(existsSync(chromium.executablePath()) ? {} : { executablePath: process.env.BROWSER_PATH || 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe' }) });
    page = await browser.newPage();
    await page.route('**/*', async route => {
        const path = new URL(route.request().url()).pathname;
        const js = body => route.fulfill({ contentType: 'text/javascript', body });
        if (path === '/editor') return route.fulfill({ contentType: 'text/html', body: `<script type="importmap">{"imports":{
            "@umbraco-cms/backoffice/class-api":"/umb.js", "@umbraco-cms/backoffice/workspace":"/umb.js"
        }}</script><script type="module">
            import { api } from '/App_Plugins/DataSourceAuthoring/access-preset.js';
            // As Umbraco's preset builder calls it: the stored value, the data type's settings, the variation, then the property and document.
            window.preset = (value, args, workspace) => { window.workspace = workspace; return new api({}).processValue(value, [{ alias: 'items', value: ['Public', 'Not public', 'Specific groups'] }], {}, args); };
            window.ready = true;
        </script>` });
        if (path === '/umb.js') return js(`export const UMB_WORKSPACE_CONTEXT = 'UmbWorkspaceContext';
            export class UmbControllerBase { constructor(host) { this.host = host; }
                getContext(token) { return token === UMB_WORKSPACE_CONTEXT && window.workspace ? Promise.resolve(window.workspace) : Promise.reject(new Error('Context could not be found.')); } }`);
        if (path.startsWith('/App_Plugins/DataSourceAuthoring/')) return js(await readFile(resolve(plugin, path.slice('/App_Plugins/DataSourceAuthoring/'.length)), 'utf8'));
        return route.fulfill({ status: 404 });
    });
    await page.goto('http://app.test/editor');
    await page.waitForFunction(() => window.ready);
});
after(async () => browser?.close());

const preset = (value, args, isNew) => page.evaluate(([value, args, isNew]) =>
    window.preset(value ?? undefined, args, isNew === null ? undefined : { getIsNew: () => isNew ?? undefined }), [value, args, isNew]);
const access = { alias: 'access', entityType: 'document', entityTypeUnique: sqlDataSource, propertyEditorSchemaAlias: 'Umbraco.RadioButtonList' };

test('the package registers the preset for radio-button lists', async () => {
    const manifest = JSON.parse(await readFile(resolve(plugin, 'umbraco-package.json'), 'utf8'));
    const extension = manifest.extensions.find(extension => extension.type === 'propertyValuePreset');
    assert.equal(extension.forPropertyEditorSchemaAlias, 'Umbraco.RadioButtonList');
    assert.equal(extension.api, '/App_Plugins/DataSourceAuthoring/access-preset.js');
});

test('a new SQL data source starts with Public', async () => {
    // Umbraco marks the workspace new only after the presets ran, so a scaffold sees it unset; a later new state counts too.
    assert.equal(await preset(undefined, access, undefined), 'Public');
    assert.equal(await preset(undefined, access, true), 'Public');
    assert.equal(await preset(undefined, { ...access, entityTypeUnique: sqlDataSource.toUpperCase() }, undefined), 'Public');
});

test('an existing source, a chosen value and other properties are left alone', async () => {
    assert.equal(await preset(undefined, access, false), undefined, 'opening an existing source with no value must not fill one in');
    assert.equal(await preset('Not public', access, undefined), 'Not public');
    assert.equal(await preset('Specific groups', access, true), 'Specific groups');
    assert.equal(await preset(undefined, access, null), undefined, 'without a workspace it cannot tell a new source from an existing one');
    assert.equal(await preset(undefined, { ...access, alias: 'size' }, undefined), undefined);
    assert.equal(await preset(undefined, { ...access, entityTypeUnique: '00000000-0000-0000-0000-000000000001' }, undefined), undefined);
});
