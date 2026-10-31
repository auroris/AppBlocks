import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { readFile, mkdir } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../', import.meta.url));
const site = process.env.APPBLOCKS_CLEAN_SITE;
const url = process.env.APPBLOCKS_CLEAN_URL;
assert.ok(site && url && ['localhost', '127.0.0.1'].includes(new URL(url).hostname));
for (const file of ['appsettings.json', 'appsettings.Development.json']) {
    const settings = JSON.parse(await readFile(resolve(site, file), 'utf8'));
    assert.ok(!settings.ConnectionStrings?.umbracoDbDSN, 'Wizard site starts without a database connection');
    assert.ok(!settings.Umbraco?.CMS?.Unattended?.InstallUnattended, 'Unattended installation is disabled');
}
const account = JSON.parse(await readFile(resolve(site, 'test-account.json'), 'utf8'));
const output = resolve(site, '../results');
await mkdir(output, {recursive:true});
const require = createRequire(resolve(process.env.INTERACTIVE_REPORTS_ROOT || resolve(root, '../Interactive-Reports'), 'package.json'));
const { chromium } = require('playwright');
const browser = await chromium.launch({headless:true, ...(existsSync(chromium.executablePath()) ? {} : {executablePath:process.env.BROWSER_PATH || 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'})});
const page = await browser.newPage({viewport:{width:1280,height:900}});
page.setDefaultTimeout(30000);
try {
    await page.goto(url + '/umbraco');
    await page.locator('umb-installer-user').waitFor();
    await page.screenshot({path:resolve(output, 'wizard-start.png'), fullPage:true});
    await page.getByRole('textbox', {name:'name', exact:true}).fill(account.UnattendedUserName);
    await page.getByRole('textbox', {name:'email', exact:true}).fill(account.UnattendedUserEmail);
    await page.getByRole('textbox', {name:'password', exact:true}).fill(account.UnattendedUserPassword);
    await page.getByRole('button', {name:'Next', exact:true}).click();
    await page.locator('umb-installer-consent').waitFor();
    await page.getByRole('slider').press('Home');
    await page.getByRole('button', {name:'Next', exact:true}).click();
    await page.locator('umb-installer-database').waitFor();
    await page.getByRole('combobox').selectOption({label:'SQLite'});
    const setup = page.waitForResponse(response => response.url().endsWith('/install/setup') && response.request().method() === 'POST', {timeout:120000});
    await page.getByRole('button', {name:'Install', exact:true}).click();
    const response = await setup;
    assert.ok(response.ok(), 'Wizard installation returned HTTP ' + response.status());
    await page.locator('umb-installer-user').waitFor({state:'detached', timeout:120000});
    console.log('PASS Browser installation wizard completed from no database, without unattended installation');
    await page.screenshot({path:resolve(output, 'wizard-complete.png'), fullPage:true});
} catch (error) {
    await page.screenshot({path:resolve(output, 'wizard-failure.png'), fullPage:true});
    console.log(await page.locator('body').ariaSnapshot());
    throw error;
} finally { await browser.close(); }
