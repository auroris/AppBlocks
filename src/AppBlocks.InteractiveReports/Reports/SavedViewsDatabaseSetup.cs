using AppBlocks.Connections;
using AppBlocks.Databases;
using InteractiveReport.AspNetCore;
using InteractiveReport.Core.Authorization;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;
using Microsoft.Extensions.Options;

namespace AppBlocks.InteractiveReports.Reports;

/// <summary>
/// The saved-view tables on the setup screen. Its script is what the report stores themselves run. The administrator table
/// is included because the Interactive Reports administration page reads it, even though AppBlocks decides who is an
/// administrator itself, but nothing a reader does needs it, so it does not decide the feature's standing.
/// </summary>
internal sealed class SavedViewsDatabaseSetup(ISavedReportStore saved, IAdministratorStore administrators, ConnectionCatalog catalog, IReportConnectionFactory connections,
    IConfiguration configuration, IOptionsMonitor<InteractiveReportOptions> options) : IDatabaseSetupFeature
{
    private const string Title = "Saved report views";
    private const string Lost = "Without them, people can't save report views or open saved ones. Reports still load, query and export, each on its own starting view.";

    public string Alias => "saved-views";

    public async Task<DatabaseFeatureStatus> StatusAsync(CancellationToken ct)
    {
        if (Store() is not { } store)
            return new(Alias, Title, "Site-provided store", ConnectionCatalog.UnknownProvider, DatabaseFeatureState.SiteManaged, "This site replaced the saved-report store with its own, so its storage is the site's to set up.", Lost, [], null, null, false);
        SavedReportStoreConfig config;
        try { config = store.Configuration; }
        catch (InvalidOperationException)
        {
            return new(Alias, Title, "Not configured", ConnectionCatalog.UnknownProvider, DatabaseFeatureState.Unreachable,
                "InteractiveReport:SavedReports does not name a usable connection. Check the site's configuration.", Lost, [], null, null, false);
        }
        var optional = AdministratorTable() is { } table ? new[] { table } : [];
        var shown = Shown(config.ConnectionName);
        DatabaseInspection inspection;
        try { inspection = await SavedViewStore.InspectAsync(catalog, connections, config, [config.TableName, .. optional], ct); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return new(Alias, Title, shown, Provider(config.Dialect), DatabaseFeatureState.Unreachable,
                $"The saved-report store names {shown}, which is not a connection AppBlocks can open. Check the site's configuration.", Lost, [], null, null, false);
        }
        var provider = Provider(ActualDialect(config.Dialect, inspection));
        var (state, summary) = DatabaseSetupGuide.Standing(inspection, [config.TableName], config.AutoCreate);
        var (text, sql) = DatabaseSetupGuide.Grant(provider, inspection.Account, inspection.Schema);
        return new(Alias, Title, shown, provider, state, summary, Lost, DatabaseSetupGuide.Tables(inspection, [config.TableName], optional), text, sql, true);
    }

    public async Task<string?> ScriptAsync(CancellationToken ct)
    {
        if (Store() is not { } store) return null;
        SavedReportStoreConfig config;
        try { config = store.Configuration; }
        catch (InvalidOperationException) { return null; }
        var administrator = administrators as SqlAdministratorStore;
        DatabaseInspection? inspection = null;
        try { inspection = await SavedViewStore.InspectAsync(catalog, connections, config, [], ct); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { }
        var dialect = ActualDialect(config.Dialect, inspection);
        var provider = Provider(dialect);
        var statements = SqlSavedReportStore.SchemaStatements(config with { Dialect = dialect }).ToList();
        var tables = new List<string> { config.TableName };
        if (administrator is not null)
        {
            statements.AddRange(SqlAdministratorStore.SchemaStatements(administrator.Configuration with { Dialect = dialect }));
            tables.Add(administrator.Configuration.TableName);
        }
        return DatabaseSetupGuide.Header(Title, Shown(config.ConnectionName), provider, inspection?.Account, inspection?.Schema)
            + DatabaseSetupGuide.Join(provider, statements) + DatabaseSetupGuide.TableGrants(provider, inspection?.Account, tables);
    }

    private SqlSavedReportStore? Store() => saved switch { SavedViewStore wrapper => wrapper.Inner as SqlSavedReportStore, SqlSavedReportStore store => store, _ => null };

    private string? AdministratorTable()
    {
        try { return (administrators as SqlAdministratorStore)?.Configuration.TableName; }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>A store's own data source gets a generated name, and its setting may be a whole connection string, which must never be shown.</summary>
    private string Shown(string name)
    {
        var item = catalog.List().FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (item is not null) return item.Label == item.Name ? item.Name : $"{item.Label} ({item.Name})";
        if (!name.StartsWith("__ir:", StringComparison.Ordinal)) return name;
        var source = options.CurrentValue.SavedReports.DataSource;
        return source is not null && configuration.GetConnectionString(source) is { Length: > 0 } ? source : "The data source in InteractiveReport:SavedReports";
    }

    /// <summary>An Oracle server older than 12 gets the 11.2 form of everything, as the store itself decides when it opens.</summary>
    private static ReportDialect ActualDialect(ReportDialect dialect, DatabaseInspection? inspection)
        => dialect == ReportDialect.Oracle && int.TryParse(inspection?.ServerVersion?.Split('.')[0], out var major) && major < 12 ? ReportDialect.Oracle11g : dialect;

    private static string Provider(ReportDialect dialect)
        => ReportConnectionBridge.TryFromReportDialect(dialect, out var kind) ? kind.ProviderName() : ConnectionCatalog.UnknownProvider;
}
