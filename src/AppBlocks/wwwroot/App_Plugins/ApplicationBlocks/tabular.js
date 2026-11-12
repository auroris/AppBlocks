import { ApiError, errorText, request } from "./data-api.js";
import { t, dateTimeText } from "./runtime-text.js";
import { blank, truthy, newSubmissionKey, signInNotice, signInNeeded, problemLines, endpointUrl, actionButton, toLocalInput, fromLocalInput, retiredOption } from "./runtime-controls.js";
import { registerDetails } from "./form-coordination.js";

// The rows of a record, served by the block's tabular endpoint: one table row per row, one input per cell, saved as one batch that
// the server applies whole or not at all. Inside a form the block follows the form's record as Attachments do. Every
// control carries form="" and its inputs data-column, never data-field, so the enclosing form neither collects,
// validates nor submits them. Inside a form whose record the caller can save, the rows go with the form's own save
// instead, as the form's pending details, and the block shows no save control: a new record can take rows too, which
// the server links to it once it has a key. On its own, or when the form cannot be saved, the block saves itself.
const tabularInputTypes = { number: "number", date: "date", time: "time", datetime: "datetime-local", checkbox: "checkbox" };
export function mountTabular(root) {
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
        for (const row of rows) {
            for (const control of row.inputs.values()) control.disabled = value || row.deleted;
            for (const button of row.element.querySelectorAll("button")) button.disabled = value;
        }
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
        registerDetails(form, {
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
