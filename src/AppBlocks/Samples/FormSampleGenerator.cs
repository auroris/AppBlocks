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
        var names = new HashSet<string>(["Id", "Checksum", "SampleFormValues", "Equals", "GetHashCode", "ToString", "Deconstruct", "PrintMembers", "EqualityContract", "Clone", "GetType", "MemberwiseClone", "Finalize"], StringComparer.OrdinalIgnoreCase);
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
        string SqlType(FormField field) => field.Kind switch { "number" => "NUMERIC", "checkbox" => "INTEGER", _ => "TEXT" };
        string CsType(FormField field) => field.Kind switch { "number" => "decimal?", "checkbox" => "bool?", _ => "string?" };
        var schema = "CREATE TABLE IF NOT EXISTS SampleForm (Id TEXT PRIMARY KEY"
            + string.Concat(fields.Select(f => $", {Column(f.Name)} {SqlType(f.Field)}")) + ");";
        var select = "SELECT Id" + string.Concat(fields.Select(f => ", " + Column(f.Name))) + " FROM SampleForm WHERE Id = @id;";
        var insert = "INSERT INTO SampleForm (Id" + string.Concat(fields.Select(f => ", " + Column(f.Name))) + ") VALUES (@id" + string.Concat(fields.Select(f => ", @" + f.Name)) + ");";
        var update = "UPDATE SampleForm SET " + (fields.Length == 0 ? "Id = Id" : string.Join(", ", fields.Select(f => $"{Column(f.Name)} = @{f.Name}"))) + " WHERE Id = @id;";
        var code = new StringBuilder();
        code.AppendLine("// SAMPLE ONLY — a SQLite example, not deployed code or a production migration.");
        code.AppendLine("// Package: Microsoft.Data.Sqlite. Use a separate example database.");
        code.AppendLine("// Generated from form " + form.FormKey + " on page " + form.PageKey + ".");
        code.AppendLine("// Member and backoffice User identities, groups and business rules: Settings > AppBlocks > Business rules.");
        code.AppendLine("// ClaimsPrincipal can represent an Umbraco Member or a backoffice User; authorize the intended account type explicitly.");
        code.AppendLine("// On a page with no Public Access an anonymous visitor reaches your class, and a class with no check is public.");
        code.AppendLine("// Every operation below therefore starts by requiring a sign-in. Remove that line only for an operation you mean to be public.");
        code.AppendLine("// Supply an authorization callback; add row-level business rules before using real data.");
        code.AppendLine("using System;\nusing System.Threading;\nusing System.Threading.Tasks;\nusing System.Security.Claims;\nusing System.Security.Cryptography;\nusing System.Text.Json;\nusing System.Text.Json.Serialization;\nusing Microsoft.Data.Sqlite;\n");
        code.AppendLine("namespace AppBlocks.Samples.Generated;\n");
        code.AppendLine("public sealed record SampleFormValues\n{");
        foreach (var field in fields)
        {
            code.AppendLine($"    [JsonPropertyName({Literal(field.Field.Key)})]");
            code.AppendLine($"    public {CsType(field.Field)} {field.Name} {{ get; init; }}");
        }
        code.AppendLine("}\npublic sealed record SampleFormRecord(string Id, string Checksum, SampleFormValues Values);\n");
        code.AppendLine("public sealed class SampleFormRepository(string connectionString, Func<ClaimsPrincipal, bool> authorize)\n{");
        code.AppendLine("    // Run once on the example database. Production schema belongs in a migration.");
        code.AppendLine($"    public const string ExampleSchema = {Literal(schema)};");
        code.AppendLine("    private void DemandAccess(ClaimsPrincipal user)\n    {\n        if (!authorize(user)) throw new UnauthorizedAccessException();\n    }\n");
        code.AppendLine("    public async Task<SampleFormRecord?> ReadAsync(string id, ClaimsPrincipal user, CancellationToken ct = default)\n    {\n        if (user.Identity?.IsAuthenticated != true) throw new UnauthorizedAccessException();\n        DemandAccess(user);\n        await using var connection = new SqliteConnection(connectionString);\n        await connection.OpenAsync(ct);\n        return await ReadRowAsync(connection, null, id, ct);\n    }\n");
        code.AppendLine("    // For a new record use a new Guid string and expectedChecksum = null.\n    // For an edit pass the Id and Checksum returned by ReadAsync. False means the record changed since it was read, or is missing.\n    public async Task<bool> SaveAsync(string id, string? expectedChecksum, SampleFormValues values, ClaimsPrincipal user, CancellationToken ct = default)\n    {\n        if (user.Identity?.IsAuthenticated != true) throw new UnauthorizedAccessException();\n        DemandAccess(user);\n        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException(\"Supply a record id.\");");
        foreach (var (field, name) in fields)
        {
            if (field.Required) code.AppendLine($"        if ({(field.Kind is "number" or "checkbox" ? $"values.{name} is null" : $"string.IsNullOrWhiteSpace(values.{name})")}) throw new ArgumentException({Literal(field.Label + " is required.")});");
            if (field.MaxLength is { } max && field.Kind is not ("number" or "checkbox")) code.AppendLine($"        if (values.{name}?.Length > {max}) throw new ArgumentException({Literal(field.Label + " is too long.")});");
            if (field.Kind == "number" && field.Minimum is { } min) code.AppendLine($"        if (values.{name} < {min.ToString(System.Globalization.CultureInfo.InvariantCulture)}m) throw new ArgumentException({Literal(field.Label + " is below its minimum.")});");
            if (field.Kind == "number" && field.Maximum is { } maximum) code.AppendLine($"        if (values.{name} > {maximum.ToString(System.Globalization.CultureInfo.InvariantCulture)}m) throw new ArgumentException({Literal(field.Label + " is above its maximum.")});");
        }
        code.AppendLine("        await using var connection = new SqliteConnection(connectionString);\n        await connection.OpenAsync(ct);");
        code.AppendLine("        // BEGIN IMMEDIATE takes SQLite's write lock before the row is read, so no other save can change it between the comparison and the write.");
        code.AppendLine("        await using var transaction = connection.BeginTransaction(deferred: false);");
        code.AppendLine("        var current = await ReadRowAsync(connection, transaction, id, ct);");
        code.AppendLine("        if (expectedChecksum is null ? current is not null : current?.Checksum != expectedChecksum) return false;");
        code.AppendLine("        await using var command = connection.CreateCommand();\n        command.Transaction = transaction;");
        code.AppendLine($"        command.CommandText = current is null ? {Literal(insert)} : {Literal(update)};");
        code.AppendLine("        command.Parameters.AddWithValue(\"@id\", id);");
        foreach (var f in fields) code.AppendLine($"        command.Parameters.AddWithValue(\"@{f.Name}\", (object?)values.{f.Name} ?? DBNull.Value);");
        code.AppendLine("        await command.ExecuteNonQueryAsync(ct);\n        await transaction.CommitAsync(ct);\n        return true;\n    }\n");
        code.AppendLine("    private static async Task<SampleFormRecord?> ReadRowAsync(SqliteConnection connection, SqliteTransaction? transaction, string id, CancellationToken ct)\n    {\n        await using var command = connection.CreateCommand();\n        command.Transaction = transaction;");
        code.AppendLine($"        command.CommandText = {Literal(select)};");
        code.AppendLine("        command.Parameters.AddWithValue(\"@id\", id);\n        await using var reader = await command.ExecuteReaderAsync(ct);\n        if (!await reader.ReadAsync(ct)) return null;\n        var values = new SampleFormValues\n        {");
        for (var i = 0; i < fields.Length; i++)
        {
            var f = fields[i]; var column = i + 1;
            var read = f.Field.Kind switch { "number" => $"reader.GetDecimal({column})", "checkbox" => $"reader.GetBoolean({column})", _ => $"reader.GetString({column})" };
            code.AppendLine($"            {f.Name} = reader.IsDBNull({column}) ? null : {read},");
        }
        code.AppendLine("        };\n        return new(reader.GetString(0), Checksum(values), values);\n    }\n");
        code.AppendLine("    // A checksum of the row's values, read with the record and compared inside the write transaction.");
        code.AppendLine("    // A save by someone else since the read gives a different checksum, and the stale change is refused.");
        code.AppendLine("    private static string Checksum(SampleFormValues values)\n        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(values)))[..32];\n}");
        return code.ToString();
    }
}
