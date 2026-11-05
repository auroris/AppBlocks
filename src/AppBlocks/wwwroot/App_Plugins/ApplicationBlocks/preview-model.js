// These are authoring representations. They never execute form classes, SQL, scripts or report actions.
export const escape = value => String(value ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
export function json(value) {
    if (typeof value !== "string") return value;
    try { return JSON.parse(value); } catch { return value; }
}
export const richText = value => typeof value === "string" ? value : value?.markup ?? "";
export const note = text => `<p class="preview-note">${escape(text)}</p>`;
export const heading = text => text ? `<h3>${escape(text)}</h3>` : "";
export const button = text => `<button type="button" disabled>${escape(text)}</button>`;
export function values(item, culture) {
    const result = {};
    for (const property of item?.values ?? []) {
        if (!property.culture && !property.segment) result[property.alias] = property.value;
    }
    if (culture) for (const property of item?.values ?? []) {
        if (property.culture === culture && !property.segment) result[property.alias] = property.value;
    }
    return result;
}
export function listItems(value, culture) {
    const block = json(value);
    const layout = block?.layout?.["Umbraco.BlockList"] ?? block?.layout?.["Umbraco.SingleBlock"] ?? [];
    return layout.map(item => values(block.contentData?.find(data => data.key === item.contentKey), culture));
}
export function table(columns, message = "Rows appear on the page for the permitted visitor.") {
    const names = columns.length ? columns : ["Column", "Value"];
    return `<table><thead><tr>${names.map(name => `<th>${escape(name)}</th>`).join("")}</tr></thead><tbody><tr><td colspan="${names.length}">${escape(message)}</td></tr></tbody></table>`;
}

export const fieldTypes = Object.freeze({ appText: "text", appTextArea: "textarea", appNumber: "number", appDate: "date", appTime: "time", appDateTime: "datetime-local", appCheckbox: "checkbox", appSelect: "select", appShuttle: "shuttle", appColor: "color", appDisplay: "display" });
export function fieldPreview(alias, content) {
    const type = fieldTypes[alias];
    const label = content.label || content.field || "Choose a field";
    const value = escape(content.defaultValue);
    const placeholder = escape(content.placeholder);
    let input;
    switch (type) {
        case "textarea": input = `<textarea disabled rows="3" placeholder="${placeholder}">${value}</textarea>`; break;
        case "select": input = `<select disabled><option>${placeholder || "Select a value"}</option></select>`; break;
        case "shuttle": input = `<div class="preview-columns"><div><label>Available</label><select disabled size="3"><option>Choices from the configured list</option></select></div><div>${button("Add →")}${button("← Remove")}</div><div><label>Selected</label><select disabled size="3"><option>${value}</option></select></div></div>`; break;
        case "display": input = `<output>${value || "Value from the record"}</output>`; break;
        default: input = `<input disabled type="${type}" placeholder="${placeholder}" value="${value}" ${type === "checkbox" && [true, 1, "true", "1"].includes(content.defaultValue) ? "checked" : ""}>`;
    }
    return `<div class="app-form-field"><label>${escape(label)}${content.required ? " *" : ""}</label>${input}<small>${escape(content.helpText)}</small></div>`
        + (content.hidden ? note("Hidden field — not displayed on the page.") : "")
        + (content.readOnly ? note("Read only") : "");
}

export function renderAppBlock(alias, content = {}, settings = {}, nested = () => "", culture) {
    if (fieldTypes[alias]) return fieldPreview(alias, content);
    const title = heading(content.heading);
    const help = content.helpText ? note(content.helpText) : "";
    switch (alias) {
        case "appRichText": return `<div class="ah-rich-text">${richText(content.text) || note("Add rich text.")}</div>`;
        case "appHtml": return content.html || note("Add HTML.");
        case "appCss": return note("Page stylesheet") + `<pre><code>${escape(content.css || "/* Add CSS */")}</code></pre>`;
        case "appScript": return note("Page JavaScript — runs on the page only") + `<pre><code>${escape(content.script || "// Add JavaScript")}</code></pre>`;
        case "appFullWidth": case "appColumns": case "appThreeColumns": return "";
        case "appRegion": return title + (settings.collapsible ? note("Collapsible region") : "");
        case "appFieldset": return title + help;
        case "appForm": {
            const config = listItems(content.formInterface, culture)[0] ?? {};
            return title + note(`Form · ${config.resource || "Choose a form class"}${config.viewOnly ? " · View only" : ""}`);
        }
        case "appFormInterface": return `<dl><dt>Form class</dt><dd>${escape(content.resource || "Not selected")}</dd><dt>Record key parameter</dt><dd>${escape(content.keyParameter || "id")}</dd><dt>Mode</dt><dd>${content.viewOnly ? "View only" : "Edit"}</dd></dl>`;
        case "appFormsContainer": return nested(content.forms) || note("Add forms to this container.");
        case "appActions": return `<div class="app-form-actions">${button(content.saveLabel || "Save")}${button(content.cancelLabel || "Cancel")}${button(content.deleteLabel || "Delete")}</div>`;
        case "appAttachments": return title + help + `<div class="preview-dropzone">${button("Choose files")}${note(content.allowedTypes || "Attachments")}</div>` + table(["File", "Purpose", "Size"], "Files appear for the selected record.");
        case "appTabular": return title + help + table(String(content.fields || "").split(/[,\n]/).map(s => s.trim()).filter(Boolean), content.emptyMessage || "Rows from the configured data source") + (content.hideAdd ? "" : button(content.addLabel || "Add row")) + button(content.saveLabel || "Save");
        case "appSchedule": return title + help + `<div class="preview-timeline"><div>Resources</div><div>Timeline · ${escape(content.daysBefore ?? 7)} days before / ${escape(content.daysAfter ?? 30)} days after</div><div>Scheduled resource</div><div><span class="preview-bar">Scheduled item</span></div></div>` + note(`Authoring example · rows: ${content.rowsSource || "not selected"} · bars: ${content.barsSource || "not selected"}`);
        case "appAntiSpam": return `<fieldset disabled><label><input type="checkbox"> Verification</label></fieldset>` + note(`${content.service || "Choose a verification service"} · ${content.size || "normal"} · Verification runs on the page only.`);
        default: return note(`Preview unavailable for ${alias}.`);
    }
}
