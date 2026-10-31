using AppBlocks.Connections;
using AppBlocks.Databases;
using Microsoft.Extensions.Options;

namespace AppBlocks.Attachments;

/// <summary>The attachment tables on the setup screen. Its script is the store's own schema file for the connection's dialect.</summary>
internal sealed class AttachmentDatabaseSetup(ConnectionCatalog connections, IOptions<AttachmentSettings> settings) : IDatabaseSetupFeature
{
    private const string Title = "File uploads";
    private const string Lost = "Without them, Attachments blocks show a notice instead of files, and nothing can be uploaded. Forms keep working.";

    public string Alias => "uploads";

    public async Task<DatabaseFeatureStatus> StatusAsync(CancellationToken ct)
    {
        var name = settings.Value.ConnectionName;
        var label = connections.List().FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var shown = label is null ? name : label.Label == label.Name ? label.Name : $"{label.Label} ({label.Name})";
        var provider = label?.Provider ?? ConnectionCatalog.UnknownProvider;
        var noTables = AttachmentStore.Tables.Select(table => new DatabaseFeatureTable(table, "missing", true)).ToArray();
        if (provider is "postgres" or "oracle" or "oracle11g")
            return new(Alias, Title, shown, provider, DatabaseFeatureState.Unsupported,
                "File uploads run on SQLite and SQL Server only. Set Attachments:Connection in the site's configuration to AttachmentsDb, or to another SQLite or SQL Server connection. See the separate upload store in the AppBlocks README.",
                Lost, noTables, null, null, false);
        DatabaseInspection inspection;
        try
        {
            await using var connection = connections.Inspectable(name);
            inspection = await DatabaseInspector.InspectAsync(connection, AttachmentStore.Tables, false, ct);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return new(Alias, Title, shown, provider, DatabaseFeatureState.Unreachable,
                $"Attachments:Connection names {name}, which is not a connection AppBlocks can open. Choose one from the Connections tab.", Lost, noTables, null, null, false);
        }
        var (state, summary) = DatabaseSetupGuide.Standing(inspection, AttachmentStore.Tables);
        var (text, sql) = DatabaseSetupGuide.Grant(provider, inspection.Account);
        return new(Alias, Title, shown, provider, state, summary, Lost, DatabaseSetupGuide.Tables(inspection, AttachmentStore.Tables), text, sql, true);
    }

    public async Task<string?> ScriptAsync(CancellationToken ct)
    {
        var status = await StatusAsync(ct);
        if (!status.HasScript) return null;
        var dialect = status.Provider == "sqlServer" ? "sqlserver" : "sqlite";
        var account = await AccountAsync(ct);
        return DatabaseSetupGuide.Header(Title, status.Connection, status.Provider, account)
            + ApplicationDatabase.Script(AttachmentStore.Schema, dialect)
            + DatabaseSetupGuide.TableGrants(status.Provider, account, AttachmentStore.Tables.Select(table => "dbo." + table));
    }

    private async Task<string?> AccountAsync(CancellationToken ct)
    {
        await using var connection = connections.Inspectable(settings.Value.ConnectionName);
        return (await DatabaseInspector.InspectAsync(connection, [], false, ct)).Account;
    }
}
