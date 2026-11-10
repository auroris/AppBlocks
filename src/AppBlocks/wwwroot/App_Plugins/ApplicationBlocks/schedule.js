// A schedule's behaviour in the browser, and the markup of its bars and row labels. The Schedule block's server lays the
// whole table out and the runtime mounts it; a page that lays out a schedule table of its own builds its bars and row
// labels with scheduleBar and scheduleRowLabel, styles them with runtime.css and calls mountSchedule on its root, so the
// two share one markup. Importing this module mounts nothing. docs/schedule.md describes the markup element by element.

// A colour reaches CSS only as a hex value or a name, as the server's ScheduleColumns.Color allows.
const safeColor = value => typeof value === "string" && /^(#([0-9a-f]{3,4}|[0-9a-f]{6}|[0-9a-f]{8})|[a-z]{3,30})$/i.test(value) ? value.toLowerCase() : null;
// A link reaches href only as a path from the site's root or an http, https, mailto or tel address, as the server's
// ScheduleColumns.Link allows, so a value from a row never runs script. "//host" and "/\host" lead to another site, and a
// browser drops tabs and line breaks from an address, which could turn a path into either, so all three are refused.
function safeLink(value) {
    const link = typeof value === "string" ? value.trim() : "";
    if (!link || /[\u0000-\u001f\u007f]/.test(link)) return null;
    if (link.startsWith("/")) return /^\/[/\\]/.test(link) ? null : link;
    return /^(https?|mailto|tel):/i.test(link) ? link : null;
}
let popovers = 0;
// Text, or nodes a page built itself, appended as they are.
function fill(element, content) {
    if (content instanceof Node) element.append(content);
    else if (Array.isArray(content)) element.append(...content);
    else element.textContent = content ?? "";
    return element;
}
function element(tag, className, content) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    return content === undefined ? node : fill(node, content);
}
// The browser's own popover, opened by its button without any script.
function popover(title, lines, closeLabel) {
    const box = element("div", "app-schedule-popover");
    box.id = `app-schedule-popover-${++popovers}`;
    box.popover = "auto";
    const heading = element("p", "app-schedule-popover__title", title);
    heading.id = `${box.id}-title`;
    box.setAttribute("aria-labelledby", heading.id);
    box.append(heading, ...lines);
    const close = element("button", "app-schedule-popover__close", "×");
    close.type = "button"; close.setAttribute("aria-label", closeLabel || "Close");
    close.setAttribute("popovertarget", box.id); close.setAttribute("popovertargetaction", "hide");
    box.append(close);
    return box;
}

// One bar, to append to the cell of the column where it starts: a fragment holding the bar, and after it the popover
// when it has a description.
//   label        the text on the bar
//   details      read after the label by screen readers and shown in the tooltip, such as ": Room 1, 3 Nov 2026 to 5 Nov 2026"
//   span, lane   how many columns it covers, and which of its row's lanes it sits in, from 0
//   kindIndex    1 to 8 gives it the colour --ah-schedule-kind-N, unless it has a colour of its own
//   color, textColor   its own background and text colour, a hex value or a colour name; anything else is ignored
//   openEnded, clippedStart, clippedEnd   it runs on past the window's end, or is cut at the window's start or end
//   vertical     it is a marker in a vertical row, its label written top to bottom
//   key, kind    carried as data-key and data-kind for a page's own script
//   link         where it leads: a path from the site's root, or an http, https, mailto or tel address; anything else
//                is ignored. A bar with neither link nor description is a focusable span
//   description  text or nodes for its popover, which makes it a button; the popover also shows when and link
//   when         the popover's second line, such as "Room 1, 3 Nov 2026 to 5 Nov 2026"
//   linkText, closeLabel   the popover's link text and its close button's name, "Open link" and "Close" unless given
export function scheduleBar(bar) {
    const color = safeColor(bar.color), textColor = safeColor(bar.textColor);
    const classes = ["app-schedule-bar"];
    if (!color && bar.kindIndex > 0) classes.push(`app-schedule-kind-${(bar.kindIndex - 1) % 8 + 1}`);
    if (bar.openEnded) classes.push("app-schedule-bar--open");
    if (bar.clippedStart) classes.push("app-schedule-bar--clipped-start");
    if (bar.clippedEnd) classes.push("app-schedule-bar--clipped-end");
    if (bar.vertical) classes.push("app-schedule-bar--vertical");
    const hasDescription = bar.description !== undefined && bar.description !== null && bar.description !== "";
    const link = safeLink(bar.link);
    const node = element(hasDescription ? "button" : link ? "a" : "span", classes.join(" "));
    node.style.setProperty("--ah-span", String(Math.max(1, Math.trunc(bar.span ?? 1))));
    node.style.setProperty("--ah-lane", String(Math.max(0, Math.trunc(bar.lane ?? 0))));
    if (color) node.style.setProperty("--ah-bar", color);
    if (textColor) node.style.setProperty("--ah-bar-text", textColor);
    if (bar.key !== undefined && bar.key !== null) node.dataset.key = String(bar.key);
    if (bar.kind !== undefined && bar.kind !== null) node.dataset.kind = String(bar.kind);
    node.append(element("span", "app-schedule-bar__label", bar.label ?? ""), element("span", "app-schedule-bar__details", bar.details ?? ""));
    const fragment = document.createDocumentFragment();
    fragment.append(node);
    if (hasDescription) {
        const lines = [];
        if (bar.when) lines.push(element("p", "app-schedule-popover__when", bar.when));
        lines.push(element("p", "app-schedule-popover__text", bar.description));
        if (link) {
            const anchor = element("a", null, bar.linkText || "Open link");
            anchor.href = link;
            lines.push(element("p", "app-schedule-popover__link", anchor));
        }
        const box = popover(bar.label ?? "", lines, bar.closeLabel);
        node.type = "button";
        node.setAttribute("popovertarget", box.id);
        fragment.append(box);
    } else if (link) node.href = link;
    else node.tabIndex = 0;
    return fragment;
}

// A row's label cell, the th that starts its tr: its label, its colours, and a description its label opens, whose close
// button closeLabel names ("Close" unless given).
export function scheduleRowLabel(row) {
    const cell = element("th", "app-schedule-row-label");
    cell.scope = "row";
    const color = safeColor(row.color), textColor = safeColor(row.textColor);
    if (color) cell.style.setProperty("--ah-row", color);
    if (textColor) cell.style.setProperty("--ah-row-text", textColor);
    if (row.description === undefined || row.description === null || row.description === "") {
        cell.append(element("span", "app-schedule-label", row.label ?? ""));
        return cell;
    }
    const box = popover(row.label ?? "", [element("p", "app-schedule-popover__text", row.description)], row.closeLabel);
    const opener = element("button", "app-schedule-label app-schedule-label--button", row.label ?? "");
    opener.type = "button";
    opener.setAttribute("popovertarget", box.id);
    cell.append(opener, box);
    return cell;
}

const mounted = new WeakMap();
let tooltips = 0;
const viewportWidth = () => document.documentElement.clientWidth || window.innerWidth;

// Brings today's column into view, shows a bar's details as a tooltip on hover and focus, and places the popover a bar
// or row label opens under it. Everything inside the root is found when it happens, so the root's content may be drawn
// again without mounting again. Mounting a root again, as after drawing it anew, replaces the earlier mount; the
// function returned undoes the mount.
export function mountSchedule(root) {
    mounted.get(root)?.();
    const controller = new AbortController();
    const options = { signal: controller.signal };
    const scroller = root.querySelector(".app-schedule-scroll");
    const today = root.querySelector("thead .app-schedule-col--today");
    if (scroller && today) {
        // Today lands just right of the sticky label column, with two columns of context before it.
        const labels = root.querySelector(".app-schedule-corner");
        scroller.scrollLeft = Math.max(0, today.offsetLeft - (labels ? labels.offsetWidth : 0) - today.offsetWidth * 2);
    }
    const tooltip = document.createElement("div");
    tooltip.className = "app-schedule-tooltip";
    tooltip.id = `app-schedule-tooltip-${++tooltips}`;
    tooltip.setAttribute("aria-hidden", "true");
    tooltip.hidden = true;
    root.append(tooltip);
    // The bar whose details show, and where it was in the viewport when the tooltip was put under it.
    let active = null, placed = null;
    const barOf = target => target instanceof Element ? target.closest(".app-schedule-bar") : null;
    function show(bar) {
        if (!bar || bar === active || !root.contains(bar)) return;
        active = bar;
        // Content drawn again takes the tooltip with it.
        if (tooltip.parentNode !== root) root.append(tooltip);
        tooltip.textContent = bar.textContent;
        tooltip.hidden = false;
        // Under the bar, kept inside the viewport even when the schedule is wider than it.
        const bounds = bar.getBoundingClientRect(), frame = root.getBoundingClientRect();
        placed = { left: bounds.left, top: bounds.top };
        tooltip.style.left = `${Math.max(8, Math.min(bounds.left, viewportWidth() - tooltip.offsetWidth - 8)) - frame.left}px`;
        tooltip.style.top = `${bounds.bottom - frame.top + 4}px`;
    }
    function hide() { active = null; tooltip.hidden = true; }
    root.addEventListener("mouseover", event => show(barOf(event.target)), options);
    root.addEventListener("mouseout", event => { if (active && barOf(event.target) === active && barOf(event.relatedTarget) !== active) hide(); }, options);
    root.addEventListener("focusin", event => show(barOf(event.target)), options);
    root.addEventListener("focusout", event => { if (active && barOf(event.target) === active) hide(); }, options);
    root.addEventListener("keydown", event => { if (event.key === "Escape") hide(); }, options);
    // Scrolling anything, the schedule's own scroller, a panel around it or the page, moves the bar and leaves the tooltip
    // behind it, so the tooltip goes. A scroll that left the bar where it was does not: the browser brings a bar into view
    // as it takes focus and reports that scroll afterwards, when the tooltip is already under the bar.
    document.addEventListener("scroll", () => {
        if (!active) return;
        const bounds = active.getBoundingClientRect();
        if (Math.abs(bounds.left - placed.left) >= .5 || Math.abs(bounds.top - placed.top) >= .5) hide();
    }, { ...options, capture: true, passive: true });
    // The browser centres a popover; here it goes under its opener, kept inside the viewport, and the tooltip steps aside.
    root.addEventListener("toggle", event => {
        const popover = event.target;
        if (event.newState !== "open" || !(popover instanceof Element) || !popover.matches(".app-schedule-popover")) return;
        hide();
        const opener = root.querySelector(`[popovertarget="${CSS.escape(popover.id)}"]:not(.app-schedule-popover__close)`);
        if (!opener) return;
        const bounds = opener.getBoundingClientRect(), gap = 8;
        popover.style.margin = "0";
        popover.style.inset = "auto";
        const left = Math.max(gap, Math.min(bounds.left, viewportWidth() - popover.offsetWidth - gap));
        let top = bounds.bottom + 4;
        if (top + popover.offsetHeight > window.innerHeight - gap) top = Math.max(gap, bounds.top - popover.offsetHeight - 4);
        popover.style.left = `${left}px`;
        popover.style.top = `${top}px`;
    }, { ...options, capture: true });
    function dispose() {
        if (mounted.get(root) !== dispose) return;
        mounted.delete(root);
        controller.abort();
        tooltip.remove();
    }
    mounted.set(root, dispose);
    return dispose;
}
