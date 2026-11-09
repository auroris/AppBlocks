using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using AppBlocks.Connections;
using AppBlocks.Forms;

namespace AppBlocks.DataSources;

/// <summary>A column of the row source as a form field: its alias and presentation, its rules, and the table column it writes.</summary>
/// <param name="Column">The row source's name for it, which reads use.</param>
/// <param name="Target">The column of the written table, whose catalog name writes use; null for a column nothing writes.</param>
/// <param name="Required">Whether the form must supply a value: a writable NOT NULL column that neither the database nor an insert default fills, unless the mapping says otherwise.</param>
internal sealed record PlanColumn(string Field, string Column, string Label, string Kind, bool ReadOnly, bool Required, bool HasServerDefault,
    int? MaxLength, decimal? Minimum, decimal? Maximum, IReadOnlyList<FormChoice>? Choices, TableColumn? Target)
{
    /// <summary>
    /// Whether the column must always hold a value: a writable NOT NULL column, unless the mapping says otherwise. An insert leaves a blank
    /// one to its insert default or the database's default, and an update refuses a blank for one.
    /// </summary>
    public bool NeedsValue { get; init; } = Required;

    public FormField ToField() => new(Field, Label, Kind, Required, ReadOnly, MaxLength, Minimum, Maximum, Choices, HasServerDefault);
}

internal sealed record InsertValue(PlanColumn Column, BindVariable? Variable, object? Literal);

/// <summary>
/// Everything the engine needs to run one definition, worked out once from the definition and the schema it reads:
/// the fields, the table writes go to, the key, the lost-update check and the parsed SQL. Plans are cached by the
/// definition's fingerprint, and the contract version changes whenever the definition or the schema does.
/// </summary>
internal sealed class DataSourcePlan
{
    public required DataSourceDefinition Definition { get; init; }
    public required SqlDialect Dialect { get; init; }
    /// <summary>The timeout of every command the plan runs (<see cref="DataSourceOptions.CommandTimeoutSeconds"/>).</summary>
    public int CommandTimeout { get; init; } = DataSourceOptions.DefaultCommandTimeoutSeconds;
    public TableName? Table { get; init; }
    public SqlTemplate? Query { get; init; }
    public required IReadOnlyList<PlanColumn> Columns { get; init; }
    public required IReadOnlyList<PlanColumn> Keys { get; init; }
    public TableSchema? Target { get; init; }
    public SqlTemplate? Restriction { get; init; }
    public required IReadOnlyList<InsertValue> Defaults { get; init; }
    public PlanColumn? CreatedBy { get; init; }
    public PlanColumn? CreatedAt { get; init; }
    public PlanColumn? UpdatedBy { get; init; }
    public PlanColumn? UpdatedAt { get; init; }
    public SqlTemplate? InsertStatement { get; init; }
    public SqlTemplate? UpdateStatement { get; init; }
    public SqlTemplate? DeleteStatement { get; init; }
    public required IReadOnlyList<FormField> Fields { get; init; }
    public required string ContractVersion { get; init; }

    /// <summary>Whether writes have a table or view to go to and a key in it; the runtime refuses a write that does not change exactly one row.</summary>
    public bool Writable => Target is not null && Keys.Count > 0 && Keys.All(k => k.Target is not null);
    /// <summary>Whether reading this source binds anything about the caller, so it means nothing without an account.</summary>
    public bool BindsUser => new[] { Query, Restriction }.OfType<SqlTemplate>().SelectMany(template => template.Variables)
        .Any(variable => variable.Kind is BindVariableKind.UserSid or BindVariableKind.UserName or BindVariableKind.UserKey or BindVariableKind.UserEmail or BindVariableKind.UserGroups);
    /// <summary>Identity-dependent defaults and write statements cannot run for an anonymous visitor.</summary>
    public bool WritesBindUser => new[] { InsertStatement, UpdateStatement, DeleteStatement }.OfType<SqlTemplate>()
        .SelectMany(template => template.Variables).Concat(Defaults.Select(value => value.Variable).OfType<BindVariable>())
        .Any(variable => variable.Kind is BindVariableKind.UserSid or BindVariableKind.UserName or BindVariableKind.UserKey or BindVariableKind.UserEmail or BindVariableKind.UserGroups);
    /// <summary>The column of a field alias in any letter case, as every other alias lookup; the planner refuses aliases that differ only in case.</summary>
    public PlanColumn? Field(string alias) => Field(Columns, alias);
    internal static PlanColumn? Field(IEnumerable<PlanColumn> columns, string alias) => columns.FirstOrDefault(c => c.Field.Equals(alias, StringComparison.OrdinalIgnoreCase));
    public IEnumerable<string> ReadColumns => Columns.Select(c => c.Column);
    public string From(SqlParameters parameters, Func<BindVariable, object?> value) => Dialect.From(Table, Query?.Render(parameters, value));
    /// <summary>The parameters of a new command of this plan, which also carry its timeout.</summary>
    public SqlParameters Parameters() => new(Dialect, CommandTimeout);
    public bool IsAudit(PlanColumn column) => column == CreatedBy || column == CreatedAt || column == UpdatedBy || column == UpdatedAt;
}

/// <summary>
/// Builds a <see cref="DataSourcePlan"/> by reading the schema: the table's catalog entry, or the SELECT's result
/// columns from a zero-row execution, and the catalog entry of the table the key columns come from. Every name the
/// definition uses is checked against what the schema reports, and statements later use the schema's spelling.
/// </summary>
internal static class DataSourcePlanner
{
    public const int MaxColumns = 200;

    /// <param name="compileStatements">Also compile the custom statements without running them; publishing does, requests do not.</param>
    /// <param name="warnings">Receives what publishing reports without refusing, such as a view written through or a statement that cannot be compiled.</param>
    /// <param name="commandTimeout">The timeout of the catalog reads and probes, and of every command the plan runs later.</param>
    public static DataSourcePlan? Build(DataSourceDefinition definition, SqlDialect dialect, DbConnection connection, ICollection<string> errors, bool compileStatements = false,
        ICollection<string>? warnings = null, int commandTimeout = DataSourceOptions.DefaultCommandTimeoutSeconds)
    {
        var start = errors.Count;
        var d = definition;
        if (!Enum.IsDefined(d.Concurrency)) errors.Add("Select a valid concurrency mode.");
        TableName? table = null;
        SqlTemplate? query = null;
        if (SqlTemplate.IsQuery(d.Source)) query = SqlTemplate.Parse(d.Source, SqlUse.RowSource, "The row source", errors);
        else if ((table = TableName.Parse(d.Source)) is null)
            errors.Add("Enter the row source as a table name, such as ORDERS or dbo.ORDERS, or as a SELECT statement.");
        var restriction = d.RowRestriction is { } predicate ? SqlTemplate.Parse(predicate, SqlUse.RowRestriction, "The row restriction", errors) : null;
        var insert = Statement(d.CustomStatements.Insert, "insert");
        var update = Statement(d.CustomStatements.Update, "update");
        var delete = Statement(d.CustomStatements.Delete, "delete");
        if (errors.Count > start) return null;

        TableSchema? sourceTable = null;
        if (table is not null)
        {
            sourceTable = dialect.ReadTable(connection, table, commandTimeout);
            if (sourceTable is null) { errors.Add($"The table {table} was not found through connection {d.Connection}."); return null; }
            table = sourceTable.Name;
        }
        IReadOnlyList<SourceColumn> probed;
        try
        {
            var parameters = new SqlParameters(dialect, commandTimeout);
            probed = dialect.Probe(connection, dialect.ProbeText(dialect.From(table, query?.Render(parameters, Nothing))), parameters);
        }
        catch (DbException error) { errors.Add("The row source could not be read: " + error.Message); return null; }
        if (probed.Count == 0) { errors.Add("The row source has no columns."); return null; }
        if (probed.Count > MaxColumns) { errors.Add($"The row source has more than {MaxColumns} columns; select the ones the forms need."); return null; }
        foreach (var duplicate in probed.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            errors.Add($"The row source has two columns named {duplicate.Key}; give one an alias.");
        if (restriction is not null)
        {
            try
            {
                var parameters = new SqlParameters(dialect, commandTimeout);
                var from = dialect.From(table, query?.Render(parameters, Nothing));
                dialect.Probe(connection, dialect.ProbeText(from, restriction.Render(parameters, Nothing)), parameters);
            }
            catch (DbException error) { errors.Add("The row restriction could not be applied: " + error.Message); }
        }
        if (errors.Count > start) return null;

        SourceColumn? Find(string name) => probed.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        SourceColumn? Named(string name, string role)
        {
            var column = Find(name);
            if (column is null) errors.Add($"The {role} {name} is not a column of the row source.");
            return column;
        }

        // Writes go to the table itself, or for a SELECT to the one table its key columns come from.
        var keySources = d.KeyColumns.Select(k => Named(k, "key column")).ToArray();
        var target = sourceTable;
        if (query is not null && keySources.Length > 0 && keySources.All(k => k?.BaseTable is not null))
        {
            var bases = keySources.Select(k => (Schema: k!.BaseSchema?.ToLowerInvariant(), Table: k.BaseTable!.ToLowerInvariant())).Distinct().ToArray();
            if (bases.Length == 1)
                target = dialect.ReadTable(connection, new TableName(keySources[0]!.BaseSchema, keySources[0]!.BaseTable!) { SchemaIsQuoted = true, NameIsQuoted = true }, commandTimeout);
        }
        if (d.KeySequences.Count > 0)
        {
            var sequences = new Dictionary<string, TableName>(StringComparer.OrdinalIgnoreCase);
            foreach (var (columnName, sequenceName) in d.KeySequences)
            {
                var source = Find(columnName);
                var targetName = query is null ? source?.Name : source?.BaseColumn;
                var column = targetName is null ? null : target?.Column(targetName);
                if (column is null || !keySources.Contains(source)) { errors.Add($"Key sequence {columnName} must name a key column of the written table."); continue; }
                if (dialect.Kind is not (DatabaseDialect.Oracle or DatabaseDialect.Oracle11g))
                { errors.Add("Key sequences are Oracle settings; this database supplies identity or sequence defaults on its columns."); continue; }
                var sequence = TableName.Parse(sequenceName) is { } name ? dialect.ReadSequence(connection, name, commandTimeout) : null;
                if (sequence is null) { errors.Add($"The sequence {sequenceName} was not found through connection {d.Connection}."); continue; }
                sequences[column.Name] = sequence;
            }
            if (target is not null) target = target with { Columns = target.Columns.Select(c => sequences.TryGetValue(c.Name, out var sequence)
                ? c with { Sequence = sequence, AutoValue = true, HasDefault = true } : c).ToArray() };
        }
        TableColumn? TargetOf(SourceColumn column)
        {
            if (target is null) return null;
            if (query is null) return target.Column(column.Name);
            if (column.BaseTable is null || column.BaseColumn is null || !column.BaseTable.Equals(target.Name.Name, StringComparison.OrdinalIgnoreCase)) return null;
            if (column.BaseSchema is not null && target.Name.Schema is not null && !column.BaseSchema.Equals(target.Name.Schema, StringComparison.OrdinalIgnoreCase)) return null;
            // A same-named table in another database is not the table written here.
            if (column.BaseCatalog is not null && connection.Database.Length > 0 && !column.BaseCatalog.Equals(connection.Database, StringComparison.OrdinalIgnoreCase)) return null;
            return target.Column(column.BaseColumn);
        }

        var mapped = new Dictionary<SourceColumn, FieldMapping>();
        foreach (var mapping in d.Mapping)
        {
            var column = mapping.Column is not null ? Find(mapping.Column) : probed.FirstOrDefault(c => Normalize(c.Name) == Normalize(mapping.Alias));
            if (column is null)
                errors.Add(mapping.Column is not null ? $"The mapping names column {mapping.Column}, which the row source does not have."
                    : $"No column of the row source matches field {mapping.Alias}; name its column in the mapping.");
            else if (!mapped.TryAdd(column, mapping)) errors.Add($"The mapping maps two fields to column {column.Name}.");
        }
        var audit = new Dictionary<string, SourceColumn>();
        foreach (var (role, name) in new[] { ("createdBy", d.AuditColumns.CreatedBy), ("createdAt", d.AuditColumns.CreatedAt), ("updatedBy", d.AuditColumns.UpdatedBy), ("updatedAt", d.AuditColumns.UpdatedAt) })
            if (name is not null && Named(name, "audit column") is { } column) audit[role] = column;
        var defaulted = d.InsertDefaults.Select(x => Named(x.Column, "insert default column")).OfType<SourceColumn>().ToHashSet();
        var anyWrites = d.AllowInsert || d.AllowUpdate;

        var columns = new List<PlanColumn>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in probed)
        {
            var targetColumn = TargetOf(column);
            mapped.TryGetValue(column, out var mapping);
            var kind = mapping?.Kind ?? (targetColumn is not null ? dialect.KindOf(targetColumn.StoreType) : dialect.ExpressionKind(column));
            if (kind is null)
            {
                // Binary data has no field; the column is left out of reads and writes alike.
                if (mapping is not null || keySources.Contains(column)) errors.Add($"Column {column.Name} holds binary data, which a form field cannot show.");
                continue;
            }
            var alias = mapping?.Alias ?? FieldAlias(column.Name);
            if (!DataSourceDefinition.IsFieldAlias(alias)) { errors.Add($"Column {column.Name} needs a field alias in the mapping: '{alias}' is not one."); continue; }
            var isAudit = audit.ContainsValue(column);
            var generated = targetColumn is null || targetColumn.Computed || targetColumn.RowVersion || targetColumn.AutoValue;
            // A SELECT that returns one table column twice writes it through the first field only. A column it
            // renames may come from a second copy of the table (a self-join: p.NAME AS PARENT_NAME), which the
            // metadata cannot tell apart, so it is written only when the mapping says "readOnly": false.
            var repeated = targetColumn is not null && !claimed.Add(targetColumn.Name);
            var renamed = query is not null && column.BaseColumn is not null && !column.BaseColumn.Equals(column.Name, StringComparison.OrdinalIgnoreCase)
                && mapping?.ReadOnly != false;
            var readOnly = !anyWrites || generated || repeated || renamed || isAudit || mapping?.ReadOnly == true;
            var serverDefault = targetColumn is { HasDefault: true } or { AutoValue: true } or { Computed: true } || isAudit || defaulted.Contains(column);
            // A NOT NULL column must always hold a value; the form must supply it only when neither the database nor an insert default can.
            var needsValue = !readOnly && (mapping?.Required ?? targetColumn is { Nullable: false });
            var required = !readOnly && (mapping?.Required ?? (targetColumn is { Nullable: false } && !serverDefault));
            var range = targetColumn is not null && kind == FieldKinds.Number ? dialect.Range(targetColumn) : (null, null);
            int? maxLength = kind != FieldKinds.Text ? null
                : Math.Min(mapping?.MaxLength ?? (targetColumn is not null ? targetColumn.Length : column.Size) ?? FieldKinds.DefaultTextLength, FieldKinds.MaxTextLength);
            var minimum = kind == FieldKinds.Number ? mapping?.Minimum ?? range.Min : null;
            var maximum = kind == FieldKinds.Number ? mapping?.Maximum ?? range.Max : null;
            columns.Add(new PlanColumn(alias, column.Name, mapping?.Label ?? Label(column.Name), kind, readOnly, required, serverDefault,
                maxLength, minimum, maximum, mapping?.Choices, targetColumn) { NeedsValue = needsValue });
        }
        foreach (var duplicate in columns.GroupBy(c => c.Field, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            errors.Add($"Columns {string.Join(" and ", duplicate.Select(c => c.Column))} both become field {duplicate.Key}; rename one in the mapping.");

        PlanColumn? Field(SourceColumn? source) => source is null ? null : columns.FirstOrDefault(c => c.Column.Equals(source.Name, StringComparison.OrdinalIgnoreCase));
        var keys = keySources.Select(Field).OfType<PlanColumn>().ToArray();
        if (d.AllowsWrites)
        {
            if (target is null)
                errors.Add(query is null ? "Writes need a table." : "Writes need the key columns to come from one table the SELECT reads; name them in the key columns.");
            else if (keys.Length != keySources.Length || keys.Any(k => k.Target is null))
                errors.Add($"Each key column must be a column of {target.Name}, which the data source writes.");
            // Not refused: an updatable view or a table that is unique in practice works, and the runtime refuses a read of a key that matches
            // several rows and a generated update or delete that does not change exactly one.
            else if (target.IsView)
                warnings?.Add($"{target.Name} is a view, so writes work only if the view accepts them, through an INSTEAD OF trigger or as an updatable view.");
            else if (!target.UniqueKeys.Any(u => u.Count == keys.Length && u.All(n => keys.Any(k => k.Target!.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))))
                warnings?.Add($"The key columns are not a primary key or unique key of {target.Name}: a record whose key matches several rows cannot be opened or changed.");
        }

        PlanColumn? Audit(string role, params string[] kinds)
        {
            if (!audit.TryGetValue(role, out var source)) return null;
            var field = Field(source);
            if (field?.Target is null) { errors.Add($"The audit column {source.Name} must be a column of the written table."); return null; }
            if (!kinds.Contains(field.Kind)) errors.Add($"The audit column {source.Name} must be a {string.Join(" or ", kinds)} column.");
            return field;
        }
        var createdBy = Audit("createdBy", FieldKinds.Text);
        var createdAt = Audit("createdAt", FieldKinds.DateTime, FieldKinds.Date, FieldKinds.Text);
        var updatedBy = Audit("updatedBy", FieldKinds.Text);
        var updatedAt = Audit("updatedAt", FieldKinds.DateTime, FieldKinds.Date, FieldKinds.Text);

        var defaults = new List<InsertValue>();
        foreach (var value in d.InsertDefaults)
        {
            var field = Find(value.Column) is { } source ? Field(source) : null;
            if (field is null) continue;
            if (field.Target is not { Computed: false, RowVersion: false, AutoValue: false } || field == createdBy || field == createdAt || field == updatedBy || field == updatedAt)
            { errors.Add($"The insert default for {value.Column} cannot be written: the database or the engine fills that column."); continue; }
            if (value.Variable is { Kind: BindVariableKind.Field, Name: { } other } && DataSourcePlan.Field(columns, other) is null)
            { errors.Add($"The insert default for {value.Column} uses :FIELD.{other}, which is not a field."); continue; }
            object? literal = null;
            if (value.Literal is not null)
            {
                literal = FieldValues.FromValue(value.Literal, field.Kind, out var valid, d.DateTimes);
                if (!valid || literal is not null && !FieldValues.Check(literal, field, out _))
                { errors.Add($"The insert default for {value.Column} is not a valid {field.Kind} value for that field."); continue; }
            }
            defaults.Add(new(field, value.Variable, literal));
        }

        foreach (var (statement, label) in new[] { (insert, "insert"), (update, "update"), (delete, "delete") })
            foreach (var variable in statement?.Variables.Where(v => v.Kind == BindVariableKind.Field) ?? [])
                if (DataSourcePlan.Field(columns, variable.Name!) is null)
                    errors.Add($"The custom {label} statement uses {variable}, which is not a field.");
        if (errors.Count > start) return null;
        if (compileStatements)
        {
            foreach (var (statement, label) in new[] { (insert, "insert"), (update, "update"), (delete, "delete") })
                if (statement is not null) Compile(statement, label);
            if (errors.Count > start) return null;
        }

        var fields = columns.Select(c => c.ToField()).ToArray();
        return new DataSourcePlan {
            Definition = d, Dialect = dialect, CommandTimeout = commandTimeout, Table = table, Query = query, Columns = columns, Keys = keys, Target = target,
            Restriction = restriction,
            Defaults = defaults, CreatedBy = createdBy, CreatedAt = createdAt, UpdatedBy = updatedBy, UpdatedAt = updatedAt,
            InsertStatement = insert, UpdateStatement = update, DeleteStatement = delete, Fields = fields,
            ContractVersion = Contract(d, columns, keys, target),
        };

        SqlTemplate? Statement(string? text, string label)
            => text is null ? null : SqlTemplate.Parse(dialect.Statement(text), SqlUse.Statement, $"The custom {label} statement", errors);

        void Compile(SqlTemplate statement, string label)
        {
            // SQLite's and PostgreSQL's EXPLAIN take one statement, and Npgsql would run any that follow it, so each is checked on its own;
            // the others compile the text whole, as it runs. Nothing runs either way.
            foreach (var part in dialect.Kind is DatabaseDialect.Sqlite or DatabaseDialect.Postgres ? statement.Statements : [statement.Text])
            {
                if (!dialect.Compiles(part))
                {
                    warnings?.Add($"Publishing does not check the custom {label} statement: the database cannot compile {SqlTemplate.FirstWord(part)?.ToUpperInvariant()} without running it.");
                    continue;
                }
                if (SqlTemplate.Parse(part, SqlUse.Statement, "", []) is not { } single) continue;
                try
                {
                    var parameters = new SqlParameters(dialect, commandTimeout);
                    using var command = parameters.Command(connection, null, dialect.CompileOnly(single.Render(parameters, Nothing)));
                    dialect.ConfigureCompilation(command);
                    using var reader = command.ExecuteReader();
                    while (reader.Read()) { }
                }
                catch (DbException error) { errors.Add($"The custom {label} statement does not compile: {error.Message}"); return; }
            }
        }
    }

    /// <summary>Every bind variable is bound as NULL to read the schema, :USER_GROUPS as an empty list.</summary>
    private static object? Nothing(BindVariable variable) => null;

    private static string Normalize(string name) => new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>The field alias a column gets by name: ORDER_DATE and OrderDate both become orderDate.</summary>
    internal static string FieldAlias(string column)
    {
        var words = Words(column);
        if (words.Count == 0) return column;
        return words[0].ToLowerInvariant() + string.Concat(words.Skip(1).Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
    }

    /// <summary>The label a column gets by name, in sentence case: ORDER_DATE becomes "Order date".</summary>
    internal static string Label(string column)
    {
        var words = Words(column);
        if (words.Count == 0) return column;
        var text = string.Join(' ', words.Select(w => w.ToLowerInvariant()));
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static List<string> Words(string column)
    {
        var words = new List<string>();
        foreach (var part in column.Split(['_', ' ', '-', '.'], StringSplitOptions.RemoveEmptyEntries))
        {
            // Mixed case marks words (OrderDate); an all-capital name is one word (CUSTOMER).
            var first = 0;
            for (var i = 1; i < part.Length; i++)
                if (char.IsUpper(part[i]) && char.IsLower(part[i - 1])) { words.Add(part[first..i]); first = i; }
            words.Add(part[first..]);
        }
        return words;
    }

    private static string Contract(DataSourceDefinition definition, IEnumerable<PlanColumn> columns, IEnumerable<PlanColumn> keys, TableSchema? target)
    {
        var text = new StringBuilder(definition.ComputeFingerprint()).Append('|').Append(target?.Name);
        foreach (var c in columns)
            text.Append('|').AppendJoin(',', c.Field, c.Column, c.Kind, c.ReadOnly, c.Required, c.NeedsValue, c.HasServerDefault, c.MaxLength, c.Minimum, c.Maximum, c.Target?.StoreType)
                .AppendJoin(',', c.Choices?.Select(x => x.Value) ?? []);
        text.Append('|').AppendJoin(',', keys.Select(k => k.Field));
        return "ds-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }
}
