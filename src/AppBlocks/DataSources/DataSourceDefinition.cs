using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UmbracoAppHost.Forms;

namespace UmbracoAppHost.DataSources;

/// <summary>Aliases of the data source document types and their properties, shared by the export, the reader and the docs.</summary>
public static class DataSourceSchema
{
    /// <summary>The root that holds data sources: no template, allowed at root, never routed.</summary>
    public const string RootAlias = "dataSources";
    /// <summary>A data source that reads and writes one table or SELECT through a registered connection.</summary>
    public const string SqlAlias = "sqlDataSource";
}

/// <summary>How a field reads and writes its column: the alias forms bind to and the overrides the schema cannot express.</summary>
/// <param name="Column">The column, or null when the alias matches a column by name (orderDate matches ORDER_DATE).</param>
/// <param name="Kind">One of the field kinds the blocks know, for columns whose type does not say it (SQLite keeps dates as text).</param>
public sealed record FieldMapping(string Alias, string? Column = null, string? Label = null, string? Kind = null,
    IReadOnlyList<FormChoice>? Choices = null, bool? ReadOnly = null, bool? Required = null, int? MaxLength = null,
    decimal? Minimum = null, decimal? Maximum = null);

/// <summary>A column filled on insert when the form does not send it, and shown as the new record's starting value.</summary>
/// <param name="Variable">The bind variable the value comes from, or null for <paramref name="Literal"/>.</param>
/// <param name="Literal">The text written after the equals sign; null means SQL NULL.</param>
public sealed record InsertDefault(string Column, BindVariable? Variable, string? Literal);

/// <summary>Optional columns that record who created or changed a row and when, in the application's own table.</summary>
public sealed record AuditColumns(string? CreatedBy = null, string? CreatedAt = null, string? UpdatedBy = null, string? UpdatedAt = null)
{
    [JsonIgnore]
    public IEnumerable<string> All => new[] { CreatedBy, CreatedAt, UpdatedBy, UpdatedAt }.OfType<string>();
}

/// <summary>Statements an editor writes to replace the generated insert, update or delete; each runs whole, with bound parameters.</summary>
public sealed record CustomStatements(string? Insert = null, string? Update = null, string? Delete = null);

/// <summary>
/// A published <c>sqlDataSource</c> node, read once into plain values. Structured properties are line-based
/// text or JSON so an editor needs no special UI; this reader reports every mistake it finds so publishing can
/// list them together. Nothing here touches a database: the planner checks the definition against the schema.
/// </summary>
public sealed record DataSourceDefinition
{
    public required Guid Key { get; init; }
    public required string Name { get; init; }
    public required string Alias { get; init; }
    /// <summary>A connection registered with AppBlocks; credentials never enter content.</summary>
    public required string Connection { get; init; }
    /// <summary>A table name or a SELECT statement.</summary>
    public required string Source { get; init; }
    public IReadOnlyList<string> KeyColumns { get; init; } = [];
    /// <summary>Optional Oracle key column to sequence name mapping, for tables without an identity (including 11g).</summary>
    public IReadOnlyDictionary<string, string> KeySequences { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<FieldMapping> Mapping { get; init; } = [];
    /// <summary>A predicate added to every fetch and write, bound to the signed-in person.</summary>
    public string? RowRestriction { get; init; }
    public bool AllowInsert { get; init; }
    public bool AllowUpdate { get; init; }
    public bool AllowDelete { get; init; }
    /// <summary>The version column; null compares a checksum of the fetched row instead.</summary>
    public string? VersionColumn { get; init; }
    public IReadOnlyList<InsertDefault> InsertDefaults { get; init; } = [];
    public AuditColumns AuditColumns { get; init; } = new();
    /// <summary>Member group names that may read; empty leaves reading to the page's own access.</summary>
    public IReadOnlyList<string> ReadGroups { get; init; } = [];
    /// <summary>Member group names that may write; empty leaves writing to whoever may read.</summary>
    public IReadOnlyList<string> WriteGroups { get; init; } = [];
    public CustomStatements CustomStatements { get; init; } = new();

    [JsonIgnore]
    public bool AllowsWrites => AllowInsert || AllowUpdate || AllowDelete;

    /// <summary>A hash of every setting, so a changed definition gets a new plan and invalidates loaded forms.</summary>
    public string ComputeFingerprint() => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this)));

    public const int MaxSourceLength = 20_000;
    public const int MaxRestrictionLength = 4_000;
    public const int MaxStatementLength = 20_000;
    public const int MaxMappingLength = 50_000;

    /// <summary>Reads a definition from property values, which come from published content or from content being published.</summary>
    /// <param name="value">Returns the raw value of a property by alias.</param>
    /// <param name="memberGroups">Turns a member group picker value into group names, or null when a picked group no longer exists.</param>
    /// <param name="errors">Receives every problem found; the definition is returned only when there are none.</param>
    public static DataSourceDefinition? Read(Guid key, string name, Func<string, object?> value,
        Func<string, IReadOnlyList<string>?> memberGroups, ICollection<string> errors)
    {
        var start = errors.Count;
        string Text(string alias) => value(alias) switch { null => "", string text => text.Trim(), var other => Convert.ToString(other, CultureInfo.InvariantCulture)?.Trim() ?? "" };
        bool Flag(string alias) => value(alias) switch {
            bool flag => flag, int number => number != 0, long number => number != 0,
            string text => text.Trim() is "1" or "true" or "True" or "TRUE", _ => false,
        };

        var alias = Text("alias");
        if (alias.Length == 0) errors.Add("Enter the data source's alias, the name a Form interface selects.");
        else if (!IsAlias(alias)) errors.Add($"The alias '{alias}' may use letters, digits, '.', '-' and '_', start with a letter and have at most 64 characters.");
        var connection = Text("connection");
        if (connection.Length == 0) connection = AppBlocks.Connections.ConnectionCatalog.UmbracoConnection;
        var source = Text("source");
        if (source.Length == 0) errors.Add("Enter the row source: a table name or a SELECT statement.");
        else if (source.Length > MaxSourceLength) errors.Add($"The row source is longer than {MaxSourceLength} characters.");
        var restriction = Text("rowRestriction");
        if (restriction.Length > MaxRestrictionLength) errors.Add($"The row restriction is longer than {MaxRestrictionLength} characters.");
        var concurrency = Text("concurrency");
        IReadOnlyList<string> versionColumns = concurrency.Length == 0 || concurrency.Equals("checksum", StringComparison.OrdinalIgnoreCase)
            ? [] : Columns(concurrency, "concurrency", errors);
        if (versionColumns.Count > 1) errors.Add("Concurrency names one version column, or stays empty to compare a checksum of the row.");

        var definition = new DataSourceDefinition {
            Key = key, Name = name, Alias = alias, Connection = connection, Source = source,
            KeyColumns = Columns(Text("keyColumns"), "key columns", errors),
            KeySequences = ReadKeySequences(Text("keySequences"), errors),
            Mapping = ReadMapping(Text("mapping"), errors),
            RowRestriction = restriction.Length == 0 ? null : restriction,
            AllowInsert = Flag("allowInsert"), AllowUpdate = Flag("allowUpdate"), AllowDelete = Flag("allowDelete"),
            VersionColumn = versionColumns.FirstOrDefault(),
            InsertDefaults = ReadInsertDefaults(Text("insertDefaults"), errors),
            AuditColumns = ReadAuditColumns(Text("auditColumns"), errors),
            ReadGroups = Groups(Text("readGroups"), "read groups"),
            WriteGroups = Groups(Text("writeGroups"), "write groups"),
            CustomStatements = ReadCustomStatements(Text("customStatements"), errors),
        };
        if (definition.AllowsWrites && definition.KeyColumns.Count == 0)
            errors.Add("Enter the key columns: writes find their row by key.");
        return errors.Count == start ? definition : null;

        IReadOnlyList<string> Groups(string picked, string label)
        {
            if (picked.Length == 0) return [];
            if (memberGroups(picked) is { } names) return names;
            errors.Add($"One of the {label} no longer exists. Pick the groups again.");
            return [];
        }
    }

    /// <summary>Aliases are URL- and JSON-safe so a Form interface can store them as plain text.</summary>
    public static bool IsAlias(string alias) => alias.Length is > 0 and <= 64 && char.IsAsciiLetter(alias[0])
        && alias.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    /// <summary>Field aliases become HTML names and JSON keys, so they are plain identifiers.</summary>
    public static bool IsFieldAlias(string alias) => alias.Length is > 0 and <= 64 && char.IsAsciiLetter(alias[0])
        && alias.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private static IReadOnlyDictionary<string, string> ReadKeySequences(string text, ICollection<string> errors)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (text.Length == 0) return result;
        if (text.Length > MaxMappingLength) { errors.Add("The key sequences mapping is too long."); return result; }
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object) { errors.Add("Key sequences must be a JSON object from key column to sequence name."); return result; }
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Name.Length is 0 or > 128 || property.Value.ValueKind != JsonValueKind.String
                    || TableName.Parse(property.Value.GetString()!) is null)
                    errors.Add($"Key sequence {property.Name} must name a database sequence, optionally qualified by schema.");
                else if (!result.TryAdd(property.Name, property.Value.GetString()!)) errors.Add($"Key sequences name {property.Name} twice.");
        }
        catch (JsonException error) { errors.Add("Key sequences are not valid JSON: " + error.Message); }
        return result;
    }

    /// <summary>
    /// Column names separated by commas or lines. A name may be quoted with brackets or double quotes when it holds
    /// spaces; it is only ever compared with the columns the schema reports, never written into a statement as typed.
    /// </summary>
    internal static IReadOnlyList<string> Columns(string text, string label, ICollection<string> errors)
    {
        var columns = new List<string>();
        foreach (var raw in text.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var column = Unquote(raw);
            if (column.Length == 0 || column.Length > 128 || column.Any(char.IsControl)) errors.Add($"'{raw}' in the {label} is not a column name.");
            else if (columns.Contains(column, StringComparer.OrdinalIgnoreCase)) errors.Add($"The {label} name {column} twice.");
            else columns.Add(column);
        }
        return columns;
    }

    internal static string Unquote(string name)
    {
        name = name.Trim();
        if (name.Length >= 2 && (name[0], name[^1]) is ('[', ']') or ('"', '"') or ('`', '`')) return name[1..^1].Trim();
        return name;
    }

    /// <summary>
    /// The mapping is a JSON object from field alias to column name, or to an object of overrides:
    /// <c>{ "orderDate": { "column": "ORDER_DATE", "kind": "date" }, "client": "CUSTOMER" }</c>.
    /// </summary>
    private static IReadOnlyList<FieldMapping> ReadMapping(string text, ICollection<string> errors)
    {
        if (text.Length == 0) return [];
        if (text.Length > MaxMappingLength) { errors.Add($"The mapping is longer than {MaxMappingLength} characters."); return []; }
        JsonDocument document;
        try { document = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException error) { errors.Add("The mapping is not valid JSON: " + error.Message); return []; }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) { errors.Add("The mapping must be a JSON object from field alias to column."); return []; }
            var mappings = new List<FieldMapping>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var alias = property.Name;
                if (!IsFieldAlias(alias)) { errors.Add($"The field alias '{alias}' must start with a letter and use letters, digits and '_'."); continue; }
                if (mappings.Any(m => m.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase))) { errors.Add($"The mapping names field {alias} twice."); continue; }
                if (property.Value.ValueKind == JsonValueKind.String) { mappings.Add(new(alias, Unquote(property.Value.GetString()!))); continue; }
                if (property.Value.ValueKind != JsonValueKind.Object) { errors.Add($"Map field {alias} to a column name or to an object of settings."); continue; }
                mappings.Add(ReadField(alias, property.Value, errors));
            }
            return mappings;
        }
    }

    private static readonly string[] MappingSettings = ["column", "label", "kind", "choices", "readOnly", "required", "maxLength", "min", "max"];

    private static FieldMapping ReadField(string alias, JsonElement settings, ICollection<string> errors)
    {
        foreach (var unknown in settings.EnumerateObject().Select(p => p.Name).Where(n => !MappingSettings.Contains(n)))
            errors.Add($"Field {alias} has an unknown setting '{unknown}' (known: {string.Join(", ", MappingSettings)}).");
        string? Text(string name)
        {
            if (!settings.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
            if (value.ValueKind == JsonValueKind.String) return value.GetString();
            errors.Add($"Field {alias}: {name} must be text.");
            return null;
        }
        bool? Flag(string name)
        {
            if (!settings.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
            errors.Add($"Field {alias}: {name} must be true or false.");
            return null;
        }
        decimal? Number(string name)
        {
            if (!settings.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
            errors.Add($"Field {alias}: {name} must be a number.");
            return null;
        }

        var kind = Text("kind");
        if (kind is not null && !FieldKinds.All.Contains(kind)) errors.Add($"Field {alias}: kind must be one of {string.Join(", ", FieldKinds.All)}.");
        var maxLength = Number("maxLength");
        if (maxLength is { } length && (length < 1 || length > FieldKinds.MaxTextLength || length != decimal.Truncate(length)))
            errors.Add($"Field {alias}: maxLength must be a whole number between 1 and {FieldKinds.MaxTextLength}.");
        List<FormChoice>? choices = null;
        if (settings.TryGetProperty("choices", out var list))
        {
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() is 0 or > 500) errors.Add($"Field {alias}: choices must be a list of 1 to 500 values.");
            else
            {
                choices = [];
                foreach (var choice in list.EnumerateArray())
                {
                    if (choice.ValueKind == JsonValueKind.String) choices.Add(new(choice.GetString()!, choice.GetString()!));
                    else if (choice.ValueKind == JsonValueKind.Object && choice.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String)
                        choices.Add(new(v.GetString()!, choice.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString()! : v.GetString()!));
                    else errors.Add($"Field {alias}: each choice is a text value or {{ \"value\": ..., \"label\": ... }}.");
                }
                if (choices.Select(c => c.Value).Distinct(StringComparer.Ordinal).Count() != choices.Count) errors.Add($"Field {alias}: choices repeat a value.");
            }
        }
        var column = Text("column");
        return new(alias, column is null ? null : Unquote(column), Text("label"), kind, choices, Flag("readOnly"), Flag("required"),
            maxLength is { } max && max >= 1 && max <= FieldKinds.MaxTextLength ? (int)max : null, Number("min"), Number("max"));
    }

    /// <summary>Lines of <c>COLUMN = value</c>, where the value is a bind variable, a 'quoted' or bare literal, or NULL.</summary>
    private static IReadOnlyList<InsertDefault> ReadInsertDefaults(string text, ICollection<string> errors)
    {
        var defaults = new List<InsertDefault>();
        foreach (var (column, value) in Assignments(text, "insert default", errors))
        {
            if (defaults.Any(d => d.Column.Equals(column, StringComparison.OrdinalIgnoreCase))) { errors.Add($"The insert defaults set {column} twice."); continue; }
            if (value.StartsWith(':'))
            {
                var variable = BindVariable.Parse(value[1..]);
                if (variable is null) errors.Add($"The insert default for {column} uses {value}, which is not a bind variable.");
                else if (variable.Kind == BindVariableKind.UserGroups) errors.Add($"The insert default for {column} cannot use :USER_GROUPS, which is a list.");
                else defaults.Add(new(column, variable, null));
            }
            else if (value.Equals("NULL", StringComparison.OrdinalIgnoreCase)) defaults.Add(new(column, null, null));
            else if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') defaults.Add(new(column, null, value[1..^1].Replace("''", "'")));
            else defaults.Add(new(column, null, value));
        }
        return defaults;
    }

    /// <summary>Lines of <c>createdBy = CREATED_BY</c>; the four keys are createdBy, createdAt, updatedBy and updatedAt.</summary>
    private static AuditColumns ReadAuditColumns(string text, ICollection<string> errors)
    {
        var audit = new AuditColumns();
        foreach (var (name, column) in Assignments(text, "audit column", errors))
        {
            var target = Unquote(column);
            audit = name.Replace("_", "").Replace(" ", "").ToLowerInvariant() switch {
                "createdby" when audit.CreatedBy is null => audit with { CreatedBy = target },
                "createdat" when audit.CreatedAt is null => audit with { CreatedAt = target },
                "updatedby" when audit.UpdatedBy is null => audit with { UpdatedBy = target },
                "updatedat" when audit.UpdatedAt is null => audit with { UpdatedAt = target },
                _ => Unknown(name),
            };
        }
        if (audit.All.GroupBy(c => c, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) errors.Add("Each audit column records one thing; use four different columns.");
        return audit;

        AuditColumns Unknown(string name)
        {
            errors.Add($"'{name}' is not an audit column; use createdBy, createdAt, updatedBy or updatedAt, each once.");
            return audit;
        }
    }

    private static IEnumerable<(string Name, string Value)> Assignments(string text, string label, ICollection<string> errors)
    {
        var lines = text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length > 200) { errors.Add($"Use at most 200 {label} lines."); yield break; }
        foreach (var line in lines)
        {
            if (line.StartsWith("--", StringComparison.Ordinal) || line.StartsWith('#')) continue;
            var equals = line.IndexOf('=');
            if (equals <= 0) { errors.Add($"Write each {label} as NAME = value: '{line}'."); continue; }
            var name = Unquote(line[..equals]);
            var value = line[(equals + 1)..].Trim();
            if (name.Length == 0 || value.Length == 0) { errors.Add($"Write each {label} as NAME = value: '{line}'."); continue; }
            yield return (name, value);
        }
    }

    /// <summary>
    /// Sections that each start with a line <c>-- insert</c>, <c>-- update</c> or <c>-- delete</c>. A marker is an
    /// ordinary SQL comment, so the text stays valid SQL in an editor that highlights it.
    /// </summary>
    private static CustomStatements ReadCustomStatements(string text, ICollection<string> errors)
    {
        if (text.Length == 0) return new();
        var sections = new Dictionary<string, StringBuilder>(StringComparer.OrdinalIgnoreCase);
        StringBuilder? current = null;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("--", StringComparison.Ordinal) && trimmed[2..].Trim().ToLowerInvariant() is ("insert" or "update" or "delete") and var section)
            {
                if (sections.ContainsKey(section)) errors.Add($"The custom statements have two '-- {section}' sections.");
                current = sections[section] = new StringBuilder();
                continue;
            }
            if (current is null) { if (trimmed.Length > 0) errors.Add("Start the custom statements with a '-- insert', '-- update' or '-- delete' line."); current = new(); continue; }
            current.Append(line.TrimEnd('\r')).Append('\n');
        }
        string? Statement(string section)
        {
            if (!sections.TryGetValue(section, out var body)) return null;
            var statement = body.ToString().Trim();
            if (statement.Length > MaxStatementLength) errors.Add($"The custom {section} statement is longer than {MaxStatementLength} characters.");
            return statement.Length == 0 ? null : statement;
        }
        return new(Statement("insert"), Statement("update"), Statement("delete"));
    }
}
