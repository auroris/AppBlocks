using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AppBlocks.Forms;

namespace AppBlocks.Samples;

/// <summary>Produces a downloadable example; AppBlocks never compiles or executes it.</summary>
public static class FormSampleGenerator
{
    public static string Generate(DiscoveredForm form)
    {
        var names = new HashSet<string>(["Id", "Version", "SampleFormValues", "Equals", "GetHashCode", "ToString", "Deconstruct", "PrintMembers", "EqualityContract", "Clone", "GetType", "MemberwiseClone", "Finalize"], StringComparer.OrdinalIgnoreCase);
        string Name(FormField field, int index) {
            var name = string.Concat(Regex.Split(field.Key, "[^A-Za-z0-9]+").Where(part => part.Length > 0).Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
            if (name.Length == 0) name = "Field" + (index + 1);
            if (char.IsDigit(name[0])) name = "Field" + name;
            name = name[..Math.Min(name.Length, 64)];
            var candidate = name; var suffix = 2;
            while (!names.Add(candidate)) candidate = name + suffix++;
            return candidate;
        }
        var fields = form.Fields.Where(f => !f.ReadOnly).Select((field, index) => (Field: field, Name: Name(field, index))).ToArray();
        string Literal(string value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        string Column(string name) => "\"" + name + "\"";
        string SqlType(FormField field) => field.Kind switch { "number" => "NUMERIC", "boolean" or "checkbox" => "INTEGER", _ => "TEXT" };
        string CsType(FormField field) => field.Kind switch { "number" => "decimal?", "boolean" or "checkbox" => "bool?", _ => "string?" };
        var schema = "CREATE TABLE IF NOT EXISTS SampleForm (Id TEXT PRIMARY KEY, Version INTEGER NOT NULL DEFAULT 1"
            + string.Concat(fields.Select(f => $", {Column(f.Name)} {SqlType(f.Field)}")) + ");";
        var select = "SELECT Id, Version" + string.Concat(fields.Select(f => ", " + Column(f.Name))) + " FROM SampleForm WHERE Id = @id;";
        var insertColumns = string.Concat(fields.Select(f => ", " + Column(f.Name)));
        var parameters = string.Concat(fields.Select(f => ", @" + f.Name));
        var updates = string.Concat(fields.Select(f => $", {Column(f.Name)} = excluded.{Column(f.Name)}"));
        var upsert = $"INSERT INTO SampleForm (Id, Version{insertColumns}) SELECT @id, 1{parameters} WHERE @version = 0 OR EXISTS (SELECT 1 FROM SampleForm WHERE Id = @id) ON CONFLICT(Id) DO UPDATE SET Version = SampleForm.Version + 1{updates} WHERE SampleForm.Version = @version;";
        var code = new StringBuilder();
        code.AppendLine("// SAMPLE ONLY — a SQLite example, not deployed code or a production migration.");
        code.AppendLine("// Package: Microsoft.Data.Sqlite. Use a separate example database.");
        code.AppendLine("// Generated from form " + form.FormKey + " on page " + form.PageKey + ".");
        code.AppendLine("// Member and backoffice User identities, groups and business rules: Settings > AppBlocks > Business rules.");
        code.AppendLine("// ClaimsPrincipal can represent an Umbraco Member or a backoffice User; authorize the intended account type explicitly.");
        code.AppendLine("// Supply an authorization callback; add row-level business rules before using real data.");
        code.AppendLine("using System;\nusing System.Threading;\nusing System.Threading.Tasks;\nusing System.Security.Claims;\nusing System.Text.Json.Serialization;\nusing Microsoft.Data.Sqlite;\n");
        code.AppendLine("namespace AppBlocks.Samples.Generated;\n");
        code.AppendLine("public sealed record SampleFormValues\n{");
        foreach (var field in fields)
        {
            code.AppendLine($"    [JsonPropertyName({Literal(field.Field.Key)})]");
            code.AppendLine($"    public {CsType(field.Field)} {field.Name} {{ get; init; }}");
        }
        code.AppendLine("}\npublic sealed record SampleFormRecord(string Id, long Version, SampleFormValues Values);\n");
        code.AppendLine("public sealed class SampleFormRepository(string connectionString, Func<ClaimsPrincipal, bool> authorize)\n{");
        code.AppendLine("    // Run once on the example database. Production schema belongs in a migration.");
        code.AppendLine($"    public const string ExampleSchema = {Literal(schema)};");
        code.AppendLine("    private void DemandAccess(ClaimsPrincipal user)\n    {\n        if (user.Identity?.IsAuthenticated != true || !authorize(user)) throw new UnauthorizedAccessException();\n    }\n");
        code.AppendLine("    public async Task<SampleFormRecord?> ReadAsync(string id, ClaimsPrincipal user, CancellationToken ct = default)\n    {\n        DemandAccess(user);\n        await using var connection = new SqliteConnection(connectionString);\n        await connection.OpenAsync(ct);\n        await using var command = connection.CreateCommand();");
        code.AppendLine($"        command.CommandText = {Literal(select)};");
        code.AppendLine("        command.Parameters.AddWithValue(\"@id\", id);\n        await using var reader = await command.ExecuteReaderAsync(ct);\n        if (!await reader.ReadAsync(ct)) return null;\n        return new(reader.GetString(0), reader.GetInt64(1), new SampleFormValues\n        {");
        for (var i = 0; i < fields.Length; i++)
        {
            var f = fields[i]; var column = i + 2;
            var read = f.Field.Kind switch { "number" => $"reader.GetDecimal({column})", "boolean" or "checkbox" => $"reader.GetBoolean({column})", _ => $"reader.GetString({column})" };
            code.AppendLine($"            {f.Name} = reader.IsDBNull({column}) ? null : {read},");
        }
        code.AppendLine("        });\n    }\n");
        code.AppendLine("    // For a new record use a new Guid string and expectedVersion = 0.\n    // For an edit pass the Id and Version returned by ReadAsync. False means a stale/missing record.\n    public async Task<bool> UpsertAsync(string id, long expectedVersion, SampleFormValues values, ClaimsPrincipal user, CancellationToken ct = default)\n    {\n        DemandAccess(user);\n        if (string.IsNullOrWhiteSpace(id) || expectedVersion < 0) throw new ArgumentException(\"Supply a record id and a valid version.\");");
        foreach (var (field, name) in fields)
        {
            if (field.Required) code.AppendLine($"        if ({(field.Kind is "number" or "boolean" or "checkbox" ? $"values.{name} is null" : $"string.IsNullOrWhiteSpace(values.{name})")}) throw new ArgumentException({Literal(field.Label + " is required.")});");
            if (field.MaxLength is { } max && field.Kind is not ("number" or "boolean" or "checkbox")) code.AppendLine($"        if (values.{name}?.Length > {max}) throw new ArgumentException({Literal(field.Label + " is too long.")});");
            if (field.Kind == "number" && field.Minimum is { } min) code.AppendLine($"        if (values.{name} < {min.ToString(System.Globalization.CultureInfo.InvariantCulture)}m) throw new ArgumentException({Literal(field.Label + " is below its minimum.")});");
            if (field.Kind == "number" && field.Maximum is { } maximum) code.AppendLine($"        if (values.{name} > {maximum.ToString(System.Globalization.CultureInfo.InvariantCulture)}m) throw new ArgumentException({Literal(field.Label + " is above its maximum.")});");
        }
        code.AppendLine("        await using var connection = new SqliteConnection(connectionString);\n        await connection.OpenAsync(ct);\n        await using var command = connection.CreateCommand();");
        code.AppendLine($"        command.CommandText = {Literal(upsert)};");
        code.AppendLine("        command.Parameters.AddWithValue(\"@id\", id);\n        command.Parameters.AddWithValue(\"@version\", expectedVersion);");
        foreach (var f in fields) code.AppendLine($"        command.Parameters.AddWithValue(\"@{f.Name}\", (object?)values.{f.Name} ?? DBNull.Value);");
        code.AppendLine("        return await command.ExecuteNonQueryAsync(ct) == 1;\n    }\n}");
        return code.ToString();
    }
}
