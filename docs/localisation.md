# Localisation

Every text the blocks show a visitor, from a button's label to a refusal, has an id and an English default. A site replaces any of them, per language, from its Umbraco dictionary or from a source of its own, and the server-rendered markup and the browser runtime both show the replacement. Texts an editor types on a block, such as a heading, a field's label or an Actions block's button labels, are content, and Umbraco's own language variants translate them.

The ids cover the base package's blocks on the website: forms and their fields, attachments, tabular forms, schedules and the anti-spam honeypot.

## From the Umbraco dictionary

1. In **Translation**, create a dictionary item with the key `AppBlocks`.
2. Under it, create one item per text, keyed `AppBlocks.<id>`, such as `AppBlocks.saved`. A child keyed by the id alone, `saved`, works too.
3. Give each item its translation in each language the site serves.

A page then shows the translation for the culture Umbraco serves it in. An id with no item, or an item with no translation for that culture, keeps the English. The dictionary is read once per request.

## From a source of your own

A site that keeps its texts elsewhere implements `IAppBlocksTextSource` and registers it:

```csharp
using AppBlocks.Blocks;

public sealed class SiteTexts(SiteTranslations translations) : IAppBlocksTextSource
{
    // Null for an id this source does not know, so the next source, and then the English, answers.
    public string? Get(string id) => translations.Find("appblocks", id);

    // Every id this source knows for the current culture, for the page's scripts.
    public IReadOnlyDictionary<string, string> All() => translations.All("appblocks");
}
```

```csharp
builder.Services.AddScoped<IAppBlocksTextSource, SiteTexts>();
```

AppBlocks registers `UmbracoDictionaryTextSource` the same way. The sources are asked in the order they were registered, and the first that answers an id with text wins. Both members answer for the current request's culture.

## How the texts reach the page

Server-rendered views ask `AppBlocksText`, a scoped service, with the id and its English: `Text["saved", "Saved."]`. A companion's own views can inject it and use their own ids the same way.

For the browser runtime, the page carries every replacement the sources know in a JSON script element at the end of the body:

```html
<script type="application/json" data-appblocks-strings>{"saved":"Enregistré.","confirmDelete":"Supprimer cet enregistrement?"}</script>
```

The runtime takes the ids it knows from it and keeps its English for the rest. A page whose sources know no replacement carries no element.

## Placeholders

Some texts carry values. The browser runtime's texts name them in braces, such as `{count}` and `{name}`, and a replacement writes the same names wherever its language puts them. The schedule's server-rendered texts number them, `{0}` and `{1}`, as .NET composite formatting does; a replacement whose placeholders do not match falls back to the English, so a typo never breaks the page.

## Dates, times and sizes

The browser runtime formats dates, times and file sizes for the page's language, `document.documentElement.lang`, so a layout that writes `<html lang="fr-CA">` gets French dates. A page that names no language, or one the browser does not know, gets the browser's own. Server-rendered dates, such as the schedule's column headings and window, follow the culture Umbraco serves the page in.

## Browser runtime texts

| Id | English |
| --- | --- |
| `loading` | Loading… |
| `saving` | Saving… |
| `deleting` | Deleting… |
| `uploading` | Uploading… |
| `removing` | Removing… |
| `saved` | Saved. |
| `yes` | Yes |
| `no` | No |
| `currentValue` | {value} (current value) |
| `requestFailed` | The request could not be completed. |
| `requestFailedRetained` | The request could not be completed. Your entered values have been retained. |
| `signInForm` | Sign in to use this form. |
| `signInFiles` | Sign in to see the files. |
| `signInRows` | Sign in to see the rows. |
| `signInThenRetry` | Sign in, then try again. |
| `antiSpamUnavailable` | The anti-spam check could not load. Check your connection or anything blocking it, then reload the page. |
| `antiSpamIncomplete` | Complete the anti-spam check, then try again. |
| `shuttleOneLine` | The selected values must be one line of CSV. |
| `shuttleInvalid` | The selected CSV is invalid. |
| `shuttleUnique` | Selected values must be unique and non-empty. |
| `shuttleRequired` | Select at least one item. |
| `shuttleSelected` | {count} selected. |
| `shuttleOrder` | {count} selected. Order: {order}. |
| `shuttleOrderNone` | none |
| `formForbidden` | You do not have access to this form. |
| `recordNotFound` | The record was not found. |
| `formLoadFailed` | The form could not be loaded. |
| `confirmDelete` | Delete this record? |
| `rowsRefusedRecordSaved` | The record was saved, but its rows were not. Correct the rows marked below and save again. |
| `rowsRefusedNothingSaved` | Nothing was saved. Correct the rows marked below and save again. |
| `operationForbidden` | This operation is not permitted. |
| `tooLarge` | This is too much to send at once. Shorten what you entered and try again. |
| `tooManyRequests` | That was too many tries. Wait a minute and try again. |
| `operationFailed` | The operation failed. Reload the page if the record has changed. |
| `saveRecordBeforeFiles` | Save the record before adding files. |
| `openDatabaseSetup` | Open database setup |
| `noFiles` | No files yet. |
| `earlierRevision` | 1 earlier revision |
| `earlierRevisions` | {count} earlier revisions |
| `replaceFile` | Replace |
| `removeFile` | Remove |
| `confirmRemoveFile` | Remove {name}? |
| `filesLoadFailed` | The files could not be loaded. |
| `filesNotAttached` | The record was saved, but its files could not be attached to it. Save again to retry. |
| `fileTooLarge` | The file is larger than the {size} MB this block accepts. |
| `fileOperationFailed` | The operation failed. |
| `chooseFile` | Choose a file to upload. |
| `sizeBytes` | {size} B |
| `sizeKilobytes` | {size} KB |
| `sizeMegabytes` | {size} MB |
| `noRows` | No rows yet. |
| `deleteRow` | Delete |
| `undoDelete` | Undo |
| `saveRecordBeforeRows` | Save the record before adding rows. |
| `reloadRows` | Reload rows |
| `rowsLoadFailed` | The rows could not be loaded. |
| `rowNumber` | Row {number} |
| `rowConflict` | This row was changed elsewhere. Reload the rows to see it; the changes entered here would then be lost. |
| `rowsSaveFailed` | The rows could not be saved. |
| `rowsSaveFailedReload` | The rows could not be saved. Reload the page and try again. |
| `nothingToSave` | Nothing to save. |

## Server-rendered texts

| Id | English |
| --- | --- |
| `signInToUsePage` | Sign in with an Umbraco Member or backoffice User account permitted to use this page. |
| `signInToUseForm` | Sign in to use this form. |
| `formInvalid` | This form has an invalid resource or field configuration. |
| `save` | Save |
| `cancel` | Cancel |
| `delete` | Delete |
| `print` | Print |
| `selectValue` | Select a value |
| `choicesUnavailable` | The choices for this field are not available. Try reloading the page. |
| `chooseColorFor` | Choose a color for {label} |
| `clearColorFor` | Clear color for {label} |
| `clear` | Clear |
| `colorFormat` | Enter a color in #RRGGBB format, such as #2a5d8f. |
| `honeypotLabel` | Leave this empty |
| `shuttleAvailable` | Available |
| `shuttleSelectedHeading` | Selected (in order) |
| `shuttleMoveItems` | Move items |
| `shuttleAdd` | Add → |
| `shuttleAddAll` | Add all → |
| `shuttleRemove` | ← Remove |
| `shuttleRemoveAll` | ← Remove all |
| `shuttleReorder` | Reorder selected items |
| `shuttleTop` | Top |
| `shuttleUp` | Up |
| `shuttleDown` | Down |
| `shuttleBottom` | Bottom |
| `attachmentsSignIn` | Sign in to see the files. |
| `attachmentsNoOwner` | This Attachments block is not inside a form and names no owner. |
| `attachmentsFile` | File |
| `attachmentsLabel` | Label |
| `attachmentsNotes` | Notes |
| `attachmentsPurpose` | Purpose |
| `attachmentsChoosePurpose` | Choose a purpose |
| `attachmentsUpload` | Upload |
| `tabularSignIn` | Sign in to see the rows. |
| `tabularNoOwner` | This Tabular form block is not inside a form and names no owner. |
| `tabularEmpty` | No rows yet. |
| `tabularActions` | Actions |
| `tabularAddRow` | Add row |
| `tabularSaveRows` | Save rows |
| `scheduleSignIn` | Sign in to see the schedule. |
| `scheduleCaption` | Schedule |
| `scheduleNavigation` | Schedule window |
| `scheduleEarlier` | Earlier |
| `scheduleToday` | Today |
| `scheduleLater` | Later |
| `scheduleJumpTo` | Jump to |
| `scheduleMonths` | Months |
| `scheduleQuarters` | Quarters |
| `scheduleQuarter` | Q{0} {1} |
| `scheduleWindow` | {0} to {1} |
| `scheduleBarRange` | {0} to {1} |
| `scheduleBarOnward` | {0} onward |
| `scheduleOpenLink` | Open link |
| `scheduleClose` | Close |
| `scheduleLegend` | Legend |
| `scheduleNoRows` | The schedule has no rows to show. |
| `scheduleNoRowsWithBars` | No rows have bars between {0} and {1}. |
| `scheduleNoBars` | No bars between {0} and {1}. |
| `scheduleOneRowHidden` | One row without bars in this window is not shown. |
| `scheduleRowsHidden` | {0} rows without bars in this window are not shown. |

`tabularAddRow` and `tabularSaveRows` apply while the block's own **Add label** and **Save label** keep their defaults; a label the editor wrote is shown as written.
