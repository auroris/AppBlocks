import { ApiError, pathBase } from "./data-api.js";
import { t } from "./runtime-text.js";

// Nothing to show, so a new record's default may fill it.
export const blank = value => value === undefined || value === null || value === "";
// Defaults and hidden fields hold their values as text, so a checkbox's arrives as "true" or "false".
export const truthy = value => value === true || typeof value === "string" && /^(true|1|yes|on)$/i.test(value.trim());
// A key for one submission, so a retry is not saved twice. crypto.randomUUID exists only on HTTPS and localhost, and a public
// site may be plain HTTP; getRandomValues exists everywhere and is just as random. Both give a version 4 UUID, which the server parses.
export function newSubmissionKey() {
    if (crypto.randomUUID) return crypto.randomUUID();
    const bytes = crypto.getRandomValues(new Uint8Array(16));
    bytes[6] = bytes[6] & 0x0f | 0x40; bytes[8] = bytes[8] & 0x3f | 0x80;
    const hex = Array.from(bytes, byte => byte.toString(16).padStart(2, "0")).join("");
    return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
// What an anonymous visitor sees when a block's class or engine refuses them, in place of an error.
export function signInNotice(text) {
    const notice = document.createElement("p");
    notice.className = "app-block-signin"; notice.setAttribute("role", "status"); notice.textContent = text;
    return notice;
}
// Whether a refusal asks the caller to sign in: a 401 always, since it says nobody is signed in or a session has ended,
// and a 403 when the page was drawn for someone anonymous, since a signed-in caller refused needs more than signing in.
export const signInNeeded = (error, anonymous) => error instanceof ApiError && (error.status === 401 || error.status === 403 && anonymous);
// What a refused batch of rows says: every message among its errors, or else its detail or title, or else the fallback.
// A refusal of the whole batch, such as a field the data source does not have, carries only errors.
export function problemLines(problem, fallback) {
    const lines = Object.values(problem.errors ?? {}).flat().filter(line => typeof line === "string" && line);
    return lines.length ? lines : [problem.detail || problem.title || fallback];
}
// A block's endpoint below the address the server gave it, such as a record's key, with the page's own query so the
// endpoint sees the parameters the page did, the endpoint's own query values over those, and the given values last.
export function endpointUrl(api, segments = [], query = {}) {
    const url = new URL(api, location.origin);
    for (const segment of segments) url.pathname += `/${encodeURIComponent(segment)}`;
    const internalQuery = new URLSearchParams(url.search);
    url.search = location.search;
    for (const [name, value] of internalQuery) url.searchParams.set(name, value);
    for (const [name, value] of Object.entries(query)) url.searchParams.set(name, value);
    return url;
}
// One of a block's own buttons. It carries form="" so the enclosing form neither submits on it nor counts it among its controls.
export function actionButton(label, onClick) {
    const button = document.createElement("button");
    button.type = "button"; button.setAttribute("form", ""); button.textContent = label;
    button.addEventListener("click", onClick);
    return button;
}
// A datetime-local control holds wall-clock time with no zone, and the server sends and takes ISO date-times.
export function toLocalInput(value) {
    const date = new Date(value);
    return new Date(date.getTime() - date.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
}
export const fromLocalInput = text => text === "" ? null : new Date(text).toISOString();
// A value as text, for a link's text and its address: nothing for no value, true or false for a checkbox.
export const plain = value => blank(value) ? "" : String(value).trim();
// A record's own value of that name, so a template naming {constructor} reads a field or nothing.
export const valueOf = (values, name) => Object.hasOwn(values, name) ? values[name] : null;
// An address a page may link to: a path, or an http, https, mailto or tel address. Another scheme, such as javascript:, and
// an address that starts with two slashes, which leaves the site as a full address would, give null. AppBlockField.IsSafeLink
// is the same rule, which publishing applies to the template.
export function safeAddress(link) {
    if (!link || /[\u0000-\u001f\u007f-\u009f]/.test(link) || /^[/\\]{2}/.test(link)) return null;
    const scheme = /^([a-z][a-z0-9+.-]*):/i.exec(link);
    return !scheme || /^(https?|mailto|tel)$/i.test(scheme[1]) ? link : null;
}
// The address a Display value links to, from its template and the record shown: {field} becomes that field's value, escaped
// so that it stays the one part of the address it was written into. A template that is one field alone takes the whole
// address from it, for a field that holds an address. A path from the site's root gets the site's path base.
export function recordAddress(template, values) {
    const text = (template ?? "").trim();
    const whole = /^\{([^{}]*)\}$/.exec(text);
    // A mail or phone address has no host for a value to change, and mail and dialling programs differ on whether they read
    // %40 and %2B back, so there an @ and a + stay as written.
    const literal = /^(mailto|tel):/i.test(text);
    const escaped = value => literal ? encodeURIComponent(value).replace(/%40/g, "@").replace(/%2B/gi, "+") : encodeURIComponent(value);
    const link = safeAddress(whole ? plain(valueOf(values, whole[1].trim())) : text.replace(/\{([^{}]*)\}/g, (_, name) => escaped(plain(valueOf(values, name.trim())))));
    return link?.startsWith("/") ? pathBase + link : link;
}
// A stored value no longer among the choices stays visible and selected as it is, so saving another field leaves it alone.
export function retiredOption(choices, value) {
    if (blank(value) || [...choices].some(choice => choice.value === String(value))) return null;
    return new Option(t("currentValue", { value }), String(value));
}
// The form's other named controls, such as a captcha widget's hidden token or a honeypot: everything the browser would
// have submitted except its fields, passwords, files and the antiforgery field, which travels in a header. Sent apart from
// the fields, so the server's validation and strict binding never see them. A repeated name goes as a list, which the
// server leaves out rather than guess at. A block's own controls carry form="", so they are not among the form's elements.
export function otherControls(form) {
    const controls = {};
    const add = (name, value) => { controls[name] = Object.hasOwn(controls, name) ? [controls[name], value].flat() : value; };
    for (const control of form.elements) {
        if (!control.name || !["INPUT", "SELECT", "TEXTAREA"].includes(control.tagName)) continue;
        if (control.matches("[data-field], :disabled") || control.name === "__RequestVerificationToken") continue;
        if (["password", "file", "submit", "button", "reset", "image"].includes(control.type)) continue;
        if (["checkbox", "radio"].includes(control.type) && !control.checked) continue;
        if (control.type === "select-multiple") for (const option of control.selectedOptions) add(control.name, option.value);
        else add(control.name, control.value);
    }
    return controls;
}
