# Color picker

Add **Color picker** (`appColor`) inside a Form in a Forms container. Bind it to a `text` field that can store at least seven characters:

```csharp
new FormField("color", "Color", "text", MaxLength: 7)
```

Visitors can use the native color picker or type a six-digit hex color, such as `#2a5d8f`. Uppercase and lowercase hex digits are accepted. Named colors, shorthand hex and alpha values are not supported. Both browser and server validation check the format.

**Clear** removes the selection. An optional empty field submits `null`; a required field must have a color before saving. The checkerboard swatch represents an empty or invalid value. Existing invalid values stay visible in the text field so they can be corrected.

The block has the same label, help, required, read-only, hidden, width, placeholder and default-value settings as other fields. A default fills an empty field only on a new record and may be a literal hex color or a bind variable such as `:PARAM.color`. Read-only fields and forms without write permission disable both controls and the Clear button.

The schema installs on fresh sites. As with other schema changes, startup does not update an existing Forms container data type: add the installed **Color picker** element to its allowed blocks, or recreate a disposable development site to get the complete current schema.
