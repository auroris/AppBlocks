using System.Data.Common;

namespace AppBlocks.Connections;

public sealed record DatabaseTable(string Name, string Value);

public sealed class ConnectionTables(ConnectionCatalog connections)
{
    public async Task<IReadOnlyList<DatabaseTable>> ListAsync(string name, CancellationToken ct)
    {
        var info = connections.List().SingleOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Choose an available connection.");
        await using var connection = connections.Openable(name);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await connection.OpenAsync(timeout.Token);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 10;
        command.CommandText = info.Provider switch
        {
            "sqlite" => "SELECT '' AS schema_name, name FROM sqlite_schema WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%' ORDER BY name",
            "sqlServer" or "postgres" => "SELECT TABLE_SCHEMA, TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA NOT IN ('pg_catalog','information_schema') ORDER BY TABLE_SCHEMA, TABLE_NAME",
            "oracle" => "SELECT OWNER, TABLE_NAME FROM ALL_TABLES WHERE OWNER = SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA') UNION SELECT OWNER, VIEW_NAME FROM ALL_VIEWS WHERE OWNER = SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')",
            _ => throw new ArgumentException("Unsupported database type."),
        };
        var tables = new List<DatabaseTable>();
        await using var reader = await command.ExecuteReaderAsync(timeout.Token);
        while (await reader.ReadAsync(timeout.Token))
        {
            var schema = reader.GetString(0); var table = reader.GetString(1);
            string Quote(string item) => info.Provider == "sqlServer" ? "[" + item.Replace("]", "]]") + "]" : "\"" + item.Replace("\"", "\"\"") + "\"";
            tables.Add(new(schema.Length > 0 ? schema + "." + table : table, schema.Length > 0 ? Quote(schema) + "." + Quote(table) : Quote(table)));
        }
        return tables;
    }
}
