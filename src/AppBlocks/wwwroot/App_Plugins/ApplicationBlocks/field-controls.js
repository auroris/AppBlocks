import { t } from "./runtime-text.js";

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

export function mountShuttle(root) {
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
export function mountColor(root) {
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

