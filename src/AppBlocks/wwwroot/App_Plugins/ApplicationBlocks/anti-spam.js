import { t } from "./runtime-text.js";
import { otherControls } from "./runtime-controls.js";


// The Anti-spam block's widget. The server rendered its script and says which global it provides, with that service's render
// and reset, or its execute when it shows no widget. All this does is put a token in a control inside the form, where
// otherControls finds it: the server decides, and refuses a submission without one whatever happens here.
export function mountAntiSpam(root, form) {
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

