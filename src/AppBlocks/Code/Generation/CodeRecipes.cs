using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AppBlocks.Connections;
using AppBlocks.DataSources;

// Public namespace retained for source compatibility; the file lives with its owning feature.
namespace AppBlocks.Samples;

public enum CodeRecipeKind { FormClass, BackingClass }

/// <summary>
/// What the C# section's New file wizard asks for: the table, the recipe, who may read and write in the data source vocabulary, and the
/// class's name and alias. Read groups apply only to Specific groups; write groups narrow writes in every mode, as on a data source.
/// </summary>
public sealed record CodeRecipe
{
    public required string Connection { get; init; }
    public required string Table { get; init; }
    public CodeRecipeKind Kind { get; init; }
    public DataSourceAccess Access { get; init; } = DataSourceAccess.NotPublic;
    public IReadOnlyList<string> ReadMemberGroups { get; init; } = [];
    public IReadOnlyList<string> ReadUserGroups { get; init; } = [];
    public IReadOnlyList<string> WriteMemberGroups { get; init; } = [];
    public IReadOnlyList<string> WriteUserGroups { get; init; } = [];
    /// <summary>Public only, with no write groups: visitors without an account may create, change and delete too.</summary>
    public bool AllowAnonymousWrites { get; init; }
    public required string ClassName { get; init; }
    public required string Alias { get; init; }
    public string Namespace { get; init; } = "SiteCode";
    /// <summary>The published data source a backing class is for, whose field aliases it then uses.</summary>
    public string? DataSource { get; init; }
}

/// <summary>A table or choice a recipe cannot be made from, with the reason the wizard shows.</summary>
public sealed class CodeRecipeException(string message) : InvalidOperationException(message);

/// <summary>
/// The wizard's recipes. Each writes ordinary C# for the C# folder, which the site compiles at run time: a form class that owns full CRUD
/// against one table through <see cref="IApplicationConnectionFactory"/>, with parameterized SQL and optional checksum checking,
/// or a backing class for a SQL data source on that table. Both check the caller in every operation, as the access choice says, using the
/// helpers the business rules guide documents. Unlike <see cref="FormSampleGenerator"/>, which writes an example nobody runs, this is code the
/// site will run once it is saved.
/// </summary>
public static partial class CodeRecipes
{
    private static readonly string[] Keywords = ["abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue",
        "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto",
        "if", "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch",
        "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while"];
    /// <summary>Names a generated record or class already has, or that its helpers use.</summary>
    private static readonly HashSet<string> Taken = new(["EqualityContract", "PrintMembers", "Equals", "GetHashCode", "ToString", "Deconstruct", "Clone", "GetType",
        "MemberwiseClone", "Finalize"], StringComparer.Ordinal);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$")]
    private static partial Regex Identifier();

    /// <summary>Why a recipe cannot be made as asked; empty when it can.</summary>
    public static IReadOnlyList<string> Validate(CodeRecipe recipe)
    {
        var errors = new List<string>();
        if (!IsIdentifier(recipe.ClassName)) errors.Add("The class name must be a C# name: a letter or '_', then letters, digits and '_', at most 64 characters.");
        if (recipe.Namespace.Split('.').Any(part => !IsIdentifier(part))) errors.Add("The namespace must be C# names separated by '.'.");
        if (!DataSourceDefinition.IsAlias(recipe.Alias)) errors.Add("The alias may use letters, digits, '.', '-' and '_', start with a letter and have at most 64 characters.");
        if (string.IsNullOrWhiteSpace(recipe.Connection)) errors.Add("Choose a connection.");
        if (string.IsNullOrWhiteSpace(recipe.Table) || TableName.Parse(recipe.Table) is null) errors.Add("Choose a table or view.");
        var groups = recipe.ReadMemberGroups.Concat(recipe.ReadUserGroups).Concat(recipe.WriteMemberGroups).Concat(recipe.WriteUserGroups).ToArray();
        if (groups.Any(group => string.IsNullOrWhiteSpace(group) || group.Length > 256 || group.Any(char.IsControl))) errors.Add("A group name is empty, too long or has a control character.");
        if (recipe.Access == DataSourceAccess.SpecificGroups && recipe.ReadMemberGroups.Count + recipe.ReadUserGroups.Count == 0)
            errors.Add("Specific groups needs at least one read group, a Member group or a User group.");
        if (recipe.AllowAnonymousWrites && (recipe.Access != DataSourceAccess.Public || recipe.WriteMemberGroups.Count + recipe.WriteUserGroups.Count > 0))
            errors.Add("Anonymous writes need Public access and no write groups.");
        return errors;
    }

    private static bool IsIdentifier(string name) => Identifier().IsMatch(name) && !Keywords.Contains(name);

    /// <summary>Where the wizard saves the class: <paramref name="folder"/>, then the class name.</summary>
    public static string FilePath(CodeRecipe recipe, string? folder)
        => (string.IsNullOrWhiteSpace(folder) ? "" : folder.Trim().Trim('/', '\\') + "/") + recipe.ClassName + ".cs";

    /// <summary>A suggested class name and alias for a table: <c>sales.ORDER_LINES</c> becomes OrderLinesForm and order-lines.</summary>
    public static (string ClassName, string Alias) Suggest(string table, CodeRecipeKind kind)
    {
        var name = TableName.Parse(table)?.Name ?? table;
        var words = Words(name);
        var pascal = string.Concat(words.Select(Capitalized));
        if (pascal.Length == 0 || char.IsDigit(pascal[0])) pascal = "Table" + pascal;
        var alias = string.Join('-', words.Select(w => w.ToLowerInvariant()));
        if (alias.Length == 0 || !char.IsAsciiLetter(alias[0])) alias = "table-" + alias;
        return (pascal + (kind == CodeRecipeKind.FormClass ? "Form" : "Rules"), alias[..Math.Min(alias.Length, kind == CodeRecipeKind.FormClass ? 64 : 58)] + (kind == CodeRecipeKind.FormClass ? "" : ".rules"));
    }

    private static string[] Words(string name)
        => Regex.Split(name, "[^A-Za-z0-9]+").Where(w => w.Length > 0)
            .SelectMany(w => w.All(c => !char.IsLower(c)) ? [w] : Regex.Split(w, "(?<=[a-z0-9])(?=[A-Z])")).Where(w => w.Length > 0).ToArray();

    private static string Capitalized(string word) => word.All(c => !char.IsLower(c))
        ? char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()
        : char.ToUpperInvariant(word[0]) + word[1..];

    /// <summary>A C# string literal of any text.</summary>
    private static string Literal(string value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    /// <summary>A constant holding SQL, as a raw string where it can be one, so the quoting reads as the database sees it.</summary>
    private static string SqlConstant(string name, string sql) => sql.Contains("\"\"\"", StringComparison.Ordinal)
        ? $"    private const string {name} = {Literal(sql)};\n"
        : $"    private const string {name} = \"\"\"\n        {sql}\n        \"\"\";\n";

    private static string Number(decimal value) => value.ToString(CultureInfo.InvariantCulture) + "m";

    private static string DialectName(DatabaseDialect dialect) => dialect switch {
        DatabaseDialect.Sqlite => "SQLite", DatabaseDialect.SqlServer => "SQL Server", DatabaseDialect.Postgres => "PostgreSQL",
        DatabaseDialect.Oracle => "Oracle 12+", DatabaseDialect.Oracle11g => "Oracle 11.2", _ => dialect.ToString(),
    };

    /// <summary>The access choice in words, for the generated file's header.</summary>
    private static string Describe(CodeRecipe recipe)
    {
        static string List(IReadOnlyList<string> groups) => string.Join(", ", groups.Select(g => "\"" + g + "\""));
        string Groups(IReadOnlyList<string> members, IReadOnlyList<string> users) => string.Join(" or ", new[] {
            members.Count > 0 ? "Member groups " + List(members) : null, users.Count > 0 ? "User groups " + List(users) : null }.OfType<string>());
        var read = recipe.Access switch {
            DataSourceAccess.Public => "anyone the page admits, with or without an account, may read",
            DataSourceAccess.NotPublic => "any signed-in Member or backoffice User may read",
            _ => "accounts in " + Groups(recipe.ReadMemberGroups.Concat(recipe.WriteMemberGroups).Distinct().ToArray(), recipe.ReadUserGroups.Concat(recipe.WriteUserGroups).Distinct().ToArray()) + " may read",
        };
        var write = recipe.WriteMemberGroups.Count + recipe.WriteUserGroups.Count > 0 ? "only readers in " + Groups(recipe.WriteMemberGroups, recipe.WriteUserGroups) + " may write"
            : recipe.AllowAnonymousWrites ? "anyone who may read may write, including visitors without an account"
            : "any signed-in reader may write";
        var name = recipe.Access switch { DataSourceAccess.Public => "Public", DataSourceAccess.NotPublic => "Not public", _ => "Specific groups" };
        return $"{name}: {read}; {write}.";
    }

    /// <summary>The access checks, as static members taking the caller from <c>IDataSourceUserResolver</c> or the operation.</summary>
    private static void AppendAccess(StringBuilder code, CodeRecipe recipe)
    {
        void Groups(string name, IReadOnlyList<string> groups)
        {
            if (groups.Count > 0) code.AppendLine($"    private static readonly string[] {name} = [{string.Join(", ", groups.Select(Literal))}];");
        }
        Groups("ReadMemberGroups", recipe.ReadMemberGroups);
        Groups("ReadUserGroups", recipe.ReadUserGroups);
        Groups("WriteMemberGroups", recipe.WriteMemberGroups);
        Groups("WriteUserGroups", recipe.WriteUserGroups);
        code.AppendLine("    // Member Groups are matched by name, backoffice User Groups by alias, both ignoring case; neither account type ever matches the other's groups.");
        var read = recipe.Access switch {
            DataSourceAccess.Public => "true",
            DataSourceAccess.NotPublic => "!user.IsAnonymous",
            _ => "!user.IsAnonymous && (" + string.Join(" || ", new[] {
                recipe.ReadMemberGroups.Count > 0 ? "ReadMemberGroups.Any(user.IsInMemberGroup)" : null,
                recipe.ReadUserGroups.Count > 0 ? "ReadUserGroups.Any(user.IsInUserGroup)" : null,
                recipe.WriteMemberGroups.Count > 0 ? "WriteMemberGroups.Any(user.IsInMemberGroup)" : null,
                recipe.WriteUserGroups.Count > 0 ? "WriteUserGroups.Any(user.IsInUserGroup)" : null }.OfType<string>()) + ")",
        };
        var writeGroups = string.Join(" || ", new[] {
            recipe.WriteMemberGroups.Count > 0 ? "WriteMemberGroups.Any(user.IsInMemberGroup)" : null,
            recipe.WriteUserGroups.Count > 0 ? "WriteUserGroups.Any(user.IsInUserGroup)" : null }.OfType<string>());
        if (writeGroups.Contains(" || ", StringComparison.Ordinal)) writeGroups = $"({writeGroups})";
        var write = string.Join(" && ", new[] { recipe.AllowAnonymousWrites ? null : "!user.IsAnonymous", "CanRead(user)", writeGroups.Length > 0 ? writeGroups : null }.OfType<string>());
        code.AppendLine($"    private static bool CanRead(DataSourceUser user) => {read};");
        code.AppendLine($"    private static bool CanWrite(DataSourceUser user) => {write};");
    }

    private static void AppendHeader(StringBuilder code, CodeRecipe recipe, TableSchema table, SqlDialect dialect, string what)
    {
        code.AppendLine($"// {what}, made by the AppBlocks C# wizard from {table.Name} on connection {recipe.Connection} ({DialectName(dialect.Kind)}).");
        code.AppendLine("// It is yours to change: AppBlocks compiles this folder at run time and never writes this file again.");
        code.AppendLine("// On a page with no Public Access an anonymous visitor reaches this class, and a class with no check is public.");
        code.AppendLine("// Every operation below therefore checks the caller first. Keep those checks when you change it.");
        code.AppendLine("// ClaimsPrincipal can represent an Umbraco Member or a backoffice User; the checks below say which accounts and groups count.");
        code.AppendLine("// Member and backoffice User identities, groups and business rules: Settings > AppBlocks > Business rules.");
        code.AppendLine("// Access: " + Describe(recipe));
    }

    private sealed record Column(TableColumn Source, int Index, string Alias, string Property, string Label, string Kind, bool ReadOnly, bool Required,
        int? MaxLength, decimal? Minimum, decimal? Maximum)
    {
        public string Type => Kind switch { FieldKinds.Number => "decimal?", FieldKinds.Checkbox => "bool?", FieldKinds.Date => "DateOnly?", FieldKinds.Time => "TimeOnly?", FieldKinds.DateTime => "DateTimeOffset?", _ => "string?" };
    }

    private static List<Column> Columns(TableSchema table, SqlDialect dialect, TableColumn key)
    {
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        var properties = new HashSet<string>(Taken, StringComparer.Ordinal);
        var columns = new List<Column>();
        foreach (var column in table.Columns)
        {
            if (dialect.KindOf(column.StoreType) is not { } kind || column.RowVersion) continue;
            var words = Words(column.Name);
            var pascal = string.Concat(words.Select(Capitalized));
            if (pascal.Length == 0 || char.IsDigit(pascal[0])) pascal = "Column" + pascal;
            pascal = pascal[..Math.Min(pascal.Length, 60)];
            var property = pascal; var suffix = 2;
            while (!properties.Add(property)) property = pascal + suffix++;
            var alias = char.ToLowerInvariant(property[0]) + property[1..];
            while (!aliases.Add(alias)) alias += "_";
            var label = words.Length == 0 ? column.Name : string.Join(' ', words.Select((w, i) => i == 0 ? Capitalized(w) : w.ToLowerInvariant()));
            var readOnly = column.Computed || column == key && column.AutoValue;
            var (min, max) = kind == FieldKinds.Number ? dialect.Range(column) : (null, null);
            columns.Add(new(column, columns.Count, alias, property, label, kind, readOnly, !readOnly && !column.Nullable && !column.HasDefault && !column.AutoValue,
                kind == FieldKinds.Text ? column.Length is > 0 and <= FieldKinds.MaxTextLength ? column.Length : FieldKinds.DefaultTextLength : null, min, max));
        }
        return columns;
    }

    /// <summary>The value a column takes from the input property <paramref name="value"/>, as the database's driver binds it.</summary>
    private static string ToDatabase(Column column, string value, DatabaseDialect dialect)
    {
        var type = column.Source.StoreType.ToLowerInvariant();
        var zoned = type.Contains("offset") || type.Contains("time zone") || type is "timestamptz" or "timetz";
        // A pattern variable named after the property, so two columns never declare the same one in a method.
        var named = char.ToLowerInvariant(column.Property[0]) + column.Property[1..] + "Value";
        return (column.Kind, dialect) switch {
            (FieldKinds.Checkbox, DatabaseDialect.Sqlite or DatabaseDialect.Oracle or DatabaseDialect.Oracle11g) => $"{value} is {{ }} {named} ? ({named} ? 1 : 0) : null",
            (FieldKinds.Number, DatabaseDialect.Postgres) => type switch {
                "int2" or "smallint" => $"(short?){value}", "int4" or "integer" => $"(int?){value}", "int8" or "bigint" => $"(long?){value}",
                "float4" or "real" => $"(float?){value}", "float8" or "double precision" => $"(double?){value}", _ => value },
            (FieldKinds.Text, DatabaseDialect.Postgres) when type == "uuid" => $"{value} is {{ }} {named} ? Guid.Parse({named}) : null",
            (FieldKinds.Date, DatabaseDialect.Sqlite) => $"{value}?.ToString(\"yyyy-MM-dd\", CultureInfo.InvariantCulture)",
            (FieldKinds.Date, DatabaseDialect.Postgres) => value,
            (FieldKinds.Date, _) => $"{value}?.ToDateTime(TimeOnly.MinValue)",
            (FieldKinds.Time, DatabaseDialect.Sqlite) => $"{value}?.ToString(\"HH:mm:ss\", CultureInfo.InvariantCulture)",
            (FieldKinds.Time, DatabaseDialect.Postgres) => value,
            (FieldKinds.Time, _) => $"{value}?.ToTimeSpan()",
            (FieldKinds.DateTime, DatabaseDialect.Sqlite) => $"{value}?.LocalDateTime.ToString(\"yyyy-MM-dd HH:mm:ss\", CultureInfo.InvariantCulture)",
            (FieldKinds.DateTime, DatabaseDialect.Postgres) when zoned => $"{value}?.ToUniversalTime()",
            (FieldKinds.DateTime, _) when zoned => value,
            (FieldKinds.DateTime, _) => $"Local({value})",
            _ => value,
        };
    }

    /// <summary>The key's text, as a record key, turned into the value its column compares with.</summary>
    private static string KeyParse(Column key, DatabaseDialect dialect)
    {
        var type = key.Source.StoreType.ToLowerInvariant();
        if (key.Kind == FieldKinds.Number)
            return dialect == DatabaseDialect.Postgres && type is "int2" or "smallint" or "int4" or "integer" or "int8" or "bigint"
                ? $"long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? {(type is "int8" or "bigint" ? "number" : type is "int2" or "smallint" ? "checked((short)number)" : "checked((int)number)")} : null"
                : "decimal.TryParse(key, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? number : null";
        if (dialect == DatabaseDialect.Postgres && type == "uuid") return "Guid.TryParse(key, out var id) ? id : null";
        return "key.Length > 0 ? key : null";
    }

    private static TableColumn Key(TableSchema table)
    {
        if (table.UniqueKeys.FirstOrDefault() is not [var name]) throw new CodeRecipeException($"{table.Name} has no one-column primary or unique key, which a form class needs to find one row. Choose another table, or use a SQL data source.");
        return table.Column(name) ?? throw new CodeRecipeException($"{table.Name} has no column {name}.");
    }

    /// <summary>A form class owning full CRUD against <paramref name="table"/>.</summary>
    public static string FormClass(CodeRecipe recipe, TableSchema table, SqlDialect dialect)
    {
        if (Validate(recipe) is [var first, ..]) throw new CodeRecipeException(first);
        var keyColumn = Key(table);
        var columns = Columns(table, dialect, keyColumn);
        var key = columns.FirstOrDefault(c => c.Source == keyColumn && c.Kind is FieldKinds.Number or FieldKinds.Text)
            ?? throw new CodeRecipeException($"The key column {keyColumn.Name} is not a number or text, which a form class's record key needs.");
        var inputs = columns.Where(c => !c.ReadOnly).ToList();
        var inserted = inputs;
        var updated = inputs.Where(c => c != key).ToList();
        var oracle = dialect.Kind is DatabaseDialect.Oracle or DatabaseDialect.Oracle11g;
        string Marker(string name) => (oracle ? ":" : "@") + name;
        var from = dialect.Quote(table.Name);
        var where = $" WHERE {dialect.Quote(key.Source.Name)} = {Marker("key")}";
        var select = "SELECT " + string.Join(", ", columns.Select(c => dialect.Quote(c.Source.Name))) + " FROM " + from;
        var selectSql = select + where;
        var lockSql = dialect.Kind switch {
            DatabaseDialect.SqlServer => select + " WITH (UPDLOCK, ROWLOCK)" + where,
            DatabaseDialect.Postgres or DatabaseDialect.Oracle or DatabaseDialect.Oracle11g => selectSql + " FOR UPDATE",
            _ => selectSql,
        };
        var keyName = dialect.Quote(key.Source.Name);
        var columnList = string.Join(", ", inserted.Select(c => dialect.Quote(c.Source.Name)));
        var values = string.Join(", ", inserted.Select((c, i) => Marker("p" + i)));
        var insertSql = (inserted.Count, dialect.Kind, key.ReadOnly) switch {
            (0, DatabaseDialect.Oracle or DatabaseDialect.Oracle11g, _) => $"INSERT INTO {from} ({keyName}) VALUES (DEFAULT) RETURNING {keyName} INTO :newKey",
            (0, DatabaseDialect.SqlServer, _) => $"INSERT INTO {from} OUTPUT INSERTED.{keyName} DEFAULT VALUES",
            (0, _, _) => $"INSERT INTO {from} DEFAULT VALUES RETURNING {keyName}",
            (_, _, false) => $"INSERT INTO {from} ({columnList}) VALUES ({values})",
            (_, DatabaseDialect.SqlServer, true) => $"INSERT INTO {from} ({columnList}) OUTPUT INSERTED.{keyName} VALUES ({values})",
            (_, DatabaseDialect.Oracle or DatabaseDialect.Oracle11g, true) => $"INSERT INTO {from} ({columnList}) VALUES ({values}) RETURNING {keyName} INTO :newKey",
            _ => $"INSERT INTO {from} ({columnList}) VALUES ({values}) RETURNING {keyName}",
        };
        var updateSql = updated.Count == 0 ? null
            : $"UPDATE {from} SET " + string.Join(", ", updated.Select((c, i) => $"{dialect.Quote(c.Source.Name)} = {Marker("p" + i)}")) + where;
        var deleteSql = $"DELETE FROM {from}" + where;
        var input = recipe.ClassName + "Input";

        var code = new StringBuilder();
        AppendHeader(code, recipe, table, dialect, "A form class");
        code.AppendLine($"// It reads and writes {table.Name} with bound parameters. Last save wins; set UseChecksum to true to detect conflicting edits.");
        code.AppendLine("// Pick it in a form's Form Interface by its alias.");
        code.AppendLine("""
            using System.ComponentModel.DataAnnotations;
            using System.Data.Common;
            using System.Globalization;
            using System.Security.Cryptography;
            using System.Text;
            using System.Text.Json;
            using System.Text.Json.Serialization;
            using AppBlocks.Connections;
            using AppBlocks.DataSources;
            using AppBlocks.Forms;

            """);
        code.AppendLine($"namespace {recipe.Namespace};");
        code.AppendLine();
        code.AppendLine($"/// <summary>What a form sends for one {table.Name} row: every field it may write, by field alias.</summary>");
        code.AppendLine($"public sealed record {input}");
        code.AppendLine("{");
        foreach (var column in inputs)
        {
            var attributes = new List<string> { $"JsonPropertyName({Literal(column.Alias)})" };
            if (column.Required) attributes.Add("Required");
            if (column.MaxLength is { } length) attributes.Add($"MaxLength({length})");
            if (column.Minimum is { } min && column.Maximum is { } max)
                attributes.Add($"Range(typeof(decimal), {Literal(min.ToString(CultureInfo.InvariantCulture))}, {Literal(max.ToString(CultureInfo.InvariantCulture))})");
            code.AppendLine($"    [{string.Join(", ", attributes)}]");
            code.AppendLine($"    public {column.Type} {column.Property} {{ get; init; }}");
        }
        code.AppendLine("}");
        code.AppendLine();
        code.AppendLine($"public sealed class {recipe.ClassName}(IApplicationConnectionFactory connections, IDataSourceUserResolver users, ILogger<{recipe.ClassName}> logger)");
        code.AppendLine($"    : FormResource<{input}, {input}>");
        code.AppendLine("{");
        code.AppendLine($"    private const string ConnectionName = {Literal(recipe.Connection)};");
        code.AppendLine("    private static readonly bool UseChecksum = false;");
        code.Append(SqlConstant("SelectSql", selectSql));
        code.Append(SqlConstant("LockSql", lockSql));
        code.Append(SqlConstant("InsertSql", insertSql));
        if (updateSql is not null) code.Append(SqlConstant("UpdateSql", updateSql));
        code.Append(SqlConstant("DeleteSql", deleteSql));
        code.AppendLine();
        code.AppendLine($"    public override string Alias => {Literal(recipe.Alias)};");
        code.AppendLine("    public override IReadOnlyList<FormField> Fields { get; } = [");
        foreach (var column in columns)
        {
            var arguments = new List<string> { Literal(column.Alias), Literal(column.Label), Literal(column.Kind) };
            if (column.Required) arguments.Add("Required: true");
            if (column.ReadOnly) arguments.Add("ReadOnly: true");
            if (column.MaxLength is { } length) arguments.Add($"MaxLength: {length}");
            if (column.Minimum is { } min) arguments.Add($"Minimum: {Number(min)}");
            if (column.Maximum is { } max) arguments.Add($"Maximum: {Number(max)}");
            if (column.Source.HasDefault && !column.ReadOnly) arguments.Add("HasServerDefault: true");
            code.AppendLine($"        new({string.Join(", ", arguments)}),");
        }
        code.AppendLine("    ];");
        code.AppendLine();
        // Only Member Groups can admit anyone under Specific groups with no User Groups, so AppBlocks may refuse everyone else first.
        var membersOnly = recipe.Access == DataSourceAccess.SpecificGroups && recipe.ReadUserGroups.Count == 0 && recipe.WriteUserGroups.Count == 0;
        code.AppendLine("    // True has AppBlocks refuse everyone but a signed-in Member before any method below runs: 401 with no account, 403 for a backoffice User alone.");
        code.AppendLine($"    public override bool RequireMember => {(membersOnly ? "true" : "false")};");
        code.AppendLine();
        AppendAccess(code, recipe);
        code.AppendLine();
        code.AppendLine("    public override async Task<FormResult> InitializeAsync(FormExecutionContext context, CancellationToken ct)");
        code.AppendLine("    {");
        code.AppendLine("        if (await CallerAsync(context, ct) is not { } user || !CanRead(user)) return FormResult.Denied();");
        code.AppendLine("        var values = Fields.ToDictionary(field => field.Key, field => (object?)null);");
        code.AppendLine("        return FormResult.Ok(new FormRecord(null, null, values, new FormCapabilities(Create: CanWrite(user))));");
        code.AppendLine("    }");
        code.AppendLine();
        code.AppendLine("    public override async Task<FormResult> LoadAsync(string key, FormExecutionContext context, CancellationToken ct)");
        code.AppendLine("    {");
        code.AppendLine("        if (await CallerAsync(context, ct) is not { } user || !CanRead(user)) return FormResult.Denied();");
        code.AppendLine("        if (KeyValue(key) is not { } id) return FormResult.Missing();");
        code.AppendLine("        return await RunAsync(async connection => await ReadAsync(connection, null, SelectSql, id, user, ct) is { } record ? FormResult.Ok(record) : FormResult.Missing(), ct);");
        code.AppendLine("    }");
        code.AppendLine();
        code.AppendLine($"    protected override async Task<FormResult> CreateAsync({input} input, FormExecutionContext context, CancellationToken ct)");
        code.AppendLine("    {");
        code.AppendLine("        if (await CallerAsync(context, ct) is not { } user || !CanWrite(user)) return FormResult.Denied();");
        code.AppendLine("        return await RunAsync(async connection => {");
        code.AppendLine("            await using var command = Command(connection, null, InsertSql);");
        for (var i = 0; i < inserted.Count; i++) code.AppendLine($"            Add(command, \"p{i}\", {ToDatabase(inserted[i], "input." + inserted[i].Property, dialect.Kind)});");
        if (!key.ReadOnly)
        {
            code.AppendLine("            await command.ExecuteNonQueryAsync(ct);");
            code.AppendLine($"            var id = KeyValue(Convert.ToString(input.{key.Property}, CultureInfo.InvariantCulture) ?? \"\");");
        }
        else if (oracle)
        {
            code.AppendLine("            var created = command.CreateParameter();");
            code.AppendLine("            created.ParameterName = \"newKey\";");
            code.AppendLine("            created.Direction = System.Data.ParameterDirection.Output;");
            code.AppendLine(key.Kind == FieldKinds.Number ? "            created.DbType = System.Data.DbType.Decimal;" : "            created.DbType = System.Data.DbType.String;\n            created.Size = 4000;");
            code.AppendLine("            command.Parameters.Add(created);");
            code.AppendLine("            await command.ExecuteNonQueryAsync(ct);");
            code.AppendLine("            var id = KeyValue(Convert.ToString(created.Value, CultureInfo.InvariantCulture) ?? \"\");");
        }
        else code.AppendLine("            var id = KeyValue(Convert.ToString(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) ?? \"\");");
        code.AppendLine("            return id is not null && await ReadAsync(connection, null, SelectSql, id, user, ct) is { } record ? FormResult.Ok(record) : FormResult.Missing();");
        code.AppendLine("        }, ct);");
        code.AppendLine("    }");
        code.AppendLine();
        code.AppendLine($"    protected override async Task<FormResult> UpdateAsync(string key, {input} input, string? checksum, FormExecutionContext context, CancellationToken ct)");
        code.AppendLine("    {");
        code.AppendLine("        if (await CallerAsync(context, ct) is not { } user || !CanWrite(user)) return FormResult.Denied();");
        code.AppendLine("        if (KeyValue(key) is not { } id) return FormResult.Missing();");
        if (!key.ReadOnly)
        {
            code.AppendLine($"        if (input.{key.Property} is not null && !Equals(KeyValue(Convert.ToString(input.{key.Property}, CultureInfo.InvariantCulture) ?? \"\"), id))");
            code.AppendLine($"            return FormResult.Invalid({Literal(key.Alias)}, \"A record's key cannot change.\");");
        }
        code.AppendLine("        return await RunAsync(async connection => {");
        code.AppendLine("            await using var transaction = await connection.BeginTransactionAsync(ct);");
        code.AppendLine("            // Keep the read and write together, including the checksum check when enabled.");
        code.AppendLine("            if (await ReadAsync(connection, transaction, LockSql, id, user, ct) is not { } current) return FormResult.Missing();");
        code.AppendLine("            if (current.Checksum is { } expected && expected != checksum) return FormResult.Conflict();");
        if (updateSql is not null)
        {
            code.AppendLine("            await using (var command = Command(connection, transaction, UpdateSql))");
            code.AppendLine("            {");
            for (var i = 0; i < updated.Count; i++) code.AppendLine($"                Add(command, \"p{i}\", {ToDatabase(updated[i], "input." + updated[i].Property, dialect.Kind)});");
            code.AppendLine("                Add(command, \"key\", id);");
            code.AppendLine("                await command.ExecuteNonQueryAsync(ct);");
            code.AppendLine("            }");
        }
        code.AppendLine("            await transaction.CommitAsync(ct);");
        code.AppendLine("            return await ReadAsync(connection, null, SelectSql, id, user, ct) is { } record ? FormResult.Ok(record) : FormResult.Missing();");
        code.AppendLine("        }, ct);");
        code.AppendLine("    }");
        code.AppendLine();
        code.AppendLine("    public override async Task<FormResult> DeleteAsync(string key, string? checksum, FormExecutionContext context, CancellationToken ct)");
        code.AppendLine("    {");
        code.AppendLine("        if (await CallerAsync(context, ct) is not { } user || !CanWrite(user)) return FormResult.Denied();");
        code.AppendLine("        if (KeyValue(key) is not { } id) return FormResult.Missing();");
        code.AppendLine("        return await RunAsync(async connection => {");
        code.AppendLine("            await using var transaction = await connection.BeginTransactionAsync(ct);");
        code.AppendLine("            if (await ReadAsync(connection, transaction, LockSql, id, user, ct) is not { } current) return FormResult.Missing();");
        code.AppendLine("            if (current.Checksum is { } expected && expected != checksum) return FormResult.Conflict();");
        code.AppendLine("            await using (var command = Command(connection, transaction, DeleteSql))");
        code.AppendLine("            {");
        code.AppendLine("                Add(command, \"key\", id);");
        code.AppendLine("                await command.ExecuteNonQueryAsync(ct);");
        code.AppendLine("            }");
        code.AppendLine("            await transaction.CommitAsync(ct);");
        code.AppendLine("            return new FormResult(204);");
        code.AppendLine("        }, ct);");
        code.AppendLine("    }");
        code.AppendLine();
        code.AppendLine("    /// <summary>The caller: a resolved account, the anonymous visitor, or null for an account that no longer resolves, which never counts as anonymous.</summary>");
        code.AppendLine("    private async Task<DataSourceUser?> CallerAsync(FormExecutionContext context, CancellationToken ct)");
        code.AppendLine("    {");
        code.AppendLine("        if (await users.ResolveAsync(context, ct) is { IsAnonymous: false } user) return user;");
        code.AppendLine("        return context.Member.Identities.Any(identity => identity.IsAuthenticated) ? null : DataSourceUser.Anonymous;");
        code.AppendLine("    }");
        code.AppendLine();
        code.AppendLine("    private static FormCapabilities Capabilities(DataSourceUser user) => new(Create: CanWrite(user), Update: CanWrite(user), Delete: CanWrite(user));");
        code.AppendLine();
        code.AppendLine($"    private static object? KeyValue(string key) => {KeyParse(key, dialect.Kind)};");
        code.AppendLine();
        code.AppendLine("    /// <summary>Opens the connection, runs the work, and turns a database refusal into a message instead of an error page.</summary>");
        code.AppendLine("    private async Task<FormResult> RunAsync(Func<DbConnection, Task<FormResult>> work, CancellationToken ct)");
        code.AppendLine("    {");
        code.AppendLine("        try");
        code.AppendLine("        {");
        code.AppendLine("            await using var connection = connections.CreateConnection(ConnectionName);");
        code.AppendLine("            await connection.OpenAsync(ct);");
        code.AppendLine("            return await work(connection);");
        code.AppendLine("        }");
        code.AppendLine("        catch (DbException error)");
        code.AppendLine("        {");
        code.AppendLine($"            logger.LogWarning(error, \"{recipe.ClassName}: the database refused a statement.\");");
        code.AppendLine("            return FormResult.Invalid(\"\", \"The database refused this change. Check the values and try again.\");");
        code.AppendLine("        }");
        code.AppendLine("    }");
        code.AppendLine();
        code.AppendLine("    private static DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql)");
        code.AppendLine("    {");
        code.AppendLine("        var command = connection.CreateCommand();");
        code.AppendLine("        command.CommandText = sql;");
        code.AppendLine("        command.Transaction = transaction;");
        if (oracle)
        {
            code.AppendLine("        // Oracle binds by position unless told otherwise.");
            code.AppendLine("        command.GetType().GetProperty(\"BindByName\")?.SetValue(command, true);");
        }
        code.AppendLine("        return command;");
        code.AppendLine("    }");
        code.AppendLine();
        code.AppendLine("    private static void Add(DbCommand command, string name, object? value)");
        code.AppendLine("    {");
        code.AppendLine("        var parameter = command.CreateParameter();");
        code.AppendLine(oracle ? "        parameter.ParameterName = name;" : "        parameter.ParameterName = \"@\" + name;");
        code.AppendLine("        parameter.Value = value ?? DBNull.Value;");
        code.AppendLine("        command.Parameters.Add(parameter);");
        code.AppendLine("    }");
        if (columns.Any(c => c.Kind == FieldKinds.DateTime))
        {
            code.AppendLine();
            code.AppendLine("    /// <summary>A date and time for a column without a time zone: the web server's local time, as AppBlocks stores them.</summary>");
            code.AppendLine("    private static DateTime? Local(DateTimeOffset? value) => value is { } moment ? DateTime.SpecifyKind(moment.LocalDateTime, DateTimeKind.Unspecified) : null;");
        }
        code.AppendLine();
        code.AppendLine("    private static async Task<FormRecord?> ReadAsync(DbConnection connection, DbTransaction? transaction, string sql, object id, DataSourceUser user, CancellationToken ct)");
        code.AppendLine("    {");
        code.AppendLine("        await using var command = Command(connection, transaction, sql);");
        code.AppendLine("        Add(command, \"key\", id);");
        code.AppendLine("        await using var reader = await command.ExecuteReaderAsync(ct);");
        code.AppendLine("        if (!await reader.ReadAsync(ct)) return null;");
        code.AppendLine("        var values = new Dictionary<string, object?>();");
        foreach (var column in columns)
            code.AppendLine($"        values[{Literal(column.Alias)}] = reader.IsDBNull({column.Index}) ? null : Show(reader.GetValue({column.Index}), {Literal(column.Kind)});");
        code.AppendLine($"        return new FormRecord(Convert.ToString(values[{Literal(key.Alias)}], CultureInfo.InvariantCulture), UseChecksum ? Checksum(values) : null, values, Capabilities(user));");
        code.AppendLine("    }");
        code.AppendLine();
        code.AppendLine("    /// <summary>A database value in the form's JSON shape: text, a number, true or false, or ISO dates and times.</summary>");
        code.AppendLine("    private static object? Show(object value, string kind) => kind switch {");
        code.AppendLine("        \"number\" => Convert.ToDecimal(value, CultureInfo.InvariantCulture),");
        code.AppendLine("        \"checkbox\" => value is bool flag ? flag : value is string text ? text is \"1\" or \"true\" or \"True\" : Convert.ToDecimal(value, CultureInfo.InvariantCulture) != 0,");
        code.AppendLine("        \"date\" => value switch {");
        code.AppendLine("            DateTime moment => moment.ToString(\"yyyy-MM-dd\", CultureInfo.InvariantCulture),");
        code.AppendLine("            DateOnly date => date.ToString(\"yyyy-MM-dd\", CultureInfo.InvariantCulture),");
        code.AppendLine("            _ => DateTime.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture).ToString(\"yyyy-MM-dd\", CultureInfo.InvariantCulture),");
        code.AppendLine("        },");
        code.AppendLine("        \"time\" => value switch {");
        code.AppendLine("            TimeSpan span => TimeOnly.FromTimeSpan(span).ToString(\"HH:mm:ss\", CultureInfo.InvariantCulture),");
        code.AppendLine("            TimeOnly time => time.ToString(\"HH:mm:ss\", CultureInfo.InvariantCulture),");
        code.AppendLine("            DateTime moment => moment.ToString(\"HH:mm:ss\", CultureInfo.InvariantCulture),");
        code.AppendLine("            _ => TimeOnly.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture).ToString(\"HH:mm:ss\", CultureInfo.InvariantCulture),");
        code.AppendLine("        },");
        code.AppendLine("        \"datetime\" => (value switch {");
        code.AppendLine("            DateTimeOffset moment => moment,");
        code.AppendLine("            DateTime moment => new DateTimeOffset(DateTime.SpecifyKind(moment, moment.Kind == DateTimeKind.Utc ? DateTimeKind.Utc : DateTimeKind.Local)),");
        code.AppendLine("            _ => DateTimeOffset.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal),");
        code.AppendLine("        }).ToString(\"yyyy-MM-dd'T'HH:mm:sszzz\", CultureInfo.InvariantCulture),");
        code.AppendLine("        _ => Convert.ToString(value, CultureInfo.InvariantCulture),");
        code.AppendLine("    };");
        code.AppendLine();
        code.AppendLine("    /// <summary>A checksum of the record's values as read: a change saved by someone else since gives a different one, and the save is refused.</summary>");
        code.AppendLine("    private static string Checksum(IReadOnlyDictionary<string, object?> values)");
        code.AppendLine("        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values.OrderBy(pair => pair.Key, StringComparer.Ordinal)))))[..16];");
        code.AppendLine("}");
        return Lines(code);
    }

    /// <summary>The file with one line ending throughout, whatever the platform and this source file use.</summary>
    private static string Lines(StringBuilder code) => code.ToString().ReplaceLineEndings("\n");

    /// <summary>A backing class for a SQL data source on <paramref name="table"/>, checking the caller in every hook.</summary>
    public static string BackingClass(CodeRecipe recipe, TableSchema table, SqlDialect dialect, IReadOnlyList<DataSourceFieldColumn>? fields = null)
    {
        if (Validate(recipe) is [var first, ..]) throw new CodeRecipeException(first);
        var listed = fields ?? table.Columns.Where(c => dialect.KindOf(c.StoreType) is not null && !c.RowVersion)
            .Select(c => new DataSourceFieldColumn("", c.Name, dialect.KindOf(c.StoreType)!)).ToArray();
        var code = new StringBuilder();
        AppendHeader(code, recipe, table, dialect, "A backing class");
        code.AppendLine(recipe.DataSource is { Length: > 0 } source
            ? $"// Pick it in Backing class on the Advanced tab of the data source {source}."
            : "// Pick it in Backing class on the Advanced tab of a SQL data source on this table.");
        code.AppendLine("// The data source checks its own Access, groups and allow flags before it asks this class, so these checks can only narrow");
        code.AppendLine("// what it allows, never widen it. Every IDataSourcePolicy and the row restriction still apply after it.");
        code.AppendLine("""
            using AppBlocks.DataSources;

            """);
        code.AppendLine($"namespace {recipe.Namespace};");
        code.AppendLine();
        code.AppendLine("/// <remarks>");
        code.AppendLine(fields is null ? $"/// The columns of {table.Name}; Values holds them by the data source's field aliases." : $"/// The data source's fields, by alias, with the column of {table.Name} behind each:");
        foreach (var field in listed) code.AppendLine($"/// {(field.Field.Length > 0 ? field.Field + ": " : "")}{field.Column} ({field.Kind})");
        code.AppendLine("/// </remarks>");
        code.AppendLine($"public sealed class {recipe.ClassName}(ILogger<{recipe.ClassName}> logger) : IDataSourceHandler");
        code.AppendLine("{");
        code.AppendLine($"    public string Alias => {Literal(recipe.Alias)};");
        code.AppendLine();
        AppendAccess(code, recipe);
        code.AppendLine();
        code.AppendLine("    public ValueTask<DataSourceOutcome> InitializeAsync(DataSourceOperation operation, CancellationToken ct) => Read(operation);");
        code.AppendLine("    public ValueTask<DataSourceOutcome> LoadAsync(DataSourceOperation operation, CancellationToken ct) => Read(operation);");
        code.AppendLine("    public ValueTask<DataSourceOutcome> ListAsync(DataSourceOperation operation, CancellationToken ct) => Read(operation);");
        code.AppendLine("    public ValueTask<DataSourceOutcome> QueryAsync(DataSourceOperation operation, CancellationToken ct) => Read(operation);");
        code.AppendLine();
        var example = listed.FirstOrDefault(f => f.Kind == FieldKinds.Text && f.Field.Length > 0);
        code.AppendLine("    public ValueTask<DataSourceOutcome> CreateAsync(DataSourceOperation operation, CancellationToken ct)");
        code.AppendLine("    {");
        code.AppendLine("        if (!CanWrite(operation.User)) return Refuse(operation);");
        code.AppendLine("        // operation.Values holds what is about to be written, by field alias; change an entry to change what is written, or refuse:");
        code.AppendLine(example is null
            ? "        // return ValueTask.FromResult(DataSourceOutcome.Invalid(\"field\", \"Explain what to change.\"));"
            : $"        // if (operation.Values![{Literal(example.Field)}] is not string {{ Length: > 0 }}) return ValueTask.FromResult(DataSourceOutcome.Invalid({Literal(example.Field)}, \"Enter a value.\"));");
        code.AppendLine("        return ValueTask.FromResult(DataSourceOutcome.Continue);");
        code.AppendLine("    }");
        code.AppendLine();
        code.AppendLine("    public ValueTask<DataSourceOutcome> UpdateAsync(DataSourceOperation operation, CancellationToken ct)");
        code.AppendLine("    {");
        code.AppendLine("        if (!CanWrite(operation.User)) return Refuse(operation);");
        code.AppendLine("        // operation.Current is the row as stored, locked; operation.Values the whole row as it would be stored.");
        code.AppendLine("        return ValueTask.FromResult(DataSourceOutcome.Continue);");
        code.AppendLine("    }");
        code.AppendLine();
        code.AppendLine("    public ValueTask<DataSourceOutcome> DeleteAsync(DataSourceOperation operation, CancellationToken ct)");
        code.AppendLine("        => CanWrite(operation.User) ? ValueTask.FromResult(DataSourceOutcome.Continue) : Refuse(operation);");
        code.AppendLine();
        code.AppendLine("    private ValueTask<DataSourceOutcome> Read(DataSourceOperation operation)");
        code.AppendLine("        => CanRead(operation.User) ? ValueTask.FromResult(DataSourceOutcome.Continue) : Refuse(operation);");
        code.AppendLine();
        code.AppendLine("    private ValueTask<DataSourceOutcome> Refuse(DataSourceOperation operation)");
        code.AppendLine("    {");
        code.AppendLine($"        logger.LogInformation(\"{recipe.ClassName} refused {{Action}} on {{DataSource}}.\", operation.Action, operation.DataSource);");
        code.AppendLine("        return ValueTask.FromResult(DataSourceOutcome.Refuse(403));");
        code.AppendLine("    }");
        code.AppendLine("}");
        return Lines(code);
    }
}
