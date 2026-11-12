import { ApiError, errorText, request } from "./data-api.js";
import { t } from "./runtime-text.js";
import { blank, truthy, newSubmissionKey, signInNotice, signInNeeded, endpointUrl, toLocalInput, fromLocalInput, plain, recordAddress, retiredOption, otherControls } from "./runtime-controls.js";
import { mountAntiSpam } from "./anti-spam.js";
import { mountShuttle, mountColor } from "./field-controls.js";
import { detailsFor, attachmentsFor, setSubmissionKey } from "./form-coordination.js";

export function mountForm(form) {
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
    setSubmissionKey(form, () => submissionKey);
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
        // A Display value shown as a link: its text is the field's value, and it leads somewhere only when there is a value
        // and the address built from the record is one a page may link to.
        for (const link of form.querySelectorAll("[data-display-link]")) {
            const shown = plain(fields.find(field => field.dataset.field === link.dataset.displayLink)?.value);
            const address = shown ? recordAddress(link.dataset.linkTemplate, { ...record.values, [link.dataset.displayLink]: shown }) : null;
            link.textContent = shown;
            if (address) link.setAttribute("href", address); else link.removeAttribute("href");
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
        const attachments = attachmentsFor(form);
        // A save waits for every queued file to be uploaded or removed. A deletion waits only for operations under way: the
        // files still queued would have belonged to the record it deletes.
        const files = attachments.map(block => block.standing());
        if (files.includes("working")) { window.alert(t("filesWorking")); return; }
        if (!remove && files.includes("queued")) { window.alert(t("filesBeforeSave")); return; }
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
        if (!remove) for (const detail of detailsFor(form)) {
            const pending = detail.pending();
            // A row's own check failed, and the browser has shown which.
            if (pending === false) return;
            if (pending) details.push(pending);
        }
        busy = true;
        for (const block of attachments) block.saving(true);
        // Deleting needs no check. A service that shows no widget hands over its token only now.
        if (!remove) for (const widget of antiSpam) {
            const problem = await widget.prepare();
            if (problem) { showErrors({ "": [problem] }); busy = false; for (const block of attachments) block.saving(false); return; }
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
            for (const block of attachments) block.saving(false);
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
            for (const detail of detailsFor(form)) {
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
// Opening an Attachments block's Upload dialog fetches the locally packaged Uppy modules and stylesheet once.
