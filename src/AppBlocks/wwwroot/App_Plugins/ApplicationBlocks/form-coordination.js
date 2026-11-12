
// The contract between a form and its child blocks. Registrations remain private to the coordinator.
// Detail blocks supply pending/refused/saved/keyed; attachment blocks supply standing/saving.
// app-form:loaded and app-form:saved carry the record and any saved detail answers back to the blocks.
const details = new WeakMap(), attachments = new WeakMap(), submissions = new WeakMap();
export function registerDetails(form, block) {
    if (!details.has(form)) details.set(form, []);
    details.get(form).push(block);
}
export function registerAttachments(form, block) {
    if (!attachments.has(form)) attachments.set(form, []);
    attachments.get(form).push(block);
}
export const detailsFor = form => details.get(form) ?? [];
export const attachmentsFor = form => attachments.get(form) ?? [];
export const setSubmissionKey = (form, read) => submissions.set(form, read);
export const submissionKeyFor = form => submissions.get(form)?.();
