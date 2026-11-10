import assert from 'node:assert/strict';
import { test, before, after } from 'node:test';
import { createRequire } from 'node:module';
import { existsSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { runtimeAsset } from './runtime-assets.mjs';

// A schedule in the browser. One page lays out its own table with schedule.js's renderers, under the path base /site,
// inside a panel that scrolls, and mounts it; the other carries the markup Schedule.cshtml renders and lets runtime.js
// mount it. ScheduleBlockTests verifies the real Razor output.
const root = fileURLToPath(new URL('../../', import.meta.url));
const require = createRequire(resolve(process.env.INTERACTIVE_REPORTS_ROOT || resolve(root, '../Interactive-Reports'), 'package.json'));
const { chromium } = require('playwright');
let browser;
before(async () => { browser = await chromium.launch({ headless: true, ...(existsSync(chromium.executablePath()) ? {} : { executablePath: process.env.BROWSER_PATH || 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe' }) }); });
after(async () => browser?.close());

const handBuilt = `<!doctype html><html lang="en"><head><meta name="viewport" content="width=device-width"><title>Rooms</title>
    <link rel="stylesheet" href="/site/App_Plugins/ApplicationBlocks/runtime.css">
    <style>body { margin: 0; padding: 0 0 2000px; font-family: system-ui; } #panel { overflow: auto; height: 420px; } #rooms { width: 1600px; }</style></head>
    <body><div id="panel" class="application-blocks"><section id="rooms" class="app-schedule"></section></div>
    <script type="module">
        import * as schedule from '/site/App_Plugins/ApplicationBlocks/schedule.js';
        // Which scroll listeners on the document are still live, so a test can tell whether mounts pile them up.
        const added = [], add = document.addEventListener;
        document.addEventListener = function (type, listener, options) { added.push({ type, signal: options?.signal }); return add.call(this, type, listener, options); };
        window.documentScrollListeners = () => added.filter(entry => entry.type === 'scroll' && !entry.signal?.aborted).length;
        // A table laid out as the server lays one out: a corner, one th per day, one row per room, bars in the cell they start in.
        window.draw = (target, bars, { columns = 40, rows = 14, today = 20 } = {}) => {
            const scroll = document.createElement('div'); scroll.className = 'app-schedule-scroll';
            const table = document.createElement('table'); table.className = 'app-schedule-table app-schedule-table--days';
            const head = table.createTHead().insertRow();
            const corner = document.createElement('th'); corner.scope = 'col'; corner.className = 'app-schedule-corner';
            corner.innerHTML = '<span class="app-schedule-label"></span>'; head.append(corner);
            for (let column = 0; column < columns; column++) {
                const cell = document.createElement('th'); cell.scope = 'col';
                cell.className = 'app-schedule-col' + (column === today ? ' app-schedule-col--today' : '');
                cell.innerHTML = '<span class="app-schedule-day">' + (column + 1) + '</span>'; head.append(cell);
            }
            const body = table.createTBody(); body.className = 'app-schedule-group';
            for (let index = 0; index < rows; index++) {
                const row = body.insertRow(); row.className = 'app-schedule-row'; row.style.setProperty('--ah-lanes', '1'); row.dataset.rowKey = 'room-' + (index + 1);
                row.append(schedule.scheduleRowLabel({ label: 'Room ' + (index + 1), description: index === 0 ? 'Ground floor, by the entrance.' : null }));
                for (let column = 0; column < columns; column++) row.insertCell().className = 'app-schedule-cell' + (column === today ? ' app-schedule-col--today' : '');
            }
            for (const bar of bars) body.rows[bar.row].cells[bar.column + 1].append(schedule.scheduleBar(bar));
            scroll.append(table); target.replaceChildren(scroll);
        };
        window.schedule = schedule;
        window.rooms = document.getElementById('rooms');
        window.ready = true;
    </script></body></html>`;

const bars = [
    { row: 1, column: 12, key: 'b1', kind: 'Meeting', kindIndex: 2, span: 3, label: 'Quarterly planning with the whole regional team',
        details: ': Room 2, 13 Nov 2026 to 15 Nov 2026, Meeting' },
    { row: 2, column: 2, key: 'b2', kind: 'Course', kindIndex: 1, span: 2, color: '#B35C00', textColor: 'white', label: 'First aid',
        details: ': Room 3, 3 Nov 2026 to 4 Nov 2026, Course', when: 'Room 3, 3 Nov 2026 to 4 Nov 2026, Course',
        description: 'Bring a pen.\nLunch is provided.', link: '/site/courses/first-aid' },
    { row: 3, column: 5, key: 'b3', span: 1, label: 'Inspection', details: ': Room 4, 6 Nov 2026', link: '/site/inspections/3', clippedEnd: true },
];

async function open(viewport = { width: 720, height: 600 }) {
    const page = await browser.newPage({ viewport });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.route('**/*', async route => {
        const url = new URL(route.request().url());
        const asset = await runtimeAsset(url.pathname);
        if (asset) return route.fulfill(asset);
        if (url.pathname === '/site/rooms') return route.fulfill({ contentType: 'text/html', body: handBuilt });
        return route.fulfill({ status: 404, body: '' });
    });
    await page.goto('http://localhost/site/rooms');
    await page.waitForFunction(() => window.ready);
    return { page, errors };
}

const tooltip = page => page.locator('#rooms .app-schedule-tooltip');
// Focus holds the tooltip open while something scrolls, where a pointer would land on another element.
async function reveal(page, key) {
    await page.evaluate(key => rooms.querySelector('[data-key="' + key + '"]').focus({ preventScroll: true }), key);
    await tooltip(page).waitFor({ state: "visible" });
}
// The runtime places an open popover once the browser reports it open.
const placedPopover = page => page.waitForFunction(() => document.querySelector(".app-schedule-popover:popover-open")?.style.top);
async function hover(page, name) {
    await page.mouse.move(0, 0);
    await page.locator('.app-schedule-bar', { hasText: name }).hover();
    await tooltip(page).waitFor({ state: 'visible' });
}

test('the renderers build the markup the server renders, and importing the module mounts nothing', async () => {
    const { page, errors } = await open();
    const markup = await page.evaluate(bars => {
        draw(rooms, bars);
        const describe = element => element && ({ tag: element.tagName.toLowerCase(), className: element.className, attributes: Object.fromEntries([...element.attributes].map(a => [a.name, a.value])), text: element.textContent });
        const [planning, firstAid, inspection] = ['b1', 'b2', 'b3'].map(key => rooms.querySelector('[data-key="' + key + '"]'));
        const popover = firstAid.nextElementSibling;
        const label = rooms.querySelector('[data-row-key="room-1"] th');
        return {
            tooltips: rooms.querySelectorAll('.app-schedule-tooltip').length,
            planning: { ...describe(planning), span: planning.style.getPropertyValue('--ah-span'), lane: planning.style.getPropertyValue('--ah-lane'), parts: [...planning.children].map(describe) },
            firstAid: { ...describe(firstAid), bar: firstAid.style.getPropertyValue('--ah-bar'), text: firstAid.style.getPropertyValue('--ah-bar-text') },
            popover: { ...describe(popover), children: [...popover.children].map(describe) },
            inspection: describe(inspection),
            label: { ...describe(label), children: [...label.children].map(describe) },
            unsafe: (() => { const fragment = schedule.scheduleBar({ label: 'X', kindIndex: 11, color: 'red;background:url(//example.com/x)', textColor: 'url(x)' }); const bar = fragment.firstElementChild;
                return { className: bar.className, style: bar.getAttribute('style'), tag: bar.tagName.toLowerCase(), tabIndex: bar.tabIndex, count: fragment.childNodes.length }; })(),
        };
    }, bars);
    assert.equal(markup.tooltips, 0, 'nothing is mounted until mountSchedule is called');

    assert.equal(markup.planning.tag, 'span');
    assert.equal(markup.planning.className, 'app-schedule-bar app-schedule-kind-2');
    assert.equal(markup.planning.attributes.tabindex, '0');
    assert.equal(markup.planning.attributes['data-key'], 'b1');
    assert.equal(markup.planning.attributes['data-kind'], 'Meeting');
    assert.deepEqual([markup.planning.span, markup.planning.lane], ['3', '0']);
    assert.deepEqual(markup.planning.parts.map(part => [part.tag, part.className, part.text]), [
        ['span', 'app-schedule-bar__label', 'Quarterly planning with the whole regional team'],
        ['span', 'app-schedule-bar__details', ': Room 2, 13 Nov 2026 to 15 Nov 2026, Meeting']]);

    // A bar with a colour of its own carries no kind class; one with a description is a button opening its popover.
    assert.equal(markup.firstAid.tag, 'button');
    assert.equal(markup.firstAid.className, 'app-schedule-bar');
    assert.equal(markup.firstAid.attributes.type, 'button');
    assert.deepEqual([markup.firstAid.bar, markup.firstAid.text], ['#b35c00', 'white']);
    assert.equal(markup.firstAid.attributes.href, undefined);
    assert.equal(markup.firstAid.attributes.popovertarget, markup.popover.attributes.id);
    assert.equal(markup.popover.className, 'app-schedule-popover');
    assert.equal(markup.popover.attributes.popover, 'auto');
    assert.equal(markup.popover.attributes['aria-labelledby'], `${markup.popover.attributes.id}-title`);
    assert.deepEqual(markup.popover.children.map(child => [child.tag, child.className]), [
        ['p', 'app-schedule-popover__title'], ['p', 'app-schedule-popover__when'], ['p', 'app-schedule-popover__text'], ['p', 'app-schedule-popover__link'], ['button', 'app-schedule-popover__close']]);
    assert.equal(markup.popover.children[0].attributes.id, `${markup.popover.attributes.id}-title`);
    assert.equal(markup.popover.children[2].text, 'Bring a pen.\nLunch is provided.');
    assert.equal(markup.popover.children[3].text, 'Open link');
    assert.deepEqual(markup.popover.children[4].attributes, { class: 'app-schedule-popover__close', type: 'button', 'aria-label': 'Close',
        popovertarget: markup.popover.attributes.id, popovertargetaction: 'hide' });

    assert.equal(markup.inspection.tag, 'a');
    assert.equal(markup.inspection.className, 'app-schedule-bar app-schedule-bar--clipped-end');
    assert.equal(markup.inspection.attributes.href, '/site/inspections/3');
    assert.equal(markup.inspection.attributes['data-kind'], undefined);

    assert.equal(markup.label.tag, 'th');
    assert.equal(markup.label.attributes.scope, 'row');
    assert.deepEqual(markup.label.children.map(child => [child.tag, child.className]), [['button', 'app-schedule-label app-schedule-label--button'], ['div', 'app-schedule-popover']]);

    // Only a hex value or a name reaches CSS, so the kind's colour stays, cycled into the eight kinds.
    assert.deepEqual(markup.unsafe, { className: 'app-schedule-bar app-schedule-kind-3', style: '--ah-span: 1; --ah-lane: 0;', tag: 'span', tabIndex: 0, count: 1 });
    assert.deepEqual(errors, []);
    await page.close();
});

test('the tooltip shows a bar\'s details inside the viewport and leaves when anything around it scrolls', async () => {
    const { page, errors } = await open();
    await page.evaluate(bars => { draw(rooms, bars, { rows: 24 }); schedule.mountSchedule(rooms); }, bars);

    // The schedule is wider than the viewport, so the bar near its right edge would push the tooltip off screen.
    await hover(page, 'Quarterly planning');
    assert.equal(await tooltip(page).textContent(), 'Quarterly planning with the whole regional team: Room 2, 13 Nov 2026 to 15 Nov 2026, Meeting');
    assert.equal(await tooltip(page).getAttribute('aria-hidden'), 'true');
    const placed = await page.evaluate(() => {
        const tip = rooms.querySelector('.app-schedule-tooltip').getBoundingClientRect(), bar = rooms.querySelector('[data-key="b1"]').getBoundingClientRect();
        return { left: tip.left, right: tip.right, top: tip.top, barBottom: bar.bottom, viewport: document.documentElement.clientWidth, panel: rooms.offsetWidth };
    });
    assert.ok(placed.panel > placed.viewport);
    assert.ok(placed.left >= 8 && placed.right <= placed.viewport - 8 + 0.5, JSON.stringify(placed));
    assert.ok(Math.abs(placed.top - (placed.barBottom + 4)) < 1, JSON.stringify(placed));

    // A panel around the schedule, the schedule's own scroller and the page all count.
    await page.mouse.move(0, 0);
    await reveal(page, 'b1');
    assert.equal(await page.evaluate(() => document.getElementById('panel').scrollLeft = 40), 40);
    await tooltip(page).waitFor({ state: 'hidden' });
    await reveal(page, 'b3');
    assert.equal(await page.evaluate(() => { const scroller = rooms.querySelector('.app-schedule-scroll'); scroller.scrollTop = 30; return scroller.scrollTop; }), 30);
    await tooltip(page).waitFor({ state: 'hidden' });
    await reveal(page, 'b1');
    await page.evaluate(() => window.scrollBy(0, 50));
    await tooltip(page).waitFor({ state: 'hidden' });

    // The keyboard: focus shows it, Escape hides it, and leaving the bar hides it too.
    await page.mouse.move(0, 0);
    await page.locator('[data-key="b3"]').focus();
    await tooltip(page).waitFor({ state: 'visible' });
    assert.equal(await tooltip(page).textContent(), 'Inspection: Room 4, 6 Nov 2026');
    await page.keyboard.press('Escape');
    await tooltip(page).waitFor({ state: 'hidden' });
    // The page was scrolled above, so this bar is partly out of view and the browser scrolls to it as it takes focus. That
    // scroll is reported a frame later, when the tooltip is already under the bar, and it stays there.
    assert.ok(await page.evaluate(() => rooms.querySelector('[data-key="b1"]').getBoundingClientRect().top < 0));
    await page.locator('[data-key="b1"]').focus();
    await tooltip(page).waitFor({ state: 'visible' });
    await page.evaluate(() => new Promise(done => requestAnimationFrame(() => requestAnimationFrame(done))));
    const focused = await page.evaluate(() => {
        const tip = rooms.querySelector('.app-schedule-tooltip'), bar = rooms.querySelector('[data-key="b1"]').getBoundingClientRect();
        return { hidden: tip.hidden, top: tip.getBoundingClientRect().top, barTop: bar.top, barBottom: bar.bottom };
    });
    assert.ok(!focused.hidden && focused.barTop > -1 && Math.abs(focused.top - (focused.barBottom + 4)) < 1, JSON.stringify(focused));
    await page.locator('[data-key="b3"]').focus();
    assert.equal(await tooltip(page).textContent(), 'Inspection: Room 4, 6 Nov 2026');
    assert.deepEqual(errors, []);
    await page.close();
});

test('a description opens under its bar or row label, inside the viewport, and the tooltip steps aside', async () => {
    const { page, errors } = await open();
    await page.evaluate(bars => { draw(rooms, bars); schedule.mountSchedule(rooms); }, bars);
    await hover(page, 'First aid');
    await page.locator('[data-key="b2"]').click();
    const popover = page.locator('.app-schedule-popover:popover-open');
    await popover.waitFor();
    await placedPopover(page);
    await tooltip(page).waitFor({ state: 'hidden' });
    assert.equal(await popover.locator('.app-schedule-popover__when').textContent(), 'Room 3, 3 Nov 2026 to 4 Nov 2026, Course');
    assert.equal(await popover.getByRole('link', { name: 'Open link' }).getAttribute('href'), '/site/courses/first-aid');
    const placed = await page.evaluate(() => {
        const box = document.querySelector('.app-schedule-popover:popover-open').getBoundingClientRect(), bar = rooms.querySelector('[data-key="b2"]').getBoundingClientRect();
        return { left: box.left, right: box.right, top: box.top, barLeft: bar.left, barBottom: bar.bottom, viewport: document.documentElement.clientWidth };
    });
    assert.ok(Math.abs(placed.top - (placed.barBottom + 4)) < 1 && Math.abs(placed.left - Math.max(8, placed.barLeft)) < 1 && placed.right <= placed.viewport - 8 + 0.5, JSON.stringify(placed));
    await popover.getByRole('button', { name: 'Close' }).click();
    await popover.waitFor({ state: 'hidden' });

    await page.getByRole('button', { name: 'Room 1' }).click();
    await popover.waitFor();
    await placedPopover(page);
    assert.equal(await popover.locator('.app-schedule-popover__text').textContent(), 'Ground floor, by the entrance.');
    const label = await page.evaluate(() => [document.querySelector('.app-schedule-popover:popover-open').getBoundingClientRect().top,
        rooms.querySelector('.app-schedule-label--button').getBoundingClientRect().bottom]);
    assert.ok(Math.abs(label[0] - (label[1] + 4)) < 1, JSON.stringify(label));
    await page.keyboard.press('Escape');
    await popover.waitFor({ state: 'hidden' });
    assert.deepEqual(errors, []);
    await page.close();
});

test('mounting again replaces the earlier mount, content drawn anew needs no mount, and dispose undoes it', async () => {
    const { page, errors } = await open();
    const counts = await page.evaluate(bars => {
        draw(rooms, bars);
        schedule.mountSchedule(rooms); schedule.mountSchedule(rooms);
        window.dispose = schedule.mountSchedule(rooms);
        return { listeners: documentScrollListeners(), tooltips: rooms.querySelectorAll('.app-schedule-tooltip').length };
    }, bars);
    assert.deepEqual(counts, { listeners: 1, tooltips: 1 });

    // Drawn again from new data, without mounting: the bars still get their tooltip and their popover's place.
    await page.evaluate(() => draw(rooms, [{ row: 4, column: 3, key: 'n1', span: 2, label: 'Fire drill', details: ': Room 5, 4 Nov 2026',
        when: 'Room 5, 4 Nov 2026', description: 'Everyone outside.' }]));
    await hover(page, 'Fire drill');
    assert.equal(await tooltip(page).textContent(), 'Fire drill: Room 5, 4 Nov 2026');
    await page.locator('[data-key="n1"]').click();
    await page.locator('.app-schedule-popover:popover-open').waitFor();
    await placedPopover(page);
    await page.keyboard.press('Escape');

    const disposed = await page.evaluate(() => { dispose(); dispose(); return { listeners: documentScrollListeners(), tooltips: rooms.querySelectorAll('.app-schedule-tooltip').length }; });
    assert.deepEqual(disposed, { listeners: 0, tooltips: 0 });
    await page.mouse.move(0, 0);
    await page.locator('[data-key="n1"]').hover();
    await page.waitForTimeout(100);
    assert.equal(await page.locator('.app-schedule-tooltip').count(), 0);
    // A dispose kept from an earlier mount leaves a later one alone.
    const later = await page.evaluate(() => { schedule.mountSchedule(rooms); dispose(); return documentScrollListeners(); });
    assert.equal(later, 1);
    assert.deepEqual(errors, []);
    await page.close();
});

test('the runtime mounts the Schedule block\'s server markup through the same module', async () => {
    const page = await browser.newPage({ viewport: { width: 480, height: 600 } });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    const columns = Array.from({ length: 30 }, (_, index) => index);
    const cells = column => column === 24 ? ' app-schedule-col--today' : '';
    await page.route('**/*', async route => {
        const url = new URL(route.request().url());
        const asset = await runtimeAsset(url.pathname);
        if (asset) return route.fulfill(asset);
        return route.fulfill({ contentType: 'text/html', body: `<!doctype html><html lang="en"><head><meta name="viewport" content="width=device-width"><title>Schedule</title>
            <link rel="stylesheet" href="/App_Plugins/ApplicationBlocks/runtime.css"></head><body><main class="application-blocks">
            <section class="app-schedule ah-schedule" id="schedule-1" data-app-schedule data-scale="days"><div class="app-schedule-scroll ah-schedule__scroll">
                <table class="app-schedule-table app-schedule-table--days" data-today-column="24"><caption class="app-schedule-caption">Rooms</caption>
                    <thead><tr><th scope="col" class="app-schedule-corner"><span class="app-schedule-label"></span></th>
                        ${columns.map(column => `<th scope="col" class="app-schedule-col${cells(column)}"><span class="app-schedule-day">${column + 1}</span></th>`).join('')}</tr></thead>
                    <tbody class="app-schedule-group"><tr class="app-schedule-row" style="--ah-lanes: 1" data-row-key="r1"><th scope="row" class="app-schedule-row-label"><span class="app-schedule-label">Room 1</span></th>
                        ${columns.map(column => `<td class="app-schedule-cell${cells(column)}">${column === 24 ? '<span class="app-schedule-bar app-schedule-kind-1" tabindex="0" style="--ah-span: 2; --ah-lane: 0" data-key="k1" data-kind="Stay"><span class="app-schedule-bar__label">Ada</span><span class="app-schedule-bar__details">: Room 1, 25 Nov 2026, Stay</span></span>' : ''}</td>`).join('')}</tr></tbody>
                </table></div></section></main><script type="module" src="/App_Plugins/ApplicationBlocks/runtime.js"></script></body></html>` });
    });
    await page.goto('http://localhost/schedule/');
    await page.locator('#schedule-1 .app-schedule-tooltip').waitFor({ state: 'attached' });
    assert.ok(await page.evaluate(() => document.querySelector('.app-schedule-scroll').scrollLeft > 0), 'today is scrolled into view');
    await page.locator('[data-key="k1"]').hover();
    await page.locator('#schedule-1 .app-schedule-tooltip').waitFor({ state: 'visible' });
    assert.equal(await page.locator('#schedule-1 .app-schedule-tooltip').textContent(), 'Ada: Room 1, 25 Nov 2026, Stay');
    assert.deepEqual(errors, []);
    await page.close();
});
