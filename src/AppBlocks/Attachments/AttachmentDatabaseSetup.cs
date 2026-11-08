using System.Data.Common;
using AppBlocks.Connections;
using AppBlocks.Databases;
using Microsoft.Extensions.Options;

namespace AppBlocks.Attachments;

/// <summary>The attachment tables on the setup screen. Its script is the store's own schema file for the connection's dialect.</summary>
internal sealed class AttachmentDatabaseSetup(ConnectionCatalog connections, IApplicationConnectionFactory factory, IOptions<AttachmentSettings> settings) : IDatabaseSetupFeature
{
    private const string Title = "File uploads";
    private const string Lost = "Without them, Attachments blocks show a notice instead of files, and nothing can be uploaded. Forms keep working.";
    private const string OtherDatabases = "File uploads run on SQLite and SQL Server only. Set Attachments:Connection in the site's configuration to AttachmentsDb, or to another SQLite or SQL Server connection. See the separate upload store in the AppBlocks README.";

    public string Alias => "uploads";

    public async Task<DatabaseFeatureStatus> StatusAsync(CancellationToken ct)
    {
        var name = settings.Value.ConnectionName;
        var label = connections.List().FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var shown = label is null ? name : label.Label == label.Name ? label.Name : $"{label.Label} ({label.Name})";
        var noTables = AttachmentStore.Tables.Select(table => new DatabaseFeatureTable(table, "missing", true)).ToArray();
        if (DatabaseKinds.TryFromProvider(label?.Provider, out var listed) && !Supported(listed))
            return new(Alias, Title, shown, label!.Provider, DatabaseFeatureState.Unsupported, OtherDatabases, Lost, noTables, null, null, false);
        DatabaseInspection inspection;
        DatabaseDialect kind;
        try
        {
            await using var connection = Inspectable(name);
            kind = DatabaseKinds.Of(connection);
            if (!Supported(kind))
                return new(Alias, Title, shown, kind.ProviderName(), DatabaseFeatureState.Unsupported, OtherDatabases, Lost, noTables, null, null, false);
            inspection = await DatabaseInspector.InspectAsync(connection, AttachmentStore.Tables, false, ct);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return new(Alias, Title, shown, label?.Provider ?? ConnectionCatalog.UnknownProvider, DatabaseFeatureState.Unreachable,
                $"Attachments:Connection names {name}, which is not a connection AppBlocks can open. Choose one from the Connections tab.", Lost, noTables, null, null, false);
        }
        var (state, summary) = DatabaseSetupGuide.Standing(inspection, AttachmentStore.Tables);
        var provider = kind.ProviderName();
        var (text, sql) = DatabaseSetupGuide.Grant(provider, inspection.Account, inspection.Schema);
        return new(Alias, Title, shown, provider, state, summary, Lost, DatabaseSetupGuide.Tables(inspection, AttachmentStore.Tables), text, sql, true);
    }

    public async Task<string?> ScriptAsync(CancellationToken ct)
    {
        var status = await StatusAsync(ct);
        if (!status.HasScript) return null;
        var folder = DatabaseKinds.FromProvider(status.Provider).SchemaFolder();
        var inspection = await LookAsync(ct);
        return DatabaseSetupGuide.Header(Title, status.Connection, status.Provider, inspection.Account, inspection.Schema)
            + ApplicationDatabase.Script(AttachmentStore.Schema, folder)
            + DatabaseSetupGuide.TableGrants(status.Provider, inspection.Account, AttachmentStore.Tables);
    }

    private async Task<DatabaseInspection> LookAsync(CancellationToken ct)
    {
        await using var connection = Inspectable(settings.Value.ConnectionName);
        return await DatabaseInspector.InspectAsync(connection, [], false, ct);
    }

    /// <summary>The databases the upload store has scripts for.</summary>
    private static bool Supported(DatabaseDialect kind) => kind is DatabaseDialect.Sqlite or DatabaseDialect.SqlServer;

    /// <summary>A connection the catalog knows, or one only the report builder has, which ensures nothing either way.</summary>
    private DbConnection Inspectable(string name)
    {
        try { return connections.Inspectable(name); }
        catch (ArgumentException) { return factory.CreateConnection(name); }
    }
}
