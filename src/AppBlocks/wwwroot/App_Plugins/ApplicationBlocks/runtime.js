import { mountForm } from "./forms.js";
import { mountAttachments } from "./attachments.js";
import { mountTabular } from "./tabular.js";
import { mountSchedule } from "./schedule.js";

// Attachments and tabular forms listen for their form's record, so they mount before the forms start loading.
document.querySelectorAll("[data-app-attachments]").forEach(mountAttachments);
document.querySelectorAll("[data-app-tabular]").forEach(mountTabular);
document.querySelectorAll("[data-app-schedule]").forEach(mountSchedule);
document.querySelectorAll("[data-app-form]").forEach(mountForm);
