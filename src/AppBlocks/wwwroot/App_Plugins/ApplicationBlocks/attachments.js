import { ApiError, errorText, request } from "./data-api.js";
import { t, dateTimeText, numberText, sizeText } from "./runtime-text.js";
import { newSubmissionKey, signInNotice, signInNeeded, endpointUrl, actionButton, plain } from "./runtime-controls.js";
import { registerAttachments, submissionKeyFor } from "./form-coordination.js";

let uppyAssets;
function loadUppy() {
    return uppyAssets ??= Promise.all([
        import("./uppy.js"),
        new Promise((resolve, reject) => {
            const link = document.createElement("link");
            link.rel = "stylesheet"; link.href = new URL("./uppy.css", import.meta.url).href;
            link.onload = resolve; link.onerror = reject; document.head.append(link);
        }),
    ]).then(([module]) => module);
}
// A record's files, served by the block's attachments endpoint. Inside a form the block follows the form's record through
// the app-form:loaded and app-form:saved events and asks with its key; on its own it asks at once with the page's
// address. Writes carry the antiforgery token the list handed out. A form's new record has no
// key yet. When the server allows it for this caller (data-new-uploads="allowed"), its files go up ahead under the form's
// submission key and join the record once the form has saved it; otherwise the block asks for the record to be saved first.
export function mountAttachments(root) {
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
    const retryButton = upload?.querySelector("[data-attachment-retry]");
    const addButton = root.querySelector("[data-attachment-add]");
    const addLabel = addButton?.querySelector("[data-attachment-add-label]");
    const addText = addLabel?.textContent;
    const progressCircle = addButton?.querySelector(".app-attachments-progress");
    const dialog = root.querySelector(".app-attachments-dialog");
    const uploadErrors = root.querySelector(".app-attachments-upload-errors");
    let recordKey = null;
    // The submission key a new record's files went up under, and whether any did, until they join the saved record.
    let pendingKey = null;
    let pendingFiles = false;
    let state = null;
    let busy = false;
    let saving = false;
    let uploader = null;
    let uploaderReady = null;
    let uploading = 0;
    // The list read again after uploads, and whether uploads that finished meanwhile need it read once more.
    let refreshing = false;
    let refreshAgain = false;
    // The file the upload notice is about, if any, and whether the block's notice is showing it.
    let problemFileId = null;
    let uploadNotice = false;
    // The current batch's successes, announced once its files are listed.
    const uploaded = [];
    // The purpose, label and notes copied onto each file when it is added.
    const details = () => upload?.querySelectorAll("[data-attachment-purpose], [data-attachment-label], [data-attachment-notes]") ?? [];
    const unfinished = () => uploader?.getFiles().some(file => !file.progress.uploadComplete) ?? false;
    const transferring = () => uploading > 0;
    const failedFile = () => uploader?.getFiles().find(file => file.error);
    function syncUploadButton() {
        const working = transferring();
        const failed = !!failedFile();
        const progress = uploader?.getState().totalProgress ?? 0;
        if (addLabel) addLabel.textContent = working ? t("uploadProgress", { progress: numberText(progress) }) : failed ? t("uploaderUploadFailed") : addText;
        if (progressCircle) {
            progressCircle.hidden = !working && !failed;
            progressCircle.dataset.state = working ? "uploading" : "failed";
            progressCircle.style.setProperty("--upload-progress", `${progress}%`);
            progressCircle.textContent = !working && failed ? "!" : "";
        }
        if (retryButton) {
            retryButton.hidden = !failed;
            retryButton.disabled = saving || busy || working;
        }
    }
    function syncBusy() {
        const locked = saving || busy;
        root.inert = locked;
        // A modal dialog escapes its ancestors' inert state. Reading the list after uploads does not lock it: someone may be
        // typing the details for their next files, and those files may go up meanwhile.
        if (dialog) dialog.inert = locked;
        if (addButton) addButton.disabled = locked;
        uploader?.getPlugin("Dashboard").setOptions({ disabled: locked });
        syncUploadButton();
        for (const button of list.querySelectorAll("button")) button.disabled = locked || refreshing || transferring();
    }
    // Whether the files shown are a new record's, sent ahead of its save.
    const pending = () => !!form && !recordKey && newUploads;
    const requestUrl = (id = null) => {
        const below = id === null ? [] : [id];
        if (pending()) return endpointUrl(api, ["new", pendingKey, ...below]);
        return endpointUrl(api, below, form && recordKey ? { [keyParameter]: recordKey } : {});
    };
    // The block's notice, beside the list, says what last went wrong with anything the block did. The upload dialog's notice
    // says only what went wrong uploading, and which file it was about when it was about one; the block's notice repeats it
    // for a closed dialog. A rename's refusal therefore never greets the next person to open Upload.
    function fail(text) { uploadNotice = false; errors.textContent = text; errors.hidden = false; }
    function clear() { uploadNotice = false; errors.textContent = ""; errors.hidden = true; }
    function failUpload(text, fileId = null) {
        problemFileId = fileId;
        if (uploadErrors) { uploadErrors.textContent = text; uploadErrors.hidden = false; }
        fail(text); uploadNotice = true;
    }
    function clearUpload() {
        problemFileId = null;
        if (uploadErrors) { uploadErrors.textContent = ""; uploadErrors.hidden = true; }
        if (uploadNotice) clear();
    }
    addButton?.addEventListener("click", async () => {
        if (saving || busy) return;
        dialog.showModal();
        await prepareUploader();
    });
    root.querySelector("[data-attachment-close]")?.addEventListener("click", () => dialog.close());
    const resetDetails = () => { if (!dialog.open && !unfinished()) for (const field of details()) field.value = ""; };
    dialog?.addEventListener("close", resetDetails);
    dialog?.addEventListener("cancel", event => { if (saving || busy) event.preventDefault(); });
    // Enter in upload metadata must not submit the enclosing record form.
    dialog?.addEventListener("keydown", event => { if (event.key === "Enter" && event.target.matches("input")) event.preventDefault(); });
    // Uppy's texts from the runtime's ids, so a site translates the uploader as it does every other text. Uppy fills in
    // %{name} itself; a text with a count has a singular id and a plural one.
    function uploaderStrings(limit) {
        const counted = (one, many, values) => ({ 0: t(one, values), 1: t(many, values) });
        return {
            dashboardTitle: t("uploaderTitle"),
            dropPasteFiles: t("uploaderDropOrBrowse", { browse: "%{browseFiles}" }),
            browseFiles: t("uploaderBrowse"),
            dropHint: t("uploaderDropHint"),
            addMore: t("uploaderAddMore"),
            addMoreFiles: t("uploaderAddMoreFiles"),
            addingMoreFiles: t("uploaderAddingMoreFiles"),
            back: t("uploaderBack"),
            xFilesSelected: counted("uploaderFileSelected", "uploaderFilesSelected", { count: "%{smart_count}" }),
            xMoreFilesAdded: counted("uploaderFileAdded", "uploaderFilesAdded", { count: "%{smart_count}" }),
            removeFile: t("uploaderRemoveFile"),
            cancel: t("uploaderCancel"),
            cancelUpload: t("uploaderCancelUpload"),
            uploading: t("uploading"),
            uploadingXFiles: counted("uploaderUploadingFile", "uploaderUploadingFiles", { count: "%{smart_count}" }),
            filesUploadedOfTotal: counted("uploaderFileProgress", "uploaderFilesProgress", { complete: "%{complete}", count: "%{smart_count}" }),
            dataUploadedOfTotal: t("uploaderDataProgress", { complete: "%{complete}", total: "%{total}" }),
            dataUploadedOfUnknown: t("uploaderDataProgressUnknown", { complete: "%{complete}" }),
            xTimeLeft: t("uploaderTimeLeft", { time: "%{time}" }),
            complete: t("uploaderComplete"),
            uploadComplete: t("uploaderUploadComplete"),
            uploadFailed: t("uploaderUploadFailed"),
            error: t("uploaderError"),
            showErrorDetails: t("uploaderErrorDetails"),
            done: t("uploaderDone"),
            // The block's own limit in its own words, as the server refuses a file over it.
            ...(limit ? { exceedsSize: t("fileTooLarge", { size: numberText(Math.round(limit / 1048576)) }) } : {}),
            youCanOnlyUploadFileTypes: t("fileTypeNotAccepted", { types: "%{types}" }),
            noDuplicates: t("uploaderDuplicate", { name: "%{fileName}" }),
            failedToUpload: t("uploaderFileFailed", { name: "%{file}" }),
            uploadStalled: t("uploaderStalled", { seconds: "%{seconds}" }),
            noInternetConnection: t("uploaderOffline"),
            connectedToInternet: t("uploaderOnline"),
        };
    }
    // What a refused or failed upload tells the reader: the server's own words for a refusal, as every other request shows them.
    function uploadFailure(xhr) {
        if (!xhr?.status) return t("requestFailed");
        let problem = {};
        try { problem = JSON.parse(xhr.responseText || "{}"); } catch { /* A proxy can answer without JSON. */ }
        if (problem === null || typeof problem !== "object") problem = {};
        const refusal = new ApiError(xhr.status, problem);
        if (signInNeeded(refusal, anonymous)) return t("signInThenRetry");
        return errorText(refusal, t(xhr.status === 429 ? "tooManyRequests" : "fileOperationFailed"));
    }
    function prepareUploader() {
        if (!upload) return Promise.resolve();
        return uploaderReady ??= loadUppy().then(({ Uppy, Dashboard, XHRUpload }) => {
            const limit = state?.maxFileSize || maxSize || null;
            uploader = new Uppy({
                id: root.id || api, autoProceed: false,
                locale: { strings: uploaderStrings(limit), pluralize: count => count === 1 ? 0 : 1 },
                restrictions: { maxFileSize: limit, allowedFileTypes: (state?.accept || root.dataset.accept)?.split(",").filter(Boolean) || null },
                // Uppy refuses a file already in the list before it asks this.
                onBeforeFileAdded(file) {
                    const allowed = state?.canAdd ?? state?.canWrite;
                    if (readOnly || !allowed || saving || busy) return false;
                    return { ...file, meta: { ...file.meta, ...Object.fromEntries(["purpose", "label", "notes"].map(name => [name, upload.querySelector(`[data-attachment-${name}]`)?.value ?? ""])) } };
                },
            }).use(Dashboard, {
                target: upload.querySelector("[data-attachment-dashboard]"), inline: true, height: 280, width: "100%",
                // Files start when added; the block offers an explicit retry for failures.
                hideUploadButton: true, hideRetryButton: true, proudlyDisplayPoweredByUppy: false, showProgressDetails: true,
            }).use(XHRUpload, {
                endpoint: () => requestUrl().toString(),
                fieldName: "file", formData: true, bundle: false, limit: 1,
                allowedMetaFields: ["purpose", "label", "notes"],
                headers: () => ({ RequestVerificationToken: state?.antiforgeryToken, Accept: "application/json" }),
                // A lost response may follow a committed insert: never silently repeat that POST.
                shouldRetry: () => false,
                // A refusal fails with the block's words for it, which Uppy then shows on the file and in its notice.
                onAfterResponse(xhr) { if (xhr.status < 200 || xhr.status > 299) throw new Error(uploadFailure(xhr)); },
            });
            uploader.on("files-added", files => sendFiles(files.map(file => file.id)));
            uploader.on("progress", () => queueMicrotask(syncUploadButton));
            uploader.on("file-removed", file => {
                // Completed-file cleanup must not dismiss a failed list refresh or another operation's error.
                if (file.id === problemFileId) {
                    const failed = failedFile();
                    if (failed) failUpload(failed.error, failed.id);
                    else clearUpload();
                }
                syncBusy(); resetDetails();
            });
            uploader.on("upload-success", () => {
                if (pending()) pendingFiles = true;
                uploaded.push({ id: null, operation: "upload" });
            });
            uploader.on("upload-error", (file, _error, response) => {
                const problem = uploadFailure(response);
                // A request that never got an answer carries Uppy's own English on the file; it takes the block's words too.
                if (file && uploader.getFile(file.id)) uploader.setFileState(file.id, { error: problem });
                failUpload(problem, file?.id); syncUploadButton();
            });
            syncBusy();
        }).catch(() => { failUpload(t("uploaderLoadFailed")); });
    }
    async function sendFiles(ids) {
        if (!ids.length) return;
        uploading++; syncBusy();
        // upload()/autoProceed also retry every failed file in Uppy 6. Start only the named files through its public
        // per-file method, so adding a new file cannot silently repeat an earlier failed request. XHRUpload queues them.
        await Promise.all(ids.map(async id => {
            try { await uploader.retryUpload(id); }
            catch (error) {
                const problem = error.message || t("fileOperationFailed");
                if (uploader.getFile(id)) uploader.setFileState(id, { error: problem });
                failUpload(problem, id);
            }
        }));
        uploading--;
        if (transferring()) { syncBusy(); return; }
        // One reading of the list at a time: uploads that finish while it is read have it read once more, so a slower
        // answer never replaces a newer one.
        if (refreshing) { refreshAgain = true; syncBusy(); return; }
        refreshing = true; syncBusy();
        try {
            do {
                refreshAgain = false;
                // What finished before the list was asked for is what it shows; anything finishing meanwhile waits a round.
                const listed = uploader.getFiles().filter(file => file.progress.uploadComplete && !file.error).map(file => file.id);
                const announced = uploaded.splice(0);
                await load(true);
                for (const id of listed) if (uploader.getFile(id)) uploader.removeFile(id);
                // A list that could not be read keeps saying so; otherwise the notice is the first failed file's, or nothing.
                if (state) { const failed = failedFile(); if (failed) failUpload(failed.error, failed.id); else clearUpload(); }
                resetDetails();
                for (const detail of announced) root.dispatchEvent(new CustomEvent("app-attachments:changed", { bubbles: true, detail }));
            } while (refreshAgain && root.isConnected);
        } finally { refreshing = false; syncBusy(); }
    }
    function render() {
        list.replaceChildren(); list.setAttribute("aria-busy", "false");
        if (form && !recordKey && !newUploads) { message.textContent = t("saveRecordBeforeFiles"); if (addButton) addButton.hidden = true; return; }
        if (!state) return;
        // No file storage is not an error of this page: a plain notice, and for an administrator the way to the setup screen.
        if (state.storage) {
            if (addButton) addButton.hidden = true;
            message.textContent = state.storage.message;
            if (state.storage.setupUrl) {
                const setup = document.createElement("a");
                setup.href = state.storage.setupUrl; setup.textContent = t("openDatabaseSetup");
                message.append(" ", setup);
            }
            return;
        }
        const canWrite = state.canWrite && !readOnly;
        // Someone may add files without being allowed to rename or remove the ones there.
        const canAdd = (state.canAdd ?? state.canWrite) && !readOnly;
        if (addButton) addButton.hidden = !canAdd && !unfinished();
        message.textContent = state.items.length ? "" : t("noFiles");
        for (const item of state.items) {
            const row = document.createElement("li");
            row.className = "app-attachment"; row.dataset.id = item.id;
            const name = document.createElement("a");
            name.className = "app-attachment-name"; name.textContent = item.fileName;
            name.href = requestUrl(item.id).toString();
            const meta = document.createElement("span");
            meta.className = "app-attachment-meta";
            meta.textContent = [item.purpose, item.label, sizeText(item.size), `${item.uploadedBy}, ${dateTimeText(item.uploadedAt)}`].filter(Boolean).join(" · ");
            row.append(name, meta);
            if (item.notes) { const notes = document.createElement("p"); notes.className = "app-attachment-notes"; notes.textContent = item.notes; row.append(notes); }
            if (canWrite) {
                const actions = document.createElement("span");
                actions.className = "app-attachment-actions";
                actions.append(actionButton(t("renameFile"), () => rename(item)));
                actions.append(actionButton(t("removeFile"), () => { if (window.confirm(t("confirmRemoveFile", { name: item.fileName }))) remove(item.id); }));
                row.append(actions);
            }
            list.append(row);
        }
        syncBusy();
    }
    // The block's files, read again. A list read after uploads that cannot be read says so in the upload dialog too.
    async function load(afterUpload = false) {
        clear();
        if (form && !recordKey && !newUploads) { state = null; render(); return; }
        // The form's key when the first of a new record's files is listed; the form takes a new one after each save, and
        // the files stay under this one until they join the record.
        if (pending()) pendingKey ??= submissionKeyFor(form) ?? newSubmissionKey();
        list.setAttribute("aria-busy", "true");
        try {
            state = await request(requestUrl()); render();
            if (pending()) pendingFiles = state.items?.length > 0;
        } catch (error) {
            if (signInNeeded(error, anonymous)) { root.replaceWith(signInNotice(t("signInFiles"))); return; }
            state = null; render();
            const problem = errorText(error, t("filesLoadFailed"));
            if (afterUpload) failUpload(problem); else fail(problem);
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
    async function rename(item) {
        if (busy || saving || refreshing || !state || transferring()) return;
        const fileName = window.prompt(t("newFileName", { name: item.fileName }), item.fileName)?.trim();
        if (fileName == null || fileName === item.fileName) return;
        clear(); busy = true; syncBusy(); message.textContent = t("renaming");
        try {
            await request(requestUrl(item.id), {
                method: "PATCH", headers: { RequestVerificationToken: state.antiforgeryToken }, body: { fileName },
            });
            await load();
            root.dispatchEvent(new CustomEvent("app-attachments:changed", { bubbles: true, detail: { id: item.id, operation: "rename" } }));
        } catch (error) {
            fail(signInNeeded(error, anonymous) ? t("signInThenRetry") : errorText(error, t("fileOperationFailed")));
            message.textContent = "";
        } finally { busy = false; syncBusy(); }
    }
    async function remove(id) {
        if (busy || saving || refreshing || !state || transferring()) return;
        clear();
        busy = true; syncBusy(); message.textContent = t("removing");
        try {
            try {
                await request(requestUrl(id), {
                    method: "DELETE", headers: { RequestVerificationToken: state.antiforgeryToken },
                });
            } catch (error) {
                if (!(error instanceof ApiError)) throw error;
                fail(signInNeeded(error, anonymous) ? t("signInThenRetry") : errorText(error, t("fileOperationFailed"))); message.textContent = ""; return;
            }
            await load();
            root.dispatchEvent(new CustomEvent("app-attachments:changed", { bubbles: true, detail: { id, operation: "remove" } }));
        } catch { fail(t("requestFailed")); message.textContent = ""; }
        finally { busy = false; syncBusy(); }
    }
    retryButton?.addEventListener("click", () => {
        if (!uploader || busy || saving || transferring()) return;
        clearUpload();
        sendFiles(uploader.getFiles().filter(file => file.error).map(file => file.id));
    });
    if (form) {
        registerAttachments(form, {
            // "working" while a request is under way, "queued" while files wait to upload or failed to; nothing once the block is gone.
            standing: () => !root.isConnected ? null : busy || refreshing || transferring() ? "working" : unfinished() ? "queued" : null,
            saving(value) { saving = value; syncBusy(); },
        });
        const follow = async event => {
            busy = true; syncBusy();
            try {
                const key = event.detail?.record?.key ?? null;
                // A new record's files join it once it has a key, before the block lists the record's own.
                const problem = key && pendingKey && pendingFiles ? await adopt(key) : null;
                if (key && !pendingFiles) pendingKey = null;
                recordKey = key;
                await load();
                if (problem) fail(problem);
            } finally { busy = false; syncBusy(); }
        };
        form.addEventListener("app-form:loaded", follow);
        form.addEventListener("app-form:saved", follow);
        render();
    } else load();
}
