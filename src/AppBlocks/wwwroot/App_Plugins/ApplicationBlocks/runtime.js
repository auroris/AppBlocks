import { ApiError, errorText, request } from "./data-api.js";
import { mountSchedule } from "./schedule.js";

// Every text the runtime shows, by id, in English. A site that translates or rewords them writes a JSON object of the ids
// it changes into <script type="application/json" data-appblocks-strings>, read once here; an id it leaves out, or a value
// that is not text, keeps the English. {name} stands for a value filled in when the text is shown.
const strings = {
    loading: "Loading…",
    saving: "Saving…",
    deleting: "Deleting…",
    uploading: "Uploading…",
    removing: "Removing…",
    saved: "Saved.",
    yes: "Yes",
    no: "No",
    currentValue: "{value} (current value)",
    requestFailed: "The request could not be completed.",
    requestFailedRetained: "The request could not be completed. Your entered values have been retained.",
    signInForm: "Sign in to use this form.",
    signInFiles: "Sign in to see the files.",
    signInRows: "Sign in to see the rows.",
    signInThenRetry: "Sign in, then try again.",
    antiSpamUnavailable: "The anti-spam check could not load. Check your connection or anything blocking it, then reload the page.",
    antiSpamIncomplete: "Complete the anti-spam check, then try again.",
    shuttleOneLine: "The selected values must be one line of CSV.",
    shuttleInvalid: "The selected CSV is invalid.",
    shuttleUnique: "Selected values must be unique and non-empty.",
    shuttleRequired: "Select at least one item.",
    shuttleSelected: "{count} selected.",
    shuttleOrder: "{count} selected. Order: {order}.",
    shuttleOrderNone: "none",
    formForbidden: "You do not have access to this form.",
    recordNotFound: "The record was not found.",
    formLoadFailed: "The form could not be loaded.",
    confirmDelete: "Delete this record?",
    rowsRefusedRecordSaved: "The record was saved, but its rows were not. Correct the rows marked below and save again.",
    rowsRefusedNothingSaved: "Nothing was saved. Correct the rows marked below and save again.",
    operationForbidden: "This operation is not permitted.",
    tooLarge: "This is too much to send at once. Shorten what you entered and try again.",
    tooManyRequests: "That was too many tries. Wait a minute and try again.",
    operationFailed: "The operation failed. Reload the page if the record has changed.",
    saveRecordBeforeFiles: "Save the record before adding files.",
    openDatabaseSetup: "Open database setup",
    noFiles: "No files yet.",
    earlierRevision: "1 earlier revision",
    earlierRevisions: "{count} earlier revisions",
    replaceFile: "Replace",
    removeFile: "Remove",
    confirmRemoveFile: "Remove {name}?",
    filesLoadFailed: "The files could not be loaded.",
    filesNotAttached: "The record was saved, but its files could not be attached to it. Save again to retry.",
    fileTooLarge: "The file is larger than the {size} MB this block accepts.",
    fileOperationFailed: "The operation failed.",
    chooseFile: "Choose a file to upload.",
    sizeBytes: "{size} B",
    sizeKilobytes: "{size} KB",
    sizeMegabytes: "{size} MB",
    noRows: "No rows yet.",
    deleteRow: "Delete",
    undoDelete: "Undo",
    saveRecordBeforeRows: "Save the record before adding rows.",
    reloadRows: "Reload rows",
    rowsLoadFailed: "The rows could not be loaded.",
    rowNumber: "Row {number}",
    rowConflict: "This row was changed elsewhere. Reload the rows to see it; the changes entered here would then be lost.",
    rowsSaveFailed: "The rows could not be saved.",
    rowsSaveFailedReload: "The rows could not be saved. Reload the page and try again.",
    nothingToSave: "Nothing to save.",
};
try {
    const overrides = JSON.parse(document.querySelector("script[data-appblocks-strings]")?.textContent || "{}") ?? {};
    for (const [id, text] of Object.entries(overrides)) if (Object.hasOwn(strings, id) && typeof text === "string") strings[id] = text;
} catch { /* A table that is not JSON leaves every text in English rather than stop the blocks. */ }
const t = (id, values = {}) => strings[id].replace(/\{(\w+)\}/g, (match, name) => Object.hasOwn(values, name) ? String(values[name]) : match);
// Dates, times and sizes follow the page's language, as the server's own text does, or the browser's when the page names
// none or one the browser does not know.
const locale = (() => { try { return Intl.getCanonicalLocales(document.documentElement.lang || [])[0]; } catch { return undefined; } })();
const dateTimeText = value => new Date(value).toLocaleString(locale);
const numberText = (value, digits = 0) => value.toLocaleString(locale, { minimumFractionDigits: digits, maximumFractionDigits: digits });
const sizeText = bytes => bytes < 1024 ? t("sizeBytes", { size: numberText(bytes) })
    : bytes < 1048576 ? t("sizeKilobytes", { size: numberText(Math.round(bytes / 1024)) }) : t("sizeMegabytes", { size: numberText(bytes / 1048576, 1) });

// Nothing to show, so a new record's default may fill it.
const blank = value => value === undefined || value === null || value === "";
// Defaults and hidden fields hold their values as text, so a checkbox's arrives as "true" or "false".
const truthy = value => value === true || typeof value === "string" && /^(true|1|yes|on)$/i.test(value.trim());
// A key for one submission, so a retry is not saved twice. crypto.randomUUID exists only on HTTPS and localhost, and a public
// site may be plain HTTP; getRandomValues exists everywhere and is just as random. Both give a version 4 UUID, which the server parses.
function newSubmissionKey() {
    if (crypto.randomUUID) return crypto.randomUUID();
    const bytes = crypto.getRandomValues(new Uint8Array(16));
    bytes[6] = bytes[6] & 0x0f | 0x40; bytes[8] = bytes[8] & 0x3f | 0x80;
    const hex = Array.from(bytes, byte => byte.toString(16).padStart(2, "0")).join("");
    return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
// What an anonymous visitor sees when a block's class or engine refuses them, in place of an error.
function signInNotice(text) {
    const notice = document.createElement("p");
    notice.className = "app-block-signin"; notice.setAttribute("role", "status"); notice.textContent = text;
    return notice;
}
// Whether a refusal asks the caller to sign in: a 401 always, since it says nobody is signed in or a session has ended,
// and a 403 when the page was drawn for someone anonymous, since a signed-in caller refused needs more than signing in.
const signInNeeded = (error, anonymous) => error instanceof ApiError && (error.status === 401 || error.status === 403 && anonymous);
// What a refused batch of rows says: every message among its errors, or else its detail or title, or else the fallback.
// A refusal of the whole batch, such as a field the data source does not have, carries only errors.
function problemLines(problem, fallback) {
    const lines = Object.values(problem.errors ?? {}).flat().filter(line => typeof line === "string" && line);
    return lines.length ? lines : [problem.detail || problem.title || fallback];
}
// A block's endpoint below the address the server gave it, such as a record's key, with the page's own query so the
// endpoint sees the parameters the page did, the endpoint's own query values over those, and the given values last.
function endpointUrl(api, segments = [], query = {}) {
    const url = new URL(api, location.origin);
    for (const segment of segments) url.pathname += `/${encodeURIComponent(segment)}`;
    const internalQuery = new URLSearchParams(url.search);
    url.search = location.search;
    for (const [name, value] of internalQuery) url.searchParams.set(name, value);
    for (const [name, value] of Object.entries(query)) url.searchParams.set(name, value);
    return url;
}
// One of a block's own buttons. It carries form="" so the enclosing form neither submits on it nor counts it among its controls.
function actionButton(label, onClick) {
    const button = document.createElement("button");
    button.type = "button"; button.setAttribute("form", ""); button.textContent = label;
    button.addEventListener("click", onClick);
    return button;
}
// A datetime-local control holds wall-clock time with no zone, and the server sends and takes ISO date-times.
function toLocalInput(value) {
    const date = new Date(value);
    return new Date(date.getTime() - date.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
}
const fromLocalInput = text => text === "" ? null : new Date(text).toISOString();
// A stored value no longer among the choices stays visible and selected as it is, so saving another field leaves it alone.
function retiredOption(choices, value) {
    if (blank(value) || [...choices].some(choice => choice.value === String(value))) return null;
    return new Option(t("currentValue", { value }), String(value));
}
// The form's other named controls, such as a captcha widget's hidden token or a honeypot: everything the browser would
// have submitted except its fields, passwords, files and the antiforgery field, which travels in a header. Sent apart from
// the fields, so the server's validation and strict binding never see them. A repeated name goes as a list, which the
// server leaves out rather than guess at. A block's own controls carry form="", so they are not among the form's elements.
function otherControls(form) {
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

// The Anti-spam block's widget. The server rendered its script and says which global it provides, with that service's render
// and reset, or its execute when it shows no widget. All this does is put a token in a control inside the form, where
// otherControls finds it: the server decides, and refuses a submission without one whatever happens here.
function mountAntiSpam(root, form) {
    const api = () => window[root.dataset.api];
    const siteKey = root.dataset.siteKey;
    const action = root.dataset.action;
    const field = root.dataset.tokenField;
    let widget, blocked = false;
    const wait = member => new Promise(resolve => {
        const started = Date.now();
        (function poll() {
            if (typeof api()?.[member] === "function") resolve(true);
            else if (Date.now() - started > 15000) { blocked = true; resolve(false); }
            else setTimeout(poll, 100);
        })();
    });
    const loaded = wait(action ? "execute" : "render");
    function reset() {
        const input = action && root.querySelector("input");
        if (input) input.value = "";
        if (widget !== undefined) { try { api().reset(widget); } catch { /* A widget that will not reset leaves no token, and the server says so. */ } }
    }
    // A token works once, and one that sits too long expires.
    if (!action) loaded.then(ok => { if (ok) widget = api().render(root, { sitekey: siteKey, ...JSON.parse(root.dataset.options || "{}"), "expired-callback": reset }); });
    const hasToken = () => [...form.elements].some(control => control.name === field && control.value);
    const unavailable = t("antiSpamUnavailable");
    return {
        reset,
        // Returns what to tell the visitor when the form is not ready to send, or nothing when it is.
        async prepare() {
            if (!action) return hasToken() ? null : blocked ? unavailable : t("antiSpamIncomplete");
            if (!await loaded) return unavailable;
            try {
                const token = await Promise.race([
                    new Promise((resolve, reject) => (api().ready ?? (run => run()))(() => api().execute(siteKey, { action }).then(resolve, reject))),
                    new Promise((_, reject) => setTimeout(reject, 15000)),
                ]);
                let input = root.querySelector("input");
                if (!input) { input = document.createElement("input"); input.type = "hidden"; input.name = field; root.append(input); }
                input.value = token;
                return null;
            } catch { return unavailable; }
        },
    };
}

// One CSV record, matching ShuttleValue on the server. Item values cannot contain line breaks or be empty/duplicated.
function parseShuttleValue(csv) {
    if (!csv) return [];
    if (/[\r\n]/.test(csv)) throw new Error(t("shuttleOneLine"));
    const values = [], seen = new Set();
    let position = 0;
    while (position < csv.length) {
        let value = "";
        if (csv[position] === '"') {
            position++;
            let closed = false;
            while (position < csv.length) {
                const c = csv[position++];
                if (c !== '"') { value += c; continue; }
                if (csv[position] === '"') { value += '"'; position++; }
                else { closed = true; break; }
            }
            if (!closed || position < csv.length && csv[position] !== ',') throw new Error(t("shuttleInvalid"));
        } else {
            while (position < csv.length && csv[position] !== ',') {
                if (csv[position] === '"') throw new Error(t("shuttleInvalid"));
                value += csv[position++];
            }
        }
        if (!value.trim() || seen.has(value)) throw new Error(t("shuttleUnique"));
        seen.add(value); values.push(value);
        if (position < csv.length && ++position === csv.length) throw new Error(t("shuttleInvalid"));
    }
    return values;
}
const shuttleCsv = values => values.map(value => /[",]/.test(value) ? `"${value.replaceAll('"', '""')}"` : value).join(",");

function mountShuttle(root) {
    const field = root.querySelector("[data-field]");
    const available = root.querySelector("[data-shuttle-available]");
    const selected = root.querySelector("[data-shuttle-selected]");
    const status = root.querySelector("[role=status]");
    const buttons = [...root.querySelectorAll("[data-shuttle-action]")];
    const choices = new Map([...available.options].map(option => [option.value, option.textContent]));
    let problem = "";
    function refresh() {
        available.disabled = selected.disabled = field.disabled;
        selected.setCustomValidity(problem || (root.dataset.required === "true" && !selected.options.length ? t("shuttleRequired") : ""));
        const items = [...selected.options];
        const canUp = items.some((option, index) => option.selected && index > 0 && !items[index - 1].selected);
        const canDown = items.some((option, index) => option.selected && index < items.length - 1 && !items[index + 1].selected);
        const enabled = { add: available.selectedOptions.length, "add-all": available.options.length,
            remove: selected.selectedOptions.length, "remove-all": items.length, up: canUp, top: canUp, down: canDown, bottom: canDown };
        for (const button of buttons) button.disabled = field.disabled || !enabled[button.dataset.shuttleAction];
    }
    function display() {
        let values;
        problem = "";
        try { values = parseShuttleValue(field.value); }
        catch (error) { values = []; problem = error.message; }
        const picked = new Set(values);
        available.replaceChildren(...[...choices].filter(([value]) => !picked.has(value)).map(([value, label]) => new Option(label, value)));
        selected.replaceChildren(...values.map(value => new Option(choices.get(value) ?? t("currentValue", { value }), value)));
        status.textContent = problem || t("shuttleSelected", { count: values.length });
        refresh();
    }
    function act(action) {
        if (field.disabled || field.matches(":disabled")) return;
        if (action === "add" || action === "add-all") {
            const moved = [...(action === "add-all" ? available.options : available.selectedOptions)];
            for (const option of selected.options) option.selected = false;
            for (const option of moved) { option.selected = true; selected.append(option); }
        } else if (action === "remove" || action === "remove-all") {
            const removed = [...(action === "remove-all" ? selected.options : selected.selectedOptions)];
            for (const option of removed) option.remove();
            const remaining = new Set([...selected.options].map(option => option.value));
            const removedValues = new Set(removed.map(option => option.value));
            available.replaceChildren(...[...choices].filter(([value]) => !remaining.has(value)).map(([value, label]) => new Option(label, value, false, removedValues.has(value))));
        } else {
            const items = [...selected.options];
            if (action === "top") items.sort((a, b) => Number(b.selected) - Number(a.selected));
            else if (action === "bottom") items.sort((a, b) => Number(a.selected) - Number(b.selected));
            else if (action === "up") {
                for (let i = 1; i < items.length; i++) if (items[i].selected && !items[i - 1].selected) [items[i - 1], items[i]] = [items[i], items[i - 1]];
            } else if (action === "down") {
                for (let i = items.length - 2; i >= 0; i--) if (items[i].selected && !items[i + 1].selected) [items[i], items[i + 1]] = [items[i + 1], items[i]];
            }
            selected.append(...items);
        }
        problem = "";
        field.value = shuttleCsv([...selected.options].map(option => option.value));
        field.dispatchEvent(new Event("input", { bubbles: true }));
        field.dispatchEvent(new Event("change", { bubbles: true }));
        status.textContent = t("shuttleOrder", { count: selected.options.length, order: [...selected.options].map(option => option.textContent).join(", ") || t("shuttleOrderNone") });
        refresh();
        // A disabled boundary button cannot keep keyboard focus after moving the first/last item.
        if (buttons.includes(document.activeElement) && document.activeElement.disabled) selected.focus();
    }
    for (const button of buttons) button.addEventListener("click", () => act(button.dataset.shuttleAction));
    available.addEventListener("change", refresh); selected.addEventListener("change", refresh);
    available.addEventListener("dblclick", () => act("add")); selected.addEventListener("dblclick", () => act("remove"));
    return { display, control: selected };
}

// The text field owns the value: a native color input alone cannot represent an empty field.
function mountColor(root) {
    const field = root.querySelector("[data-field]");
    const picker = root.querySelector("[data-color-picker]");
    const clear = root.querySelector("[data-color-clear]");
    function display() {
        const valid = /^#[0-9a-f]{6}$/i.test(field.value);
        picker.value = valid ? field.value : "#000000";
        root.dataset.empty = String(!valid);
        picker.disabled = field.disabled;
        clear.disabled = field.disabled || !field.value;
    }
    function changed() {
        field.dispatchEvent(new Event("input", { bubbles: true }));
        field.dispatchEvent(new Event("change", { bubbles: true }));
    }
    function pick() {
        if (field.matches(":disabled")) return;
        field.value = picker.value;
        changed();
    }
    picker.addEventListener("input", pick);
    picker.addEventListener("change", pick);
    clear.addEventListener("click", () => {
        if (field.matches(":disabled")) return;
        field.value = "";
        changed();
        field.focus();
    });
    field.addEventListener("input", display);
    field.addEventListener("change", display);
    return { display };
}

// The tabular blocks of each form whose rows go with its save, as each registered itself: see mountTabular.
const formDetails = new WeakMap();
// Each form's current submission key, which an Attachments block inside it sends a new record's files under: see mountAttachments.
const formSubmissions = new WeakMap();

function mountForm(form) {
    const fields = [...form.querySelectorAll("[data-field]")];
    const dateTimes = new Map();
    const shuttles = new Map([...form.querySelectorAll("[data-app-shuttle]")].map(root => [root.querySelector("[data-field]"), mountShuttle(root)]));
    const colors = new Map([...form.querySelectorAll("[data-app-color]")].map(root => [root.querySelector("[data-field]"), mountColor(root)]));
    const errors = form.querySelector(".app-form-errors");
    const message = form.querySelector(".app-form-message");
    const body = form.querySelector(".app-form-body");
    const anonymous = form.dataset.anonymous === "true";
    let state;
    let busy = false;
    let submissionKey = newSubmissionKey();
    const antiSpam = [...form.querySelectorAll("[data-app-antispam]")].map(root => mountAntiSpam(root, form));
    // A hidden field has no message element of its own.
    const errorOf = field => document.getElementById(`${field.id}-error`);
    const requestUrl = (key = null) => endpointUrl(form.dataset.api, key ? [key] : []);
    formSubmissions.set(form, () => submissionKey);
    function showErrors(values) {
        errors.replaceChildren(); errors.hidden = !values || !Object.keys(values).length;
        for (const field of fields) { (shuttles.get(field)?.control ?? field).removeAttribute("aria-invalid"); const error = errorOf(field); if (error) error.textContent = ""; }
        if (errors.hidden) return;
        const list = document.createElement("ul");
        for (const [key, entries] of Object.entries(values)) {
            const field = fields.find(input => input.dataset.field === key);
            const control = shuttles.get(field)?.control ?? field;
            if (control) { control.setAttribute("aria-invalid", "true"); const error = errorOf(field); if (error) error.textContent = entries.join(" "); }
            for (const text of entries) {
                const item = document.createElement("li");
                // A hidden field cannot take focus, so its message names the field instead of linking to it.
                if (control?.type === "hidden") item.textContent = `${field.dataset.label ?? key}: ${text}`;
                else if (control) { const link = document.createElement("a"); link.href = `#${control.id}`; link.textContent = text; item.append(link); }
                else item.textContent = text;
                list.append(item);
            }
        }
        errors.append(list); errors.focus();
    }
    function display(record) {
        state.record = record;
        for (const field of fields) {
            let value = record.values[field.dataset.field];
            // A new record's empty fields take the block's default, which the server resolved when it rendered the page.
            if (!record.key && blank(value) && field.dataset.default !== undefined) value = field.dataset.default;
            if (field.tagName === "SELECT") {
                field.querySelectorAll("option[data-stored-value]").forEach(option => option.remove());
                const retired = record.key ? retiredOption(field.options, value) : null;
                if (retired) { retired.dataset.storedValue = ""; field.append(retired); }
            }
            if (field.type === "checkbox") field.checked = truthy(value);
            else if (field.type === "datetime-local" && !blank(value)) field.value = toLocalInput(value);
            else field.value = value ?? "";
            // The minute-level control cannot represent every second, fraction or offset of the stored value.
            if (field.type === "datetime-local") dateTimes.set(field, { shown: field.value, value: blank(value) ? null : value });
            field.disabled = field.dataset.readonly === "true" || !(record.key ? record.capabilities.update : record.capabilities.create);
            shuttles.get(field)?.display();
            colors.get(field)?.display();
        }
        for (const button of form.querySelectorAll("[data-save]")) button.hidden = !(record.key ? record.capabilities.update : record.capabilities.create);
        for (const button of form.querySelectorAll("[data-delete]")) button.hidden = !record.key || !record.capabilities.delete;
        for (const link of form.querySelectorAll("[data-print]")) {
            link.hidden = !record.key;
            if (record.key) {
                const url = new URL(link.dataset.printUrl, location.origin);
                url.search = location.search;
                url.searchParams.set(link.dataset.printKeyParameter || "id", record.key);
                link.href = url.toString();
            }
        }
        body.disabled = false;
    }
    async function load() {
        message.textContent = t("loading");
        try {
            state = await request(requestUrl()); display(state.record); message.textContent = "";
            // Blocks that follow the record, such as Attachments, learn which record is shown.
            form.dispatchEvent(new CustomEvent("app-form:loaded", { bubbles: true, detail: { record: state.record, definitionVersion: state.definitionVersion } }));
        } catch (error) {
            const status = error instanceof ApiError ? error.status : 0;
            // The page normally knows this already; a class can still change its mind between rendering and this load.
            if (signInNeeded(error, anonymous)) { form.replaceWith(signInNotice(t("signInForm"))); return; }
            showErrors({ "": [t(status === 403 ? "formForbidden" : status === 404 ? "recordNotFound" : "formLoadFailed")] }); message.textContent = "";
        }
    }
    async function submit(remove = false) {
        if (!state || busy) return;
        if (!remove && !form.reportValidity()) return;
        if (remove && !window.confirm(t("confirmDelete"))) return;
        const values = {};
        if (!remove) for (const field of fields.filter(input => !input.disabled && input.dataset.readonly !== "true")) {
            let value = field.type === "checkbox" ? field.checked : field.value;
            // A hidden field is submitted like any other; one bound to a checkbox sends true or false, as a checkbox would.
            if (field.type === "hidden" && field.dataset.kind === "checkbox") value = value === "" ? null : truthy(value);
            // Preserve decimal precision; the resource binds this invariant string to decimal.
            else if (field.type === "number") value = value === "" ? null : value;
            else if (field.type === "datetime-local") {
                const original = dateTimes.get(field);
                value = value === original.shown ? original.value : fromLocalInput(value);
            }
            else if (value === "" && field.dataset.kind !== "shuttle") value = null;
            values[field.dataset.field] = value;
        }
        // The pending rows of the form's tabular blocks go with the record, so the server saves them together.
        const details = [];
        if (!remove) for (const detail of formDetails.get(form) ?? []) {
            const pending = detail.pending();
            // A row's own check failed, and the browser has shown which.
            if (pending === false) return;
            if (pending) details.push(pending);
        }
        busy = true;
        // Deleting needs no check. A service that shows no widget hands over its token only now.
        if (!remove) for (const widget of antiSpam) {
            const problem = await widget.prepare();
            if (problem) { showErrors({ "": [problem] }); busy = false; return; }
        }
        // Read before the form is disabled below, which would take every control with it.
        const formData = otherControls(form);
        body.disabled = true; showErrors(null); message.textContent = t(remove ? "deleting" : "saving");
        try {
            let result;
            try {
                result = await request(requestUrl(state.record.key), {
                    method: remove ? "DELETE" : state.record.key ? "PUT" : "POST",
                    headers: { RequestVerificationToken: state.antiforgeryToken },
                    body: { values, definitionVersion: state.definitionVersion, checksum: state.record.checksum, submissionKey, formData, ...(details.length ? { details } : {}) },
                });
            } catch (error) {
                if (!(error instanceof ApiError)) throw error;
                // Someone anonymous refused, or a session that ended, needs to sign in; what they entered stays in the form.
                if (signInNeeded(error, anonymous)) { message.replaceChildren(signInNotice(t("signInForm"))); return; }
                refusal(error); return;
            }
            // No record in the answer, whether it has no content or a body without one: the record is gone, or a public
            // form keeps nothing to come back to, so the visitor goes back.
            if (!result?.record) { location.assign(form.dataset.return); return; }
            keep(result.record); message.textContent = t("saved");
            // Blocks that saved with the record take their rows from the answer; the others reload.
            form.dispatchEvent(new CustomEvent("app-form:saved", { bubbles: true, detail: { record: result.record, definitionVersion: state.definitionVersion, details: result.details ?? [] } }));
        } catch { showErrors({ "": [t("requestFailedRetained")] }); message.textContent = ""; }
        finally {
            busy = false; body.disabled = false;
            // Whatever the answer was, the server has seen this token and it will not work again.
            if (!remove) for (const widget of antiSpam) widget.reset();
        }
    }
    // A refused save. Some refusals have no body, such as an expired antiforgery token.
    function refusal(error) {
        const { status, problem: result } = error;
        message.textContent = "";
        // A block's rows were refused: the block marks the row, and every value stays. When the record could not share their
        // transaction it was saved before them, so the form now shows it and its next save is an update.
        if (result.block) {
            if (result.record) keep(result.record);
            for (const detail of formDetails.get(form) ?? []) {
                if (result.record?.key) detail.keyed(result.record);
                // A block saved before the one that failed shows its rows as saved, so they are not sent again.
                const list = result.details?.find(saved => saved.block === detail.block)?.list;
                if (list) detail.saved(list);
                if (detail.block === result.block) detail.refused(result);
            }
            showErrors({ "": [t(result.record ? "rowsRefusedRecordSaved" : "rowsRefusedNothingSaved")] });
            return;
        }
        // These statuses have words of their own; anything else shows the server's own, such as a problem's detail.
        showErrors(result.errors ?? { "": [status === 403 ? t("operationForbidden") : status === 413 ? t("tooLarge") : status === 429 ? t("tooManyRequests") : errorText(error, t("operationFailed"))] });
    }
    // A saved record: shown, a new key for the next submission, and its key on the address.
    function keep(record) {
        display(record); submissionKey = newSubmissionKey();
        // A public form may keep nothing to come back to, so a record with no key leaves the address alone.
        if (record.key) { const url = new URL(location.href); url.searchParams.set(form.dataset.keyParameter, record.key); history.replaceState(null, "", url); }
    }
    form.addEventListener("submit", event => { event.preventDefault(); submit(); });
    form.querySelectorAll("[data-delete]").forEach(button => button.addEventListener("click", () => submit(true)));
    load();
}
// A record's files, served by the block's attachments endpoint. Inside a form the block follows the form's record through
// the app-form:loaded and app-form:saved events and asks with its key; on its own it asks at once with the page's
// address. Writes are multipart requests carrying the antiforgery token the list handed out. A form's new record has no
// key yet. When the server allows it for this caller (data-new-uploads="allowed"), its files go up ahead under the form's
// submission key and join the record once the form has saved it; otherwise the block asks for the record to be saved first.
function mountAttachments(root) {
    const api = root.dataset.api;
    const form = root.dataset.owner === "form" ? root.closest("[data-app-form]") : null;
    const keyParameter = root.dataset.keyParameter || "id";
    const readOnly = root.dataset.readOnly === "true";
    const anonymous = root.dataset.anonymous === "true";
    const newUploads = !readOnly && root.dataset.newUploads === "allowed";
    const maxSize = Number(root.dataset.maxSize) || 0;
    const list = root.querySelector(".app-attachments-list");
    const message = root.querySelector(".app-attachments-message");
    const errors = root.querySelector(".app-attachments-errors");
    const upload = root.querySelector(".app-attachments-upload");
    let recordKey = null;
    // The submission key a new record's files went up under, and whether any did, until they join the saved record.
    let pendingKey = null;
    let pendingFiles = false;
    let state = null;
    let busy = false;
    // Whether the files shown are a new record's, sent ahead of its save.
    const pending = () => !!form && !recordKey && newUploads;
    const requestUrl = (id = null) => {
        const below = id === null ? [] : [id];
        if (pending()) return endpointUrl(api, ["new", pendingKey, ...below]);
        return endpointUrl(api, below, form && recordKey ? { [keyParameter]: recordKey } : {});
    };
    function fail(text) { errors.textContent = text; errors.hidden = false; }
    function clear() { errors.textContent = ""; errors.hidden = true; }
    function pick(onFile) {
        const input = document.createElement("input");
        input.type = "file";
        if (root.dataset.accept) input.accept = root.dataset.accept;
        input.addEventListener("change", () => { if (input.files[0]) onFile(input.files[0]); });
        input.click();
    }
    function render() {
        list.replaceChildren(); list.setAttribute("aria-busy", "false");
        if (form && !recordKey && !newUploads) { message.textContent = t("saveRecordBeforeFiles"); if (upload) upload.hidden = true; return; }
        if (!state) return;
        // No file storage is not an error of this page: a plain notice, and for an administrator the way to the setup screen.
        if (state.storage) {
            if (upload) upload.hidden = true;
            message.textContent = state.storage.message;
            if (state.storage.setupUrl) {
                const setup = document.createElement("a");
                setup.href = state.storage.setupUrl; setup.textContent = t("openDatabaseSetup");
                message.append(" ", setup);
            }
            return;
        }
        const canWrite = state.canWrite && !readOnly;
        // A file sent ahead of its record's save can only be removed: the endpoint for new files neither serves nor replaces one.
        const ahead = pending();
        if (upload) upload.hidden = !canWrite;
        message.textContent = state.items.length ? "" : t("noFiles");
        for (const item of state.items) {
            const row = document.createElement("li");
            row.className = "app-attachment"; row.dataset.id = item.id;
            const name = document.createElement(ahead ? "span" : "a");
            name.className = "app-attachment-name"; name.textContent = item.fileName;
            if (!ahead) name.href = requestUrl(item.id).toString();
            const meta = document.createElement("span");
            meta.className = "app-attachment-meta";
            meta.textContent = [item.purpose, item.label, sizeText(item.size), `${item.uploadedBy}, ${dateTimeText(item.uploadedAt)}`].filter(Boolean).join(" · ");
            row.append(name, meta);
            if (item.notes) { const notes = document.createElement("p"); notes.className = "app-attachment-notes"; notes.textContent = item.notes; row.append(notes); }
            if (item.revisions?.length) {
                const revisions = document.createElement("details");
                revisions.className = "app-attachment-revisions";
                const summary = document.createElement("summary");
                summary.textContent = item.revisions.length === 1 ? t("earlierRevision") : t("earlierRevisions", { count: item.revisions.length });
                const older = document.createElement("ul");
                for (const revision of item.revisions) {
                    const entry = document.createElement("li");
                    const link = document.createElement("a");
                    link.href = requestUrl(revision.id).toString(); link.textContent = revision.fileName;
                    entry.append(link, ` (${sizeText(revision.size)}, ${revision.uploadedBy}, ${dateTimeText(revision.uploadedAt)})`);
                    older.append(entry);
                }
                revisions.append(summary, older); row.append(revisions);
            }
            if (canWrite) {
                const actions = document.createElement("span");
                actions.className = "app-attachment-actions";
                if (!ahead) actions.append(actionButton(t("replaceFile"), () => pick(file => send(item.id, { file }))));
                actions.append(actionButton(t("removeFile"), () => { if (window.confirm(t("confirmRemoveFile", { name: item.fileName }))) send(item.id, null, "DELETE"); }));
                row.append(actions);
            }
            list.append(row);
        }
    }
    async function load() {
        clear();
        if (form && !recordKey && !newUploads) { state = null; render(); return; }
        // The form's key when the first of a new record's files is listed; the form takes a new one after each save, and
        // the files stay under this one until they join the record.
        if (pending()) pendingKey ??= formSubmissions.get(form)?.() ?? newSubmissionKey();
        list.setAttribute("aria-busy", "true");
        try {
            state = await request(requestUrl()); render();
            if (pending()) pendingFiles = state.items?.length > 0;
        } catch (error) {
            if (signInNeeded(error, anonymous)) { root.replaceWith(signInNotice(t("signInFiles"))); return; }
            state = null; render(); fail(errorText(error, t("filesLoadFailed")));
        }
    }
    // The files sent ahead join the record the form has just saved, under the token their list handed out. Returns what to
    // tell the reader when they could not; the files then stay where they are, and the form's next save tries again.
    async function adopt(key) {
        try {
            await request(endpointUrl(api, ["new", pendingKey, "adopt"]), {
                method: "POST", headers: { RequestVerificationToken: state?.antiforgeryToken }, body: { key },
            });
            pendingKey = null; pendingFiles = false;
            return null;
        } catch (error) { return errorText(error, t("filesNotAttached")); }
    }
    // An upload (no id), a replacement or new details for a file (an id), or a removal (DELETE).
    async function send(id, parts, method = null) {
        if (busy || !state) return;
        clear();
        const body = new FormData();
        if (parts?.file) {
            if (maxSize && parts.file.size > maxSize) { fail(t("fileTooLarge", { size: numberText(Math.round(maxSize / 1048576)) })); return; }
            body.append("file", parts.file, parts.file.name);
        }
        for (const field of ["purpose", "label", "notes"]) if (parts && parts[field] !== undefined) body.append(field, parts[field]);
        busy = true; message.textContent = t(method === "DELETE" ? "removing" : "uploading");
        try {
            try {
                await request(requestUrl(id), {
                    method: method ?? (id === null ? "POST" : "PUT"),
                    headers: { RequestVerificationToken: state.antiforgeryToken }, body: method === "DELETE" ? undefined : body,
                });
            } catch (error) {
                if (!(error instanceof ApiError)) throw error;
                fail(signInNeeded(error, anonymous) ? t("signInThenRetry") : errorText(error, t("fileOperationFailed"))); message.textContent = ""; return;
            }
            if (upload) for (const field of upload.querySelectorAll("input, select")) field.value = "";
            await load();
            root.dispatchEvent(new CustomEvent("app-attachments:changed", { bubbles: true, detail: { id, operation: method === "DELETE" ? "remove" : id === null ? "upload" : "replace" } }));
        } catch { fail(t("requestFailed")); message.textContent = ""; }
        finally { busy = false; }
    }
    upload?.querySelector("[data-attachment-upload]")?.addEventListener("click", () => {
        const file = upload.querySelector("[data-attachment-file]").files[0];
        if (!file) { fail(t("chooseFile")); return; }
        send(null, { file, purpose: upload.querySelector("[data-attachment-purpose]")?.value ?? "",
            label: upload.querySelector("[data-attachment-label]").value, notes: upload.querySelector("[data-attachment-notes]").value });
    });
    if (form) {
        const follow = async event => {
            const key = event.detail?.record?.key ?? null;
            // A new record's files join it once it has a key, before the block lists the record's own.
            const problem = key && pendingKey && pendingFiles ? await adopt(key) : null;
            if (key && !pendingFiles) pendingKey = null;
            recordKey = key;
            await load();
            if (problem) fail(problem);
        };
        form.addEventListener("app-form:loaded", follow);
        form.addEventListener("app-form:saved", follow);
        render();
    } else load();
}
// The rows of a record, served by the block's tabular endpoint: one table row per row, one input per cell, saved as one batch that
// the server applies whole or not at all. Inside a form the block follows the form's record as Attachments do. Every
// control carries form="" and its inputs data-column, never data-field, so the enclosing form neither collects,
// validates nor submits them. Inside a form whose record the caller can save, the rows go with the form's own save
// instead, as the form's pending details, and the block shows no save control: a new record can take rows too, which
// the server links to it once it has a key. On its own, or when the form cannot be saved, the block saves itself.
const tabularInputTypes = { number: "number", date: "date", time: "time", datetime: "datetime-local", checkbox: "checkbox" };
function mountTabular(root) {
    const form = root.dataset.owner === "form" ? root.closest("[data-app-form]") : null;
    const keyParameter = root.dataset.keyParameter || "id";
    const readOnly = root.dataset.readOnly === "true";
    const anonymous = root.dataset.anonymous === "true";
    const emptyText = root.dataset.emptyMessage || t("noRows");
    const headRow = root.querySelector(".app-tabular-table thead tr");
    const tbody = root.querySelector(".app-tabular-table tbody");
    const message = root.querySelector(".app-tabular-message");
    const errors = root.querySelector(".app-tabular-errors");
    const toolbar = root.querySelector(".app-tabular-toolbar");
    const addButton = toolbar?.querySelector("[data-tabular-add]");
    const saveButton = toolbar?.querySelector("[data-tabular-save]");
    const block = root.dataset.block;
    // Whether the rows go with the form's save.
    let withForm = false;
    // The changes the form last sent, so a refusal can name their rows.
    let lastSent = null;
    let recordKey = null;
    let state = null;
    let busy = false;
    // One key per attempt: a retry after a lost answer reuses it, so the server answers without writing twice.
    let submissionKey = newSubmissionKey();
    // What the table shows: each row's record, its cells' inputs and what they held when drawn.
    let rows = [];
    // A form's record not saved yet lists from /new: no rows, and what a new row starts from.
    const requestUrl = (fresh = false) => endpointUrl(root.dataset.api, fresh ? ["new"] : [], form && recordKey ? { [keyParameter]: recordKey } : {});
    const fieldOf = key => state?.fields.find(field => field.key === key);
    const maxRows = () => Number(state?.maxRows ?? root.dataset.maxRows) || 0;
    // The value an input holds, as the form runtime sends it: a number's text, ISO date-times, booleans, null for empty.
    function read(input) {
        if (input.type === "checkbox") return input.checked;
        if (input.value === "") return null;
        return input.type === "datetime-local" ? fromLocalInput(input.value) : input.value;
    }
    function write(input, value) {
        if (input.type === "checkbox") input.checked = truthy(value);
        else if (input.type === "datetime-local" && !blank(value)) input.value = toLocalInput(value);
        else input.value = value ?? "";
    }
    // A cell the caller may not change shows its value as text.
    function text(field, value) {
        if (blank(value)) return "";
        if (field.kind === "checkbox") return t(truthy(value) ? "yes" : "no");
        if (field.kind === "datetime") return dateTimeText(value);
        return field.choices?.find(choice => choice.value === String(value))?.label ?? String(value);
    }
    function input(field, value) {
        let control;
        if (field.choices?.length) {
            control = document.createElement("select");
            control.append(new Option("", ""));
            for (const choice of field.choices) control.append(new Option(choice.label, choice.value));
            const retired = retiredOption(field.choices, value);
            if (retired) control.append(retired);
        } else {
            control = document.createElement("input");
            control.type = tabularInputTypes[field.kind] ?? "text";
            if (control.type === "number") {
                control.step = "any";
                if (field.minimum !== null && field.minimum !== undefined) control.min = String(field.minimum);
                if (field.maximum !== null && field.maximum !== undefined) control.max = String(field.maximum);
            }
            if (control.type === "text" && field.maxLength) control.maxLength = field.maxLength;
        }
        control.setAttribute("form", "");
        control.dataset.column = field.key;
        control.setAttribute("aria-label", field.label);
        // A checkbox always has a value, so only the other kinds can be required.
        if (field.required && control.type !== "checkbox") { control.required = true; control.setAttribute("aria-required", "true"); }
        write(control, value);
        return control;
    }
    function head() {
        // The list's columns are the authority; the server drew the same ones unless the data source was unavailable.
        const actions = headRow.querySelector(".app-tabular-actions-head");
        headRow.replaceChildren(...state.fields.map(field => {
            const cell = document.createElement("th");
            cell.scope = "col"; cell.dataset.column = field.key;
            cell.textContent = field.required ? `${field.label} *` : field.label;
            return cell;
        }), actions ?? document.createElement("th"));
    }
    function empty() {
        tbody.querySelector(".app-tabular-empty")?.remove();
        if (rows.length) return;
        const row = document.createElement("tr");
        row.className = "app-tabular-empty";
        const cell = document.createElement("td");
        cell.colSpan = state.fields.length + 1; cell.textContent = emptyText;
        row.append(cell); tbody.append(row);
    }
    function limit() {
        if (addButton) addButton.disabled = busy || (maxRows() > 0 && rows.filter(row => !row.deleted).length >= maxRows());
    }
    // One table row; a new row starts from the list's newRow with its defaults and the link already set by the server.
    function addRow(record, isNew) {
        const row = { record, isNew, deleted: false, inputs: new Map(), initial: new Map(), element: document.createElement("tr") };
        row.element.dataset.rowKey = record.key ?? "";
        if (isNew) row.element.classList.add("app-tabular-row--new");
        const editable = !readOnly && (isNew ? state.canAdd : state.canUpdate && record.capabilities.update);
        for (const field of state.fields) {
            const cell = document.createElement("td");
            cell.dataset.column = field.key;
            const value = record.values[field.key];
            if (editable && !field.readOnly) {
                const control = input(field, value);
                row.inputs.set(field.key, control); row.initial.set(field.key, read(control));
                cell.append(control);
            } else cell.textContent = text(field, value);
            row.element.append(cell);
        }
        const actions = document.createElement("td");
        actions.className = "app-tabular-actions";
        if (!readOnly && (isNew || state.canDelete && record.capabilities.delete)) {
            const remove = actionButton(t("deleteRow"), () => {
                row.deleted = !row.deleted;
                row.element.classList.toggle("app-tabular-row--deleted", row.deleted);
                for (const control of row.inputs.values()) {
                    control.disabled = row.deleted;
                    if (row.deleted) control.setAttribute("aria-disabled", "true"); else control.removeAttribute("aria-disabled");
                }
                remove.textContent = t(row.deleted ? "undoDelete" : "deleteRow");
                limit();
            });
            actions.append(remove);
        }
        row.element.append(actions);
        rows.push(row); tbody.append(row.element);
        return row;
    }
    function render() {
        tbody.replaceChildren(); tbody.setAttribute("aria-busy", "false"); rows = [];
        if (toolbar) toolbar.hidden = true;
        if (form && !recordKey && !withForm) { state = null; message.textContent = t("saveRecordBeforeRows"); return; }
        if (!state) return;
        message.textContent = "";
        head();
        for (const record of state.rows) addRow(record, false);
        empty();
        if (toolbar) toolbar.hidden = readOnly || !(state.canAdd || state.canUpdate || state.canDelete);
        if (addButton) addButton.hidden = !state.canAdd;
        if (saveButton) saveButton.hidden = withForm;
        limit();
    }
    function clear() {
        errors.replaceChildren(); errors.hidden = true;
        for (const row of rows) {
            row.element.classList.remove("app-tabular-row--invalid");
            for (const control of row.inputs.values()) control.removeAttribute("aria-invalid");
        }
    }
    function fail(lines, reload = false) {
        const list = document.createElement("ul");
        for (const line of lines) { const item = document.createElement("li"); item.textContent = line; list.append(item); }
        errors.replaceChildren(list);
        if (reload) {
            const link = document.createElement("a");
            link.href = "#"; link.textContent = t("reloadRows");
            link.addEventListener("click", event => { event.preventDefault(); load(); });
            errors.append(link);
        }
        errors.hidden = false; errors.focus();
    }
    function setBusy(value) {
        busy = value;
        tbody.setAttribute("aria-busy", String(value));
        if (saveButton) saveButton.disabled = value;
        limit();
    }
    async function load() {
        clear();
        if (form && !recordKey && !withForm) { render(); return; }
        tbody.setAttribute("aria-busy", "true");
        // A refusal is a problem response, or the engine's errors when the data source itself refused, such as too many rows.
        try { state = await request(requestUrl(form && !recordKey)); submissionKey = newSubmissionKey(); render(); }
        catch (error) {
            if (signInNeeded(error, anonymous)) { root.replaceWith(signInNotice(t("signInRows"))); return; }
            state = null; render(); fail([errorText(error, t("rowsLoadFailed"))]);
        }
    }
    // The batch: every new row whole, the changed cells of every other row, and every row marked for deletion.
    function changes() {
        const sent = [];
        for (const row of rows) {
            if (row.isNew) {
                // A new row marked for deletion was never saved, so it is simply left out.
                if (!row.deleted) sent.push([row, { operation: "create", values: Object.fromEntries([...row.inputs].map(([key, control]) => [key, read(control)])) }]);
            } else if (row.deleted) sent.push([row, { operation: "delete", key: row.record.key, checksum: row.record.checksum }]);
            else {
                const values = Object.fromEntries([...row.inputs].filter(([key, control]) => read(control) !== row.initial.get(key)).map(([key, control]) => [key, read(control)]));
                if (Object.keys(values).length) sent.push([row, { operation: "update", key: row.record.key, checksum: row.record.checksum, values }]);
            }
        }
        return sent;
    }
    // A refused batch names the change that failed; its row and the named cells are marked, and every value stays.
    function refused(result, sent) {
        const row = sent[result.index]?.[0];
        const number = row ? rows.indexOf(row) + 1 : null;
        const lines = [];
        row?.element.classList.add("app-tabular-row--invalid");
        for (const [key, entries] of Object.entries(result.errors ?? {})) {
            const control = row?.inputs.get(key);
            control?.setAttribute("aria-invalid", "true");
            const where = [number ? t("rowNumber", { number }) : null, key ? fieldOf(key)?.label ?? key : null].filter(Boolean).join(", ");
            for (const entry of entries) lines.push(where ? `${where}: ${entry}` : entry);
        }
        if (result.status === 409 || !lines.length) lines.push(t(result.status === 409 ? "rowConflict" : "rowsSaveFailed"));
        fail(lines, result.status === 409);
    }
    async function save() {
        if (!state || busy) return;
        clear();
        const sent = changes();
        if (!sent.length) { message.textContent = t("nothingToSave"); return; }
        // The browser's own checks first, one input at a time, stopping at the first that fails.
        for (const [row] of sent) if (!row.deleted) for (const control of row.inputs.values()) if (!control.reportValidity()) return;
        setBusy(true); message.textContent = t("saving");
        try {
            let result;
            try {
                result = await request(requestUrl(), {
                    method: "POST", headers: { RequestVerificationToken: state.antiforgeryToken },
                    body: { submissionKey, changes: sent.map(([, change]) => change) },
                });
            } catch (error) {
                if (!(error instanceof ApiError)) throw error;
                message.textContent = "";
                if (signInNeeded(error, anonymous)) fail([t("signInThenRetry")]);
                else if (typeof error.problem.index === "number") refused(error.problem, sent);
                else fail(problemLines(error.problem, t(error.status === 400 ? "rowsSaveFailedReload" : "rowsSaveFailed")), error.status === 409);
                return;
            }
            message.textContent = "";
            state = result; submissionKey = newSubmissionKey(); render(); message.textContent = t("saved");
            root.dispatchEvent(new CustomEvent("app-tabular:saved", { bubbles: true, detail: { rows: result.rows } }));
        } catch { message.textContent = ""; fail([t("requestFailedRetained")]); }
        finally { setBusy(false); }
    }
    addButton?.addEventListener("click", () => {
        if (!state || busy) return;
        const row = addRow(state.newRow, true);
        empty(); limit();
        row.inputs.values().next().value?.focus();
    });
    saveButton?.addEventListener("click", save);
    if (form) {
        const follow = event => {
            const record = event.detail?.record;
            recordKey = record?.key ?? null;
            withForm = !readOnly && !!(record?.key ? record.capabilities?.update : record?.capabilities?.create);
            // Rows saved with the record come back with it, so they are shown as saved rather than read again.
            const list = event.detail?.details?.find(detail => detail.block === block)?.list;
            if (list) saved(list);
            else load();
        };
        const saved = list => { clear(); state = list; submissionKey = newSubmissionKey(); lastSent = null; render(); };
        form.addEventListener("app-form:loaded", follow);
        form.addEventListener("app-form:saved", follow);
        if (!formDetails.has(form)) formDetails.set(form, []);
        formDetails.get(form).push({
            block,
            // The batch to send with the form's save: null for none, false when a row's own check failed.
            pending() {
                if (!withForm || !state || busy) return null;
                clear();
                const sent = changes();
                if (!sent.length) return null;
                for (const [row] of sent) if (!row.deleted) for (const control of row.inputs.values()) if (!control.reportValidity()) return false;
                lastSent = sent;
                return { block, revision: state.revision, submissionKey, changes: sent.map(([, change]) => change) };
            },
            refused(result) {
                if (lastSent && typeof result.index === "number") refused(result, lastSent);
                else fail(problemLines(result, t("rowsSaveFailed")), result.status === 409);
            },
            saved,
            // The record was saved without these rows: they stay as entered, and the next save links them to it.
            keyed(record) { recordKey = record.key; withForm = !readOnly && !!record.capabilities?.update; if (saveButton) saveButton.hidden = withForm; },
        });
        render();
    } else load();
}
// Attachments and tabular forms listen for their form's record, so they mount before the forms start loading.
document.querySelectorAll("[data-app-attachments]").forEach(mountAttachments);
document.querySelectorAll("[data-app-tabular]").forEach(mountTabular);
document.querySelectorAll("[data-app-schedule]").forEach(mountSchedule);
document.querySelectorAll("[data-app-form]").forEach(mountForm);
