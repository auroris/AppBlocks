const pathBase = new URL(import.meta.url).pathname.split("/App_Plugins/")[0];

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
    const unavailable = "The anti-spam check could not load. Check your connection or anything blocking it, then reload the page.";
    return {
        reset,
        // Returns what to tell the visitor when the form is not ready to send, or nothing when it is.
        async prepare() {
            if (!action) return hasToken() ? null : blocked ? unavailable : "Complete the anti-spam check, then try again.";
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

function mountForm(form) {
    const fields = [...form.querySelectorAll("[data-field]")];
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
    const requestUrl = (key = null) => {
        const url = new URL(form.dataset.api, location.origin);
        if (key) url.pathname += `/${encodeURIComponent(key)}`;
        const internalQuery = new URLSearchParams(url.search);
        url.search = location.search;
        for (const [name, value] of internalQuery) url.searchParams.set(name, value);
        return url;
    };
    function showErrors(values) {
        errors.replaceChildren(); errors.hidden = !values || !Object.keys(values).length;
        for (const field of fields) { field.removeAttribute("aria-invalid"); const error = errorOf(field); if (error) error.textContent = ""; }
        if (errors.hidden) return;
        const list = document.createElement("ul");
        for (const [key, entries] of Object.entries(values)) {
            const field = fields.find(input => input.dataset.field === key);
            if (field) { field.setAttribute("aria-invalid", "true"); const error = errorOf(field); if (error) error.textContent = entries.join(" "); }
            for (const text of entries) {
                const item = document.createElement("li");
                // A hidden field cannot take focus, so its message names the field instead of linking to it.
                if (field?.type === "hidden") item.textContent = `${field.dataset.label ?? key}: ${text}`;
                else if (field) { const link = document.createElement("a"); link.href = `#${field.id}`; link.textContent = text; item.append(link); }
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
                // Retired choices must remain visible and unchanged when another field is saved.
                if (record.key && !blank(value) && ![...field.options].some(option => option.value === String(value))) {
                    const option = document.createElement("option");
                    option.value = String(value); option.textContent = `${value} (current value)`;
                    option.dataset.storedValue = ""; field.append(option);
                }
            }
            if (field.type === "checkbox") field.checked = truthy(value);
            else if (field.type === "datetime-local" && value) {
                const date = new Date(value);
                field.value = new Date(date.getTime() - date.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
            } else field.value = value ?? "";
            field.disabled = field.dataset.readonly === "true" || !(record.key ? record.capabilities.update : record.capabilities.create);
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
        message.textContent = "Loading…";
        try {
            const url = requestUrl();
            const response = await fetch(url, { credentials: "same-origin", cache: "no-store" });
            // The page normally knows this already; a class can still change its mind between rendering and this load.
            if (response.status === 403 && anonymous) { form.replaceWith(signInNotice("Sign in to use this form.")); return; }
            if (!response.ok) throw new Error(response.status === 403 ? "You do not have access to this form." : response.status === 404 ? "The record was not found." : "The form could not be loaded.");
            state = await response.json(); display(state.record); message.textContent = "";
            // Blocks that follow the record, such as Attachments, learn which record is shown.
            form.dispatchEvent(new CustomEvent("app-form:loaded", { bubbles: true, detail: { record: state.record, definitionVersion: state.definitionVersion } }));
        } catch (error) { showErrors({ "": [error.message] }); message.textContent = ""; }
    }
    async function submit(remove = false) {
        if (!state || busy) return;
        if (!remove && !form.reportValidity()) return;
        if (remove && !window.confirm("Delete this record?")) return;
        const values = {};
        if (!remove) for (const field of fields.filter(input => !input.disabled && input.dataset.readonly !== "true")) {
            let value = field.type === "checkbox" ? field.checked : field.value;
            // A hidden field is submitted like any other; one bound to a checkbox sends true or false, as a checkbox would.
            if (field.type === "hidden" && field.dataset.kind === "checkbox") value = value === "" ? null : truthy(value);
            // Preserve decimal precision; the resource binds this invariant string to decimal.
            else if (field.type === "number") value = value === "" ? null : value;
            else if (field.type === "datetime-local") value = value === "" ? null : new Date(value).toISOString();
            else if (value === "") value = null;
            values[field.dataset.field] = value;
        }
        busy = true;
        // Deleting needs no check. A service that shows no widget hands over its token only now.
        if (!remove) for (const widget of antiSpam) {
            const problem = await widget.prepare();
            if (problem) { showErrors({ "": [problem] }); busy = false; return; }
        }
        // Read before the form is disabled below, which would take every control with it.
        const formData = otherControls(form);
        body.disabled = true; showErrors(null); message.textContent = remove ? "Deleting…" : "Saving…";
        try {
            const response = await fetch(requestUrl(state.record.key), {
                method: remove ? "DELETE" : state.record.key ? "PUT" : "POST", credentials: "same-origin",
                headers: { "Content-Type": "application/json", RequestVerificationToken: state.antiforgeryToken },
                body: JSON.stringify({ values, definitionVersion: state.definitionVersion, version: state.record.version, submissionKey, formData }),
            });
            if (response.status === 204) { location.assign(form.dataset.return); return; }
            // A class refusing someone anonymous means they need to sign in; what they entered stays in the form.
            if (response.status === 403 && anonymous) { message.replaceChildren(signInNotice("Sign in to use this form.")); return; }
            // Some refusals have no body, such as an expired antiforgery token.
            const result = await response.json().catch(() => ({}));
            if (!response.ok) { showErrors(result.errors ?? { "": [response.status === 403 ? "This operation is not permitted." : response.status === 413 ? "This is too much to send at once. Shorten what you entered and try again." : response.status === 429 ? "That was too many tries. Wait a minute and try again." : "The operation failed. Reload the page if the record has changed."] }); message.textContent = ""; return; }
            display(result.record); submissionKey = newSubmissionKey(); message.textContent = "Saved.";
            // A public form may keep nothing to come back to, so a record with no key leaves the address alone.
            if (result.record.key) { const url = new URL(location.href); url.searchParams.set(form.dataset.keyParameter, result.record.key); history.replaceState(null, "", url); }
            form.dispatchEvent(new CustomEvent("app-form:saved", { bubbles: true, detail: { record: result.record, definitionVersion: state.definitionVersion } }));
        } catch { showErrors({ "": ["The request could not be completed. Your entered values have been retained."] }); message.textContent = ""; }
        finally {
            busy = false; body.disabled = false;
            // Whatever the answer was, the server has seen this token and it will not work again.
            if (!remove) for (const widget of antiSpam) widget.reset();
        }
    }
    form.addEventListener("submit", event => { event.preventDefault(); submit(); });
    form.querySelectorAll("[data-delete]").forEach(button => button.addEventListener("click", () => submit(true)));
    load();
}
// A record's files, served by /api/attachments. Inside a form the block follows the form's record through the
// app-form:loaded and app-form:saved events and asks with its key; on its own it asks at once with the page's
// address. Writes are multipart requests carrying the antiforgery token the list handed out.
function mountAttachments(root) {
    const api = root.dataset.api;
    const form = root.dataset.owner === "form" ? root.closest("[data-app-form]") : null;
    const keyParameter = root.dataset.keyParameter || "id";
    const readOnly = root.dataset.readOnly === "true";
    const maxSize = Number(root.dataset.maxSize) || 0;
    const list = root.querySelector(".app-attachments-list");
    const message = root.querySelector(".app-attachments-message");
    const errors = root.querySelector(".app-attachments-errors");
    const upload = root.querySelector(".app-attachments-upload");
    let recordKey = null;
    let state = null;
    let busy = false;
    const requestUrl = (id = null) => {
        const url = new URL(api, location.origin);
        if (id !== null) url.pathname += `/${encodeURIComponent(id)}`;
        const internalQuery = new URLSearchParams(url.search);
        url.search = location.search;
        for (const [name, value] of internalQuery) url.searchParams.set(name, value);
        if (form && recordKey) url.searchParams.set(keyParameter, recordKey);
        return url;
    };
    const size = bytes => bytes < 1024 ? `${bytes} B` : bytes < 1048576 ? `${Math.round(bytes / 1024)} KB` : `${(bytes / 1048576).toFixed(1)} MB`;
    const when = value => new Date(value).toLocaleString();
    function fail(text) { errors.textContent = text; errors.hidden = false; }
    function clear() { errors.textContent = ""; errors.hidden = true; }
    function control(label, onClick) {
        const button = document.createElement("button");
        button.type = "button"; button.setAttribute("form", ""); button.textContent = label;
        button.addEventListener("click", onClick);
        return button;
    }
    function pick(onFile) {
        const input = document.createElement("input");
        input.type = "file";
        if (root.dataset.accept) input.accept = root.dataset.accept;
        input.addEventListener("change", () => { if (input.files[0]) onFile(input.files[0]); });
        input.click();
    }
    function render() {
        list.replaceChildren(); list.setAttribute("aria-busy", "false");
        if (form && !recordKey) { message.textContent = "Save the record before adding files."; if (upload) upload.hidden = true; return; }
        if (!state) return;
        // No file storage is not an error of this page: a plain notice, and for an administrator the way to the setup screen.
        if (state.storage) {
            if (upload) upload.hidden = true;
            message.textContent = state.storage.message;
            if (state.storage.setupUrl) {
                const setup = document.createElement("a");
                setup.href = state.storage.setupUrl; setup.textContent = "Open database setup";
                message.append(" ", setup);
            }
            return;
        }
        const canWrite = state.canWrite && !readOnly;
        if (upload) upload.hidden = !canWrite;
        message.textContent = state.items.length ? "" : "No files yet.";
        for (const item of state.items) {
            const row = document.createElement("li");
            row.className = "app-attachment"; row.dataset.id = item.id;
            const name = document.createElement("a");
            name.className = "app-attachment-name"; name.href = requestUrl(item.id).toString(); name.textContent = item.fileName;
            const meta = document.createElement("span");
            meta.className = "app-attachment-meta";
            meta.textContent = [item.purpose, item.label, size(item.size), `${item.uploadedBy}, ${when(item.uploadedAt)}`].filter(Boolean).join(" · ");
            row.append(name, meta);
            if (item.notes) { const notes = document.createElement("p"); notes.className = "app-attachment-notes"; notes.textContent = item.notes; row.append(notes); }
            if (item.revisions.length) {
                const revisions = document.createElement("details");
                revisions.className = "app-attachment-revisions";
                const summary = document.createElement("summary");
                summary.textContent = `${item.revisions.length} earlier revision${item.revisions.length === 1 ? "" : "s"}`;
                const older = document.createElement("ul");
                for (const revision of item.revisions) {
                    const entry = document.createElement("li");
                    const link = document.createElement("a");
                    link.href = requestUrl(revision.id).toString(); link.textContent = revision.fileName;
                    entry.append(link, ` (${size(revision.size)}, ${revision.uploadedBy}, ${when(revision.uploadedAt)})`);
                    older.append(entry);
                }
                revisions.append(summary, older); row.append(revisions);
            }
            if (canWrite) {
                const actions = document.createElement("span");
                actions.className = "app-attachment-actions";
                actions.append(control("Replace", () => pick(file => send(item.id, { file }))),
                    control("Remove", () => { if (window.confirm(`Remove ${item.fileName}?`)) send(item.id, null, "DELETE"); }));
                row.append(actions);
            }
            list.append(row);
        }
    }
    async function problem(response, fallback) {
        try { const body = await response.json(); return body.detail || body.title || fallback; } catch { return fallback; }
    }
    async function load() {
        clear();
        if (form && !recordKey) { state = null; render(); return; }
        list.setAttribute("aria-busy", "true");
        try {
            const response = await fetch(requestUrl(), { credentials: "same-origin", cache: "no-store" });
            if (response.status === 403 && root.dataset.anonymous === "true") { root.replaceWith(signInNotice("Sign in to see the files.")); return; }
            if (!response.ok) { state = null; render(); fail(await problem(response, "The files could not be loaded.")); return; }
            state = await response.json(); render();
        } catch { state = null; render(); fail("The files could not be loaded."); }
    }
    // An upload (no id), a replacement or new details for a file (an id), or a removal (DELETE).
    async function send(id, parts, method = null) {
        if (busy || !state) return;
        clear();
        const body = new FormData();
        if (parts?.file) {
            if (maxSize && parts.file.size > maxSize) { fail(`The file is larger than the ${Math.round(maxSize / 1048576)} MB this block accepts.`); return; }
            body.append("file", parts.file, parts.file.name);
        }
        for (const field of ["purpose", "label", "notes"]) if (parts && parts[field] !== undefined) body.append(field, parts[field]);
        busy = true; message.textContent = method === "DELETE" ? "Removing…" : "Uploading…";
        try {
            const response = await fetch(requestUrl(id), {
                method: method ?? (id === null ? "POST" : "PUT"), credentials: "same-origin",
                headers: { RequestVerificationToken: state.antiforgeryToken }, body: method === "DELETE" ? undefined : body,
            });
            if (!response.ok) { fail(await problem(response, "The operation failed.")); message.textContent = ""; return; }
            if (upload) for (const field of upload.querySelectorAll("input, select")) field.value = "";
            await load();
            root.dispatchEvent(new CustomEvent("app-attachments:changed", { bubbles: true, detail: { id, operation: method === "DELETE" ? "remove" : id === null ? "upload" : "replace" } }));
        } catch { fail("The request could not be completed."); message.textContent = ""; }
        finally { busy = false; }
    }
    upload?.querySelector("[data-attachment-upload]")?.addEventListener("click", () => {
        const file = upload.querySelector("[data-attachment-file]").files[0];
        if (!file) { fail("Choose a file to upload."); return; }
        send(null, { file, purpose: upload.querySelector("[data-attachment-purpose]")?.value ?? "",
            label: upload.querySelector("[data-attachment-label]").value, notes: upload.querySelector("[data-attachment-notes]").value });
    });
    if (form) {
        const follow = event => { recordKey = event.detail?.record?.key ?? null; load(); };
        form.addEventListener("app-form:loaded", follow);
        form.addEventListener("app-form:saved", follow);
        render();
    } else load();
}
// The rows of a record, served by /api/tabular: one table row per row, one input per cell, saved as one batch that
// the server applies whole or not at all. Inside a form the block follows the form's record as Attachments do. Every
// control carries form="" and its inputs data-column, never data-field, so the enclosing form neither collects,
// validates nor submits them.
const tabularInputTypes = { number: "number", date: "date", time: "time", datetime: "datetime-local", checkbox: "checkbox" };
function mountTabular(root) {
    const form = root.dataset.owner === "form" ? root.closest("[data-app-form]") : null;
    const keyParameter = root.dataset.keyParameter || "id";
    const readOnly = root.dataset.readOnly === "true";
    const emptyText = root.dataset.emptyMessage || "No rows yet.";
    const headRow = root.querySelector(".app-tabular-table thead tr");
    const tbody = root.querySelector(".app-tabular-table tbody");
    const message = root.querySelector(".app-tabular-message");
    const errors = root.querySelector(".app-tabular-errors");
    const toolbar = root.querySelector(".app-tabular-toolbar");
    const addButton = toolbar?.querySelector("[data-tabular-add]");
    const saveButton = toolbar?.querySelector("[data-tabular-save]");
    let recordKey = null;
    let state = null;
    let busy = false;
    // One key per attempt: a retry after a lost answer reuses it, so the server answers without writing twice.
    let submissionKey = newSubmissionKey();
    // What the table shows: each row's record, its cells' inputs and what they held when drawn.
    let rows = [];
    const requestUrl = () => {
        const url = new URL(root.dataset.api, location.origin);
        const internalQuery = new URLSearchParams(url.search);
        url.search = location.search;
        for (const [name, value] of internalQuery) url.searchParams.set(name, value);
        if (form && recordKey) url.searchParams.set(keyParameter, recordKey);
        return url;
    };
    const fieldOf = key => state?.fields.find(field => field.key === key);
    const maxRows = () => Number(state?.maxRows ?? root.dataset.maxRows) || 0;
    function button(label, onClick) {
        const control = document.createElement("button");
        control.type = "button"; control.setAttribute("form", ""); control.textContent = label;
        control.addEventListener("click", onClick);
        return control;
    }
    // The value an input holds, as the form runtime sends it: a number's text, ISO date-times, booleans, null for empty.
    function read(input) {
        if (input.type === "checkbox") return input.checked;
        if (input.value === "") return null;
        return input.type === "datetime-local" ? new Date(input.value).toISOString() : input.value;
    }
    function write(input, value) {
        if (input.type === "checkbox") input.checked = truthy(value);
        else if (input.type === "datetime-local" && !blank(value)) {
            const date = new Date(value);
            input.value = new Date(date.getTime() - date.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
        } else input.value = value ?? "";
    }
    // A cell the caller may not change shows its value as text.
    function text(field, value) {
        if (blank(value)) return "";
        if (field.kind === "checkbox") return truthy(value) ? "Yes" : "No";
        if (field.kind === "datetime") return new Date(value).toLocaleString();
        return field.choices?.find(choice => choice.value === String(value))?.label ?? String(value);
    }
    function input(field, value) {
        let control;
        if (field.choices?.length) {
            control = document.createElement("select");
            control.append(new Option("", ""));
            for (const choice of field.choices) control.append(new Option(choice.label, choice.value));
            // A retired choice stays visible and unchanged, as the form keeps it.
            if (!blank(value) && !field.choices.some(choice => choice.value === String(value))) control.append(new Option(`${value} (current value)`, String(value)));
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
            const remove = button("Delete", () => {
                row.deleted = !row.deleted;
                row.element.classList.toggle("app-tabular-row--deleted", row.deleted);
                for (const control of row.inputs.values()) {
                    control.disabled = row.deleted;
                    if (row.deleted) control.setAttribute("aria-disabled", "true"); else control.removeAttribute("aria-disabled");
                }
                remove.textContent = row.deleted ? "Undo" : "Delete";
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
        if (form && !recordKey) { state = null; message.textContent = "Save the record before adding rows."; return; }
        if (!state) return;
        message.textContent = "";
        head();
        for (const record of state.rows) addRow(record, false);
        empty();
        if (toolbar) toolbar.hidden = readOnly || !(state.canAdd || state.canUpdate || state.canDelete);
        if (addButton) addButton.hidden = !state.canAdd;
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
            link.href = "#"; link.textContent = "Reload rows";
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
    // A refusal is a problem response, or the engine's errors when the data source itself refused, such as too many rows.
    async function problem(response, fallback) {
        try { const body = await response.json(); return body.detail || body.title || Object.values(body.errors ?? {}).flat()[0] || fallback; } catch { return fallback; }
    }
    async function load() {
        clear();
        if (form && !recordKey) { render(); return; }
        tbody.setAttribute("aria-busy", "true");
        try {
            const response = await fetch(requestUrl(), { credentials: "same-origin", cache: "no-store" });
            if (!response.ok) { state = null; render(); fail([await problem(response, "The rows could not be loaded.")]); return; }
            state = await response.json(); submissionKey = newSubmissionKey(); render();
        } catch { state = null; render(); fail(["The rows could not be loaded."]); }
    }
    // The batch: every new row whole, the changed cells of every other row, and every row marked for deletion.
    function changes() {
        const sent = [];
        for (const row of rows) {
            if (row.isNew) {
                // A new row marked for deletion was never saved, so it is simply left out.
                if (!row.deleted) sent.push([row, { operation: "create", values: Object.fromEntries([...row.inputs].map(([key, control]) => [key, read(control)])) }]);
            } else if (row.deleted) sent.push([row, { operation: "delete", key: row.record.key, version: row.record.version }]);
            else {
                const values = Object.fromEntries([...row.inputs].filter(([key, control]) => read(control) !== row.initial.get(key)).map(([key, control]) => [key, read(control)]));
                if (Object.keys(values).length) sent.push([row, { operation: "update", key: row.record.key, version: row.record.version, values }]);
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
            const where = [number ? `Row ${number}` : null, key ? fieldOf(key)?.label ?? key : null].filter(Boolean).join(", ");
            for (const entry of entries) lines.push(where ? `${where}: ${entry}` : entry);
        }
        if (result.status === 409 || !lines.length) lines.push(result.status === 409
            ? "This row was changed elsewhere. Reload the rows to see it; the changes entered here would then be lost."
            : "The rows could not be saved.");
        fail(lines, result.status === 409);
    }
    async function save() {
        if (!state || busy) return;
        clear();
        const sent = changes();
        if (!sent.length) { message.textContent = "Nothing to save."; return; }
        // The browser's own checks first, one input at a time, stopping at the first that fails.
        for (const [row] of sent) if (!row.deleted) for (const control of row.inputs.values()) if (!control.reportValidity()) return;
        setBusy(true); message.textContent = "Saving…";
        try {
            const response = await fetch(requestUrl(), {
                method: "POST", credentials: "same-origin",
                headers: { "Content-Type": "application/json", RequestVerificationToken: state.antiforgeryToken },
                body: JSON.stringify({ submissionKey, changes: sent.map(([, change]) => change) }),
            });
            const result = await response.json().catch(() => null);
            message.textContent = "";
            if (!response.ok) {
                if (typeof result?.index === "number") refused(result, sent);
                else fail([result?.detail || result?.title || (response.status === 400 ? "The rows could not be saved. Reload the page and try again." : "The rows could not be saved.")]);
                return;
            }
            state = result; submissionKey = newSubmissionKey(); render(); message.textContent = "Saved.";
            root.dispatchEvent(new CustomEvent("app-tabular:saved", { bubbles: true, detail: { rows: result.rows } }));
        } catch { message.textContent = ""; fail(["The request could not be completed. Your entered values have been retained."]); }
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
        const follow = event => { recordKey = event.detail?.record?.key ?? null; load(); };
        form.addEventListener("app-form:loaded", follow);
        form.addEventListener("app-form:saved", follow);
        render();
    } else load();
}
// A schedule: the server laid the whole table out, so the runtime only brings today's column into view and shows
// a bar's details, which sit inside the bar for screen readers, as a tooltip on hover and focus, and places the
// popover a bar with a description opens.
let scheduleTooltips = 0;
function mountSchedule(root) {
    const scroller = root.querySelector(".app-schedule-scroll");
    const today = root.querySelector("thead .app-schedule-col--today");
    if (scroller && today) {
        // Today lands just right of the sticky label column, with two columns of context before it.
        const labels = root.querySelector(".app-schedule-corner");
        scroller.scrollLeft = Math.max(0, today.offsetLeft - (labels ? labels.offsetWidth : 0) - today.offsetWidth * 2);
    }
    const tooltip = document.createElement("div");
    tooltip.className = "app-schedule-tooltip";
    tooltip.id = `app-schedule-tooltip-${++scheduleTooltips}`;
    tooltip.setAttribute("aria-hidden", "true");
    tooltip.hidden = true;
    root.append(tooltip);
    let active = null;
    const barOf = target => target instanceof Element ? target.closest(".app-schedule-bar") : null;
    function show(bar) {
        if (!bar || bar === active) return;
        active = bar;
        tooltip.textContent = bar.textContent;
        tooltip.hidden = false;
        const bounds = bar.getBoundingClientRect(), frame = root.getBoundingClientRect();
        tooltip.style.left = `${Math.max(0, Math.min(bounds.left - frame.left, root.clientWidth - tooltip.offsetWidth))}px`;
        tooltip.style.top = `${bounds.bottom - frame.top + 4}px`;
    }
    function hide() { active = null; tooltip.hidden = true; }
    root.addEventListener("mouseover", event => show(barOf(event.target)));
    root.addEventListener("mouseout", event => { if (active && barOf(event.target) === active && barOf(event.relatedTarget) !== active) hide(); });
    root.addEventListener("focusin", event => show(barOf(event.target)));
    root.addEventListener("focusout", event => { if (active && barOf(event.target) === active) hide(); });
    root.addEventListener("keydown", event => { if (event.key === "Escape") hide(); });
    scroller?.addEventListener("scroll", hide, { passive: true });
    // A bar with a description opens the browser's own popover. The browser centres it; here it goes under its bar,
    // kept inside the viewport, and the tooltip steps aside for it.
    root.querySelectorAll(".app-schedule-popover").forEach(popover => popover.addEventListener("toggle", event => {
        if (event.newState !== "open") return;
        hide();
        const opener = root.querySelector(`[popovertarget="${CSS.escape(popover.id)}"]:not(.app-schedule-popover__close)`);
        if (!opener) return;
        const bounds = opener.getBoundingClientRect(), gap = 8;
        popover.style.margin = "0";
        popover.style.inset = "auto";
        const left = Math.max(gap, Math.min(bounds.left, window.innerWidth - popover.offsetWidth - gap));
        let top = bounds.bottom + 4;
        if (top + popover.offsetHeight > window.innerHeight - gap) top = Math.max(gap, bounds.top - popover.offsetHeight - 4);
        popover.style.left = `${left}px`;
        popover.style.top = `${top}px`;
    }));
}
// Attachments and tabular forms listen for their form's record, so they mount before the forms start loading.
document.querySelectorAll("[data-app-attachments]").forEach(mountAttachments);
document.querySelectorAll("[data-app-tabular]").forEach(mountTabular);
document.querySelectorAll("[data-app-schedule]").forEach(mountSchedule);
document.querySelectorAll("[data-app-form]").forEach(mountForm);
