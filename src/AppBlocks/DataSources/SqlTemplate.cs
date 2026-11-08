using System.Data;
using System.Data.Common;
using System.Text;

namespace AppBlocks.DataSources;

/// <summary>The bind variables of a data source (design section 4.3), APEX's <c>:APP_USER</c> and friends in Umbraco terms.</summary>
public enum BindVariableKind
{
    /// <summary>The Windows SID the account is linked to; null for an account without one.</summary>
    UserSid,
    /// <summary>The member's or backoffice user's username.</summary>
    UserName,
    /// <summary>The key the application uses for the account: the member id, or <c>backoffice:</c> and the user id.</summary>
    UserKey,
    UserEmail,
    /// <summary>The Member Group names, or for a backoffice User the User Group names, usable only as <c>IN (:USER_GROUPS)</c>.</summary>
    UserGroups,
    /// <summary>The web server's local date and time.</summary>
    Now,
    /// <summary>The current moment in UTC, bound in the form each dialect compares with stored UTC stamps.</summary>
    NowUtc,
    /// <summary>A query parameter of the form request.</summary>
    Param,
    /// <summary>The current value of another field of the record.</summary>
    Field,
    /// <summary>A value the application's registered <see cref="IBindVariableProvider"/> computes for the operation, written <c>:APP.name</c>.</summary>
    App,
    /// <summary>A value a backing class passes with its own statement, written <c>:ARG.name</c>; editor SQL has none.</summary>
    Arg,
}

/// <summary>One bind variable as written, such as <c>:USER_SID</c> or <c>:FIELD.customer</c>.</summary>
public sealed record BindVariable(BindVariableKind Kind, string? Name = null)
{
    private static readonly Dictionary<string, BindVariableKind> Fixed = new(StringComparer.OrdinalIgnoreCase) {
        ["USER_SID"] = BindVariableKind.UserSid, ["USER_NAME"] = BindVariableKind.UserName, ["USER_KEY"] = BindVariableKind.UserKey,
        ["USER_EMAIL"] = BindVariableKind.UserEmail, ["USER_GROUPS"] = BindVariableKind.UserGroups, ["NOW"] = BindVariableKind.Now,
        ["NOW_UTC"] = BindVariableKind.NowUtc,
    };

    public override string ToString() => Kind switch {
        BindVariableKind.Param => ":PARAM." + Name,
        BindVariableKind.Field => ":FIELD." + Name,
        BindVariableKind.App => ":APP." + Name,
        BindVariableKind.Arg => ":ARG." + Name,
        _ => ":" + Fixed.First(pair => pair.Value == Kind).Key,
    };

    /// <summary>Reads a variable written without its colon (<c>USER_SID</c>, <c>PARAM.region</c>); null when it is not one.</summary>
    public static BindVariable? Parse(string text)
    {
        var dot = text.IndexOf('.');
        if (dot < 0) return Fixed.TryGetValue(text, out var kind) ? new(kind) : null;
        var name = text[(dot + 1)..];
        if (!SqlTemplate.IsIdentifier(name)) return null;
        return text[..dot].ToUpperInvariant() switch {
            "PARAM" => new(BindVariableKind.Param, name),
            "FIELD" => new(BindVariableKind.Field, name),
            "APP" => new(BindVariableKind.App, name),
            "ARG" => new(BindVariableKind.Arg, name),
            _ => null,
        };
    }
}

/// <summary>Where editor SQL is used, which decides what it may contain.</summary>
internal enum SqlUse
{
    /// <summary>A SELECT used as the row source: one statement, no :FIELD values.</summary>
    RowSource,
    /// <summary>A predicate added to every fetch and write: only the signed-in person's values, :NOW and :NOW_UTC.</summary>
    RowRestriction,
    /// <summary>A custom insert, update or delete, run whole.</summary>
    Statement,
    /// <summary>A backing class's own statement or block, run whole, the only SQL that takes :ARG values.</summary>
    Handler,
}

/// <summary>
/// SQL written by an editor, split once into literal text and bind variables. Rendering replaces each variable with
/// a parameter marker and binds its value, so editor text never receives a value and values never become text.
/// The scan understands strings, quoted names and comments, so a colon inside <c>'10:30'</c> or a comment is text.
/// </summary>
internal sealed class SqlTemplate
{
    private readonly object[] parts;

    private SqlTemplate(string text, object[] parts, IReadOnlyList<string> statements)
    {
        Text = text;
        this.parts = parts;
        Statements = statements;
        Variables = parts.OfType<BindVariable>().Distinct().ToArray();
    }

    public string Text { get; }
    public IReadOnlyList<BindVariable> Variables { get; }
    /// <summary>The statements between top-level semicolons, for checks that take one statement at a time.</summary>
    public IReadOnlyList<string> Statements { get; }

    public static bool IsIdentifier(string text) => text.Length is > 0 and <= 64 && (char.IsAsciiLetter(text[0]) || text[0] == '_')
        && text.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>Whether a row source is a query rather than a table name: its first word is SELECT or WITH.</summary>
    public static bool IsQuery(string source)
    {
        var scan = new Scanner(source);
        return scan.FirstWord() is { } word && (word.Equals("SELECT", StringComparison.OrdinalIgnoreCase) || word.Equals("WITH", StringComparison.OrdinalIgnoreCase));
    }

    /// <param name="label">Names the SQL in messages, such as "The row restriction".</param>
    public static SqlTemplate? Parse(string sql, SqlUse use, string label, ICollection<string> errors)
    {
        var start = errors.Count;
        var parts = new List<object>();
        var literal = new StringBuilder();
        var tokens = new List<string>();
        var groups = new List<int>();
        var statements = new List<string>();
        var statementStart = 0;
        var depth = 0;
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';
            var alternative = Scanner.EndOfAlternative(sql, i);
            if (alternative != i)
            {
                if (alternative < 0) { errors.Add($"{label} has an unterminated quoted string."); return null; }
                literal.Append(sql, i, alternative - i);
                tokens.Add("'");
                i = alternative;
                continue;
            }
            if (c is '\'' or '"' or '`' or '[')
            {
                var end = Scanner.EndOfQuoted(sql, i);
                if (end < 0) { errors.Add($"{label} has an unterminated string or quoted name."); return null; }
                literal.Append(sql, i, end - i);
                tokens.Add(c == '\'' ? "'" : "\"");
                i = end;
                continue;
            }
            if (c == '-' && next == '-')
            {
                var end = sql.IndexOf('\n', i);
                end = end < 0 ? sql.Length : end;
                literal.Append(sql, i, end - i);
                i = end;
                continue;
            }
            if (c == '/' && next == '*')
            {
                var end = Scanner.EndOfComment(sql, i);
                if (end < 0) { errors.Add($"{label} has an unterminated comment."); return null; }
                literal.Append(sql, i, end - i);
                i = end;
                continue;
            }
            if (c == ':' && (char.IsAsciiLetter(next) || next == '_') && (i == 0 || !IsWordChar(sql[i - 1]) && sql[i - 1] != ':'))
            {
                var end = i + 1;
                while (end < sql.Length && IsWordChar(sql[end])) end++;
                if (end + 1 < sql.Length && sql[end] == '.' && (char.IsAsciiLetter(sql[end + 1]) || sql[end + 1] == '_'))
                {
                    end++;
                    while (end < sql.Length && IsWordChar(sql[end])) end++;
                }
                var written = sql[i..end];
                var variable = BindVariable.Parse(written[1..]);
                if (variable is null) errors.Add($"{label} uses {written}, which is not a bind variable (use :USER_SID, :USER_NAME, :USER_KEY, :USER_EMAIL, :USER_GROUPS, :NOW, :NOW_UTC, :PARAM.name, :FIELD.alias or :APP.name{(use == SqlUse.Handler ? ", or :ARG.name" : "")}).");
                else if (variable.Kind == BindVariableKind.Arg && use != SqlUse.Handler)
                    errors.Add($"{label} cannot use {written}: :ARG. values are what a backing class passes with its own statements.");
                else if (use == SqlUse.RowRestriction && variable.Kind is BindVariableKind.Param or BindVariableKind.Field)
                    errors.Add($"{label} cannot use {written}: the caller controls request parameters and field values, so a restriction built on them restricts nothing.");
                else if (use == SqlUse.RowSource && variable.Kind == BindVariableKind.Field)
                    errors.Add($"{label} cannot use {written}: the row source is read before any field has a value.");
                else
                {
                    if (literal.Length > 0) { parts.Add(literal.ToString()); literal.Clear(); }
                    parts.Add(variable);
                    if (variable.Kind == BindVariableKind.UserGroups) groups.Add(tokens.Count);
                }
                tokens.Add(":");
                i = end;
                continue;
            }
            if (IsWordChar(c))
            {
                var end = i;
                while (end < sql.Length && IsWordChar(sql[end])) end++;
                tokens.Add(sql[i..end]);
                literal.Append(sql, i, end - i);
                i = end;
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')' && --depth < 0) { errors.Add($"{label} closes a parenthesis it never opened."); return null; }
            else if (c == ';')
            {
                if (use is not (SqlUse.Statement or SqlUse.Handler)) { errors.Add($"{label} must be one statement without semicolons."); return null; }
                statements.Add(sql[statementStart..i]);
                statementStart = i + 1;
            }
            if (!char.IsWhiteSpace(c)) tokens.Add(c.ToString());
            literal.Append(c);
            i++;
        }
        if (depth != 0) errors.Add($"{label} leaves a parenthesis open.");
        foreach (var index in groups)
        {
            var inList = index >= 2 && tokens[index - 1] == "(" && tokens[index - 2].Equals("IN", StringComparison.OrdinalIgnoreCase)
                && index + 1 < tokens.Count && tokens[index + 1] == ")";
            if (!inList) errors.Add($"{label} uses :USER_GROUPS outside IN (:USER_GROUPS); it stands for a list of groups.");
        }
        if (use == SqlUse.RowSource && !IsQuery(sql)) errors.Add($"{label} must start with SELECT or WITH.");
        if (literal.Length > 0) parts.Add(literal.ToString());
        statements.Add(sql[statementStart..]);
        return errors.Count == start
            ? new SqlTemplate(sql, parts.ToArray(), statements.Where(s => new Scanner(s).FirstWord() is not null).ToArray())
            : null;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#' or '@';

    /// <summary>Renders the SQL with a parameter marker for every variable, binding each value through <paramref name="parameters"/>.</summary>
    /// <param name="value">The value of a variable; for :USER_GROUPS a list of names, expanded to one parameter each.</param>
    public string Render(SqlParameters parameters, Func<BindVariable, object?> value)
    {
        var text = new StringBuilder(Text.Length + 16);
        foreach (var part in parts)
        {
            if (part is string literal) { text.Append(literal); continue; }
            var variable = (BindVariable)part;
            if (variable.Kind == BindVariableKind.UserGroups)
            {
                var names = value(variable) as IReadOnlyList<string> ?? [];
                text.Append(names.Count == 0 ? "NULL" : string.Join(", ", names.Select(name => parameters.Add(name))));
            }
            else text.Append(parameters.Add(value(variable)));
        }
        return text.ToString();
    }

    /// <summary>A forward-only reader over code, skipping strings, quoted names and comments.</summary>
    private readonly struct Scanner(string sql)
    {
        public string? FirstWord()
        {
            var i = 0;
            while (i < sql.Length)
            {
                var c = sql[i];
                if (char.IsWhiteSpace(c) || c == '(') { i++; continue; }
                if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-') { var end = sql.IndexOf('\n', i); if (end < 0) return null; i = end; continue; }
                if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*') { var end = EndOfComment(sql, i); if (end < 0) return null; i = end; continue; }
                if (!IsWordChar(c)) return null;
                var stop = i;
                while (stop < sql.Length && IsWordChar(sql[stop])) stop++;
                return sql[i..stop];
            }
            return null;
        }

        /// <summary>The index after a quoted run that starts at <paramref name="start"/>; doubled closing quotes are escapes. -1 when unterminated.</summary>
        public static int EndOfQuoted(string sql, int start)
        {
            var close = sql[start] == '[' ? ']' : sql[start];
            var escapes = close == '\'' && start > 0 && sql[start - 1] is 'e' or 'E'
                && (start < 2 || !IsWordChar(sql[start - 2]));
            for (var i = start + 1; i < sql.Length; i++)
            {
                if (escapes && sql[i] == '\\') { i++; continue; }
                if (sql[i] != close) continue;
                if (i + 1 < sql.Length && sql[i + 1] == close) { i++; continue; }
                return i + 1;
            }
            return -1;
        }

        public static int EndOfComment(string sql, int start)
        {
            var nesting = 1;
            for (var i = start + 2; i + 1 < sql.Length; i++)
            {
                if (sql[i] == '/' && sql[i + 1] == '*') { nesting++; i++; }
                else if (sql[i] == '*' && sql[i + 1] == '/') { i++; if (--nesting == 0) return i + 1; }
            }
            return -1;
        }

        // Oracle q'[...]'/nq'[...]' and PostgreSQL $tag$...$tag$ are literals, including any bind-like text inside.
        public static int EndOfAlternative(string sql, int start)
        {
            if (start > 0 && IsWordChar(sql[start - 1])) return start;
            var quote = sql[start] is 'n' or 'N' && start + 1 < sql.Length && sql[start + 1] is 'q' or 'Q' ? start + 1 : start;
            if (sql[quote] is 'q' or 'Q' && quote + 2 < sql.Length && sql[quote + 1] == '\'')
            {
                var close = sql[quote + 2] switch { '[' => ']', '(' => ')', '{' => '}', '<' => '>', var c => c };
                var end = sql.IndexOf($"{close}'", quote + 3, StringComparison.Ordinal);
                return end < 0 ? -1 : end + 2;
            }
            if (sql[start] != '$') return start;
            var tagEnd = start + 1;
            while (tagEnd < sql.Length && (char.IsLetterOrDigit(sql[tagEnd]) || sql[tagEnd] == '_')) tagEnd++;
            if (tagEnd == sql.Length || sql[tagEnd] != '$' || tagEnd > start + 1 && char.IsDigit(sql[start + 1])) return start;
            var tag = sql[start..(tagEnd + 1)];
            var stop = sql.IndexOf(tag, tagEnd + 1, StringComparison.Ordinal);
            return stop < 0 ? -1 : stop + tag.Length;
        }
    }
}

/// <summary>A value with the database type it must be bound as, where the provider would otherwise infer a different one.</summary>
internal sealed record TypedValue(object? Value, DbType Type);

/// <summary>
/// The parameters of one command, named ds0, ds1 and so on. Every value a statement uses arrives here: editor
/// literals, request values and the signed-in person's facts alike. Bounded below SQL Server's 2100.
/// </summary>
internal sealed class SqlParameters(SqlDialect dialect)
{
    public const int Max = 2000;
    private readonly List<(string Name, object? Value)> values = [];

    public int Count => values.Count;
    internal IReadOnlyList<(string Name, object? Value)> Bindings => values;

    public string Add(object? value)
    {
        if (values.Count >= Max) throw new DataSourceException($"A data source statement may bind at most {Max} values.");
        var name = "ds" + values.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        values.Add((name, value));
        return dialect.Marker(name);
    }

    public DbCommand Command(DbConnection connection, DbTransaction? transaction, string text)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = text;
        command.CommandTimeout = 30;
        dialect.ConfigureCommand(command);
        foreach (var (name, value) in values)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = dialect.Marker(name);
            if (value is TypedValue typed) { parameter.DbType = typed.Type; parameter.Value = typed.Value ?? DBNull.Value; }
            else parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        return command;
    }
}

/// <summary>A problem the editor or the caller can act on, reported as a message rather than a server error.</summary>
public sealed class DataSourceException(string message, Exception? inner = null) : Exception(message, inner);
