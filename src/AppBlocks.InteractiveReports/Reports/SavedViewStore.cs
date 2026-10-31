using System.Collections.Concurrent;
using System.Data.Common;
using AppBlocks.Connections;
using AppBlocks.Databases;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;

namespace AppBlocks.InteractiveReports.Reports;

/// <summary>
/// The saved-report store as the report engine sees it on an AppBlocks site. Reports must not depend on their saved views:
/// the engine asks the store for a report's default view before showing any report, and fails the report when that
/// throws. Here, while the table cannot be used, reads answer as an empty store, so a report opens on its own definition,
/// and the definition store hides saving. Whether the table can be used is asked afresh until it can, so a grant or a
/// script takes effect on the next request, and the reason is logged once, with what to do.
/// </summary>
public sealed class SavedViewStore(ISavedReportStore inner, ConnectionCatalog catalog, IReportConnectionFactory connections, ILogger<SavedViewStore> logger) : ISavedReportStore
{
    private readonly ConcurrentDictionary<(string Connection, string Table), bool> usable = new();
    private string? reported;

    internal ISavedReportStore Inner => inner;

    /// <summary>
    /// Whether saved views work now. The first look reads the database only. When the table is not there and nothing says
    /// the account cannot create it, it goes through the store, whose auto-create then makes it, as using a report always has.
    /// </summary>
    public async Task<bool> ReadyAsync(CancellationToken ct = default)
    {
        // A site that replaced the store answers for its own.
        if (inner is not SqlSavedReportStore store) return true;
        SavedReportStoreConfig config;
        try { config = store.Configuration; }
        catch (InvalidOperationException error) { Report(error, "saved-report storage is not configured properly."); return false; }
        var target = (config.ConnectionName, config.TableName);
        if (usable.ContainsKey(target)) return true;
        DatabaseInspection inspection;
        try { inspection = await InspectAsync(catalog, connections, config, [config.TableName], ct); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException) { Report(error, $"the connection {config.ConnectionName} cannot be opened."); return false; }
        if (!inspection.Reachable) { Report(null, $"the connection {config.ConnectionName} cannot be opened."); return false; }
        var state = inspection.StateOf(config.TableName);
        if (state == InspectedTableState.NoAccess) { Report(null, $"this account may not read and write {config.TableName} on connection {config.ConnectionName}."); return false; }
        if (state == InspectedTableState.Missing && (!config.AutoCreate || inspection.CanCreateTables == false))
        {
            Report(null, $"the table {config.TableName} is missing on connection {config.ConnectionName}, and this account {(config.AutoCreate ? "may not create it" : "is not set to create it")}.");
            return false;
        }
        try
        {
            if (state == InspectedTableState.Missing) await inner.ListFamily(ProbeReport, ct);
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            Report(error, $"the table {config.TableName} could not be created on connection {config.ConnectionName}.");
            return false;
        }
        usable[target] = true;
        if (Interlocked.Exchange(ref reported, null) is not null) logger.LogInformation("Saved report views work again on connection {Connection}.", config.ConnectionName);
        return true;
    }

    /// <summary>What the feature's status and readiness both look through: the store's own connection, ensuring no schema when it opens.</summary>
    internal static async Task<DatabaseInspection> InspectAsync(ConnectionCatalog catalog, IReportConnectionFactory connections, SavedReportStoreConfig config, IReadOnlyList<string> tables, CancellationToken ct)
    {
        await using var connection = Inspectable(catalog, connections, config.ConnectionName);
        return await DatabaseInspector.InspectAsync(connection, tables, config.Dialect == ReportDialect.Oracle11g, ct);
    }

    /// <summary>A connection AppBlocks knows comes from its catalog; the engine's own data sources open as they are and ensure nothing.</summary>
    private static DbConnection Inspectable(ConnectionCatalog catalog, IReportConnectionFactory connections, string name)
    {
        try { return catalog.Inspectable(name); }
        catch (ArgumentException) { return connections.CreateConnection(name); }
    }

    private const string ProbeReport = "__appblocks-saved-view-check";

    private void Report(Exception? error, string reason)
    {
        if (Interlocked.Exchange(ref reported, reason) == reason) return;
        var detail = error is null ? "" : " " + DbErrorClassifier.Classify(ReportDialect.SqlServer, error).Summary;
        logger.LogWarning(error, "Saved report views are off: {Reason}{Detail} Grant the permission or run the script shown in Settings > AppBlocks > Database setup. Reports keep working, and saved views return without a restart once this is done.",
            reason, detail);
    }

    private async Task<T> ReadAsync<T>(Func<Task<T>> read, T nothing, CancellationToken ct)
    {
        if (!await ReadyAsync(ct)) return nothing;
        try { return await read(); }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            // It worked before and has stopped, so the next request looks again.
            usable.Clear();
            Report(error, "the saved-report table stopped answering.");
            return nothing;
        }
    }

    public Task<SavedReport?> Get(long id, CancellationToken ct = default) => ReadAsync(() => inner.Get(id, ct), null, ct);
    public Task<SavedReport?> FindConfiguredFile(string reportName, string sourceFile, CancellationToken ct = default) => inner.FindConfiguredFile(reportName, sourceFile, ct);
    public Task<IReadOnlyList<SavedReport>> ListFamily(string reportName, CancellationToken ct = default) => ReadAsync(() => inner.ListFamily(reportName, ct), [], ct);
    public Task<SavedReport?> FindDefault(string reportName, CancellationToken ct = default) => ReadAsync(() => inner.FindDefault(reportName, ct), null, ct);
    public Task<SavedReport?> FindTitleCollision(string reportName, string title, string? owner, bool isPublic, long? exceptId = null, CancellationToken ct = default)
        => ReadAsync(() => inner.FindTitleCollision(reportName, title, owner, isPublic, exceptId, ct), null, ct);
    public Task<IReadOnlyList<SavedReport>> ListAll(CancellationToken ct = default) => ReadAsync(() => inner.ListAll(ct), [], ct);
    public Task<IReadOnlyList<string>> ListOwners(CancellationToken ct = default) => ReadAsync(() => inner.ListOwners(ct), [], ct);
    public Task Create(SavedReport report, CancellationToken ct = default) => inner.Create(report, ct);
    public Task<bool> Update(SavedReport report, SavedReport expected, CancellationToken ct = default) => inner.Update(report, expected, ct);
    public Task<bool> ReplaceDefault(SavedReport report, SavedReport expected, SavedReport currentDefault, CancellationToken ct = default) => inner.ReplaceDefault(report, expected, currentDefault, ct);
    public Task<bool> Delete(SavedReport expected, CancellationToken ct = default) => inner.Delete(expected, ct);
    public Task<bool> Delete(long id, CancellationToken ct = default) => inner.Delete(id, ct);
}
