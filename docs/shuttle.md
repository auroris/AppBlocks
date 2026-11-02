# Shuttle field

The **Shuttle** form field (`appShuttle`) shows available items on the left and selected items on the right. Visitors can add or remove highlighted items, move all items at once, and reorder the selected list with **Top**, **Up**, **Down**, and **Bottom**. Multiple highlighted items retain their relative order. The lists use native multi-select controls and the buttons work with the keyboard. On narrow screens the lists stack.

Add a Shuttle inside a Form in the Forms container, including inside a fieldset or layout. Bind it to a **text** field on the registered form class. Supply either that field's `Choices` or a published **List of values**, as with Select:

```csharp
new FormField("steps", "Steps", "text", MaxLength: 2000,
    Choices: [new("review", "Review"), new("approve", "Approve"), new("publish", "Publish")])
```

The form class receives the entire right-hand list as one CSV string, using item **values**, in displayed order. Highlighting an item only selects it for the next transfer or reorder operation; it does not determine what is saved. For example, Publish above Review submits `publish,review`. An empty list submits `""`.

Values containing commas or double quotes use CSV escaping. The two values `north,east` and `say "yes"` become `"north,east","say ""yes"""`. Item values must be unique, non-empty, and contain no line breaks, so output stays on one line. Labels can differ from values. The field's maximum length applies to the complete CSV string.

Use `ShuttleValue.TryParse(csv, out var items)` and `ShuttleValue.Serialize(items)` from `AppBlocks.Forms` to read or create ordered selections in your class. The server rejects malformed CSV, duplicate values, and unavailable choices before calling the class. A published list uses the same access checks as Select. Values already saved on a record remain visible if retired from that list, and can be reordered or removed; they cannot be added to new records.

**Default value** accepts ordered CSV, including a bind variable such as `:APP.steps` or `:PARAM.steps`. It applies only when a new record has no value. Required, read-only, hidden, help text, and width settings work as for other form fields. A required shuttle needs at least one item on the right. Read-only fields and records without create/update permission disable both lists and all transfer/reorder controls.

Fresh sites offer Shuttle automatically. On an existing site, startup installs the new element type but preserves the site's existing grid configuration. In **Settings → Data Types → AppBlocks: Forms container**, add **Shuttle** to the **Content** block group, allow it in areas, and leave it disallowed at the root, matching Select. See [who owns the definitions](developing.md#who-owns-the-definitions).
