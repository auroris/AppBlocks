using System.Data.Common;
using AppBlocks.Connections;
using AppBlocks.Databases;
using AppBlocks.InteractiveReports.Connections;
using AppBlocks.InteractiveReports.Reports;
using AppBlocks.Tests;
using InteractiveReport.AspNetCore;
using InteractiveReport.Core.Authorization;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AppBlocks.InteractiveReports.Tests;

/// <summary>
/// The store the report engine gets on an AppBlocks site, and the saved views' side of the setup screen. SQLite's read-only
/// connections stand in for "no rights" where the database type does not matter; the SQL Server cases sign in as a real
/// low-privilege login. <see cref="DatabaseSetupSiteTests"/> does the same through a whole site's endpoints.
/// </summary>
public sealed class SavedViewStoreTests
{
    private static SavedReport View(string title = "Mine") => new() { ReportName = "tasks", Title = title, Owner = "someone", StateJson = "{}" };

    [Fact]
    public async Task A_fresh_store_is_created_on_first_use_and_looking_first_creates_nothing()
    {
        var root = Directory.CreateTempSubdirectory("saved-views-").FullName;
        var file = Path.Combine(root, "views.db");
        await using var site = Site($"Data Source={file};Pooling=False", text => new SqliteConnection(text));

        var status = await site.Setup.StatusAsync(default);
        Assert.Equal(DatabaseFeatureState.Automatic, status.State);
        Assert.Contains("IR_SAVED_REPORTS", await site.Setup.ScriptAsync(default));
        Assert.False(File.Exists(file));

        Assert.True(await site.Store.ReadyAsync());
        await site.Store.Create(View());
        Assert.Equal("Mine", Assert.Single(await site.Store.ListFamily("tasks")).Title);
        Assert.Equal(DatabaseFeatureState.Ready, (await site.Setup.StatusAsync(default)).State);
        Assert.Empty(site.Warnings);
    }

    [Fact]
    public async Task Without_the_table_reads_answer_as_an_empty_store_and_the_remedy_works_while_running()
    {
        // A file marked read-only: SQLite has no accounts, and this refuses both the DDL and every write.
        var file = Path.Combine(Directory.CreateTempSubdirectory("saved-views-").FullName, "views.db");
        await Run(file, "CREATE TABLE OTHER (ID INTEGER)");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        await using var site = Site($"Data Source={file};Pooling=False", text => new SqliteConnection(text));

        Assert.False(await site.Store.ReadyAsync());
        Assert.Null(await site.Store.FindDefault("tasks"));
        Assert.Null(await site.Store.Get(1));
        Assert.Empty(await site.Store.ListFamily("tasks"));
        Assert.Empty(await site.Store.ListAll());
        Assert.Empty(await site.Store.ListOwners());
        Assert.Null(await site.Store.FindTitleCollision("tasks", "Mine", "someone", false));
        Assert.Equal(DatabaseFeatureState.Action, (await site.Setup.StatusAsync(default)).State);

        // However often it is asked, it said so once, with what to do.
        var warning = Assert.Single(site.Warnings);
        Assert.Contains("Database setup", warning);
        Assert.Contains("IR_SAVED_REPORTS", warning);

        // Someone makes the file writable and runs the printed script. The site is not restarted.
        var script = (await site.Setup.ScriptAsync(default))!;
        File.SetAttributes(file, FileAttributes.Normal);
        await Run(file, script);
        Assert.True(await site.Store.ReadyAsync());
        await site.Store.Create(View());
        Assert.Equal("Mine", Assert.Single(await site.Store.ListFamily("tasks")).Title);
        Assert.Single(site.Warnings);
        Assert.Contains(site.Logs.Entries, entry => entry.Level == LogLevel.Information && entry.Message.Contains("work again"));
    }

    [Fact]
    public async Task A_connection_opened_read_only_finds_the_table_but_keeps_saving_off()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("saved-views-").FullName, "views.db");
        await using var site = Site($"Data Source={file};Pooling=False;Mode=ReadOnly", text => new SqliteConnection(text));
        await Run(file, (await site.Setup.ScriptAsync(default))!);
        Assert.False(await site.Store.ReadyAsync());
        var status = await site.Setup.StatusAsync(default);
        Assert.Equal(DatabaseFeatureState.Action, status.State);
        Assert.Equal("noAccess", status.Tables[0].State);
        Assert.Empty(await site.Store.ListFamily("tasks"));
    }

    [Fact]
    public async Task A_table_that_stops_answering_reads_as_empty_and_is_looked_for_again()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("saved-views-").FullName, "views.db");
        await using var site = Site($"Data Source={file};Pooling=False", text => new SqliteConnection(text));
        Assert.True(await site.Store.ReadyAsync());
        await site.Store.Create(View());
        await Run(file, "DROP TABLE IR_SAVED_REPORTS");
        Assert.Empty(await site.Store.ListFamily("tasks"));
        Assert.Single(site.Warnings);
    }

    [Fact]
    public async Task A_table_that_is_not_the_one_the_store_needs_reads_as_empty_and_is_logged_once()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("saved-views-").FullName, "views.db");
        await Run(file, "CREATE TABLE IR_SAVED_REPORTS (ID INTEGER)");
        await using var site = Site($"Data Source={file};Pooling=False", text => new SqliteConnection(text));
        for (var i = 0; i < 3; i++) Assert.Empty(await site.Store.ListFamily("tasks"));
        Assert.Single(site.Warnings);
        Assert.DoesNotContain(site.Logs.Entries, entry => entry.Message.Contains("work again"));
    }

    [Fact]
    public async Task A_literal_connection_string_in_the_settings_is_never_shown_and_looking_creates_nothing()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("saved-views-").FullName, "views.db");
        await using var site = Site("", _ => throw new InvalidOperationException(), dataSource: $"Data Source={file};Pooling=False");
        var status = await site.Setup.StatusAsync(default);
        Assert.Equal(DatabaseFeatureState.Automatic, status.State);
        Assert.Equal("The data source in InteractiveReport:SavedReports", status.Connection);
        Assert.DoesNotContain(file.Replace("\\", "\\\\"), System.Text.Json.JsonSerializer.Serialize(status));
        var script = await site.Setup.ScriptAsync(default);
        Assert.Contains("IR_SAVED_REPORTS", script);
        Assert.DoesNotContain(file, script);
        Assert.False(File.Exists(file));
        Assert.True(await site.Store.ReadyAsync());
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task A_replaced_store_is_the_sites_own_business()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("saved-views-").FullName, "views.db");
        await using var site = Site($"Data Source={file};Pooling=False", text => new SqliteConnection(text));
        var replaced = new SavedViewStore(new OtherStore(), site.Catalog, site.Connections, site.Provider.GetRequiredService<ILogger<SavedViewStore>>());
        Assert.True(await replaced.ReadyAsync());
        var setup = new SavedViewsDatabaseSetup(replaced, new SqlAdministratorStore(() => throw new InvalidOperationException(), site.Connections), site.Catalog, site.Connections, site.Provider.GetRequiredService<IConfiguration>(),
            site.Provider.GetRequiredService<IOptionsMonitor<InteractiveReportOptions>>());
        var status = await setup.StatusAsync(default);
        Assert.Equal(DatabaseFeatureState.SiteManaged, status.State);
        Assert.False(status.HasScript);
        Assert.Null(await setup.ScriptAsync(default));
    }

    [Fact]
    public async Task A_store_with_no_auto_create_is_told_apart_from_one_that_cannot()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("saved-views-").FullName, "views.db");
        await using var site = Site($"Data Source={file};Pooling=False", text => new SqliteConnection(text), autoCreate: false);
        var status = await site.Setup.StatusAsync(default);
        Assert.Equal(DatabaseFeatureState.Action, status.State);
        Assert.Contains("turned off", status.Summary);
        Assert.False(await site.Store.ReadyAsync());
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task A_postgres_store_that_is_not_there_is_unreachable_and_shows_no_connection_string()
    {
        const string secret = "Host=127.0.0.1;Port=1;Database=nothing;Username=someone;Password=never-shown;Timeout=1";
        await using var site = Site(secret, text => new NpgsqlConnection(text));
        Assert.False(await site.Store.ReadyAsync());
        var status = await site.Setup.StatusAsync(default);
        Assert.Equal(DatabaseFeatureState.Unreachable, status.State);
        Assert.Equal("postgres", status.Provider);
        Assert.DoesNotContain("never-shown", System.Text.Json.JsonSerializer.Serialize(status));
        var script = await site.Setup.ScriptAsync(default);
        Assert.Contains("CREATE TABLE IF NOT EXISTS \"IR_SAVED_REPORTS\"", script);
        Assert.Contains("CREATE TABLE IF NOT EXISTS \"IR_ADMINISTRATORS\"", script);
        Assert.Contains("GRANT SELECT, INSERT, UPDATE, DELETE ON \"IR_SAVED_REPORTS\"", script);
        Assert.DoesNotContain("never-shown", script);
    }

    [Theory]
    [InlineData(ReportDialect.Oracle, "oracle", false)]
    [InlineData(ReportDialect.Oracle11g, "oracle11g", true)]
    [InlineData(ReportDialect.Postgres, "postgres", false)]
    public void The_script_for_a_database_this_repository_cannot_run_still_ends_every_statement_for_its_tool(ReportDialect dialect, string provider, bool trigger)
    {
        var statements = SqlSavedReportStore.SchemaStatements(new SavedReportStoreConfig("Db", dialect));
        var script = DatabaseSetupGuide.Join(provider, statements);
        var ending = provider == "postgres" ? ";\n" : "\n/\n";
        Assert.Equal(statements.Count, script.Split(ending).Length - 1);
        Assert.Equal(trigger, script.Contains("CREATE OR REPLACE TRIGGER", StringComparison.Ordinal));
        Assert.Equal(trigger, script.Contains("CREATE SEQUENCE", StringComparison.Ordinal));
        Assert.Contains("IR_SAVED_REPORTS_TITLE_UX", script);
    }

    [LocalDbFact]
    public async Task The_script_lets_an_account_with_no_ddl_rights_use_both_tables_and_the_engine_store_needs_no_rights_to_notice()
    {
        await using var database = await RestrictedSqlServer.CreateAsync();
        await using var site = Site(database.AccountConnection, text => new SqlConnection(text));
        Assert.False(await site.Store.ReadyAsync());
        var before = await site.Setup.StatusAsync(default);
        Assert.Equal(DatabaseFeatureState.Action, before.State);
        Assert.Equal(["missing", "missing"], before.Tables.Select(table => table.State));
        Assert.Equal([true, false], before.Tables.Select(table => table.Required));
        Assert.Equal(0, await database.TableCount());

        await database.Admin((await site.Setup.ScriptAsync(default))!);
        Assert.Equal(0, await database.AccountScalar<int>("SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE TABLE')"));

        Assert.True(await site.Store.ReadyAsync());
        var view = View();
        await site.Store.Create(view);
        Assert.Equal("Mine", (await site.Store.Get(view.Id))!.Title);
        Assert.Equal("someone", Assert.Single(await site.Store.ListOwners()));
        var administrators = site.Provider.GetRequiredService<IAdministratorStore>();
        await administrators.Grant("someone");
        Assert.True(await administrators.IsAdministrator("someone"));
        Assert.True(await site.Store.Delete(view.Id));
        SqlConnection.ClearAllPools();
    }

    [LocalDbFact]
    public async Task The_engines_own_store_uses_a_hand_made_table_when_its_ddl_is_refused_on_sql_server()
    {
        await using var database = await RestrictedSqlServer.CreateAsync();
        await using var site = Site(database.AccountConnection, text => new SqlConnection(text));
        await database.Admin((await site.Setup.ScriptAsync(default))!);
        // Without its indexes the store's guarded DDL has real work to do, which this account is refused, as on Oracle and PostgreSQL even when all is in place.
        await database.Admin("DROP INDEX IR_SAVED_REPORTS_TITLE_UX ON IR_SAVED_REPORTS;");
        var engine = (SqlSavedReportStore)site.Store.Inner;
        await engine.Create(View());
        Assert.Single(await engine.ListFamily("tasks"));
        SqlConnection.ClearAllPools();
    }

    private static async Task Run(string file, string script)
    {
        await using var connection = new SqliteConnection($"Data Source={file};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = script;
        await command.ExecuteNonQueryAsync();
    }

    private static Fixture Site(string connectionString, Func<string, DbConnection> open, bool autoCreate = true, string? dataSource = null)
    {
        var root = Directory.CreateTempSubdirectory("saved-views-site-").FullName;
        const string name = "ViewsDb";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["AppBlocks:DataRoot"] = root,
            ["InteractiveReport:SavedReports:Connection"] = dataSource is null ? name : null,
            ["InteractiveReport:SavedReports:DataSource"] = dataSource,
            ["InteractiveReport:SavedReports:Provider"] = dataSource is null ? null : "sqlite",
            ["InteractiveReport:SavedReports:AutoCreate"] = autoCreate.ToString(),
        }).Build();
        var logs = new RecordingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(logs));
        services.AddSingleton<IConfiguration>(configuration);
        var environment = new Environment(root);
        services.AddSingleton<IWebHostEnvironment>(environment);
        var connections = new AppBlocksBuilder().AddConnection(name, _ => open(connectionString));
        services.AddInteractiveReports(configuration).AddConnectionsFrom(connections);
        var provider = services.BuildServiceProvider();
        var catalog = new ConnectionCatalog(configuration, environment, new EphemeralDataProtectionProvider(), connections, provider);
        var factory = new ManagedConnectionFactory(provider.GetRequiredService<IReportConnectionFactory>(), catalog);
        var store = new SavedViewStore(provider.GetRequiredService<ISavedReportStore>(), catalog, factory, provider.GetRequiredService<ILogger<SavedViewStore>>());
        var setup = new SavedViewsDatabaseSetup(store, provider.GetRequiredService<IAdministratorStore>(), catalog, factory, configuration, provider.GetRequiredService<IOptionsMonitor<InteractiveReportOptions>>());
        return new(provider, logs, catalog, factory, store, setup);
    }

    private sealed record Fixture(ServiceProvider Provider, RecordingLoggerProvider Logs, ConnectionCatalog Catalog, IReportConnectionFactory Connections, SavedViewStore Store, SavedViewsDatabaseSetup Setup) : IAsyncDisposable
    {
        public IEnumerable<string> Warnings => Logs.Entries.Where(entry => entry.Level == LogLevel.Warning).Select(entry => entry.Message);
        public async ValueTask DisposeAsync() => await Provider.DisposeAsync();
    }

    /// <summary>A store a site brought itself, which AppBlocks knows nothing about.</summary>
    private sealed class OtherStore : ISavedReportStore
    {
        public Task<SavedReport?> Get(long id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SavedReport?> FindConfiguredFile(string reportName, string sourceFile, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SavedReport>> ListFamily(string reportName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SavedReport?> FindDefault(string reportName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SavedReport?> FindTitleCollision(string reportName, string title, string? owner, bool isPublic, long? exceptId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SavedReport>> ListAll(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListOwners(CancellationToken ct = default) => throw new NotSupportedException();
        public Task Create(SavedReport report, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> Update(SavedReport report, SavedReport expected, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> ReplaceDefault(SavedReport report, SavedReport expected, SavedReport currentDefault, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> Delete(SavedReport expected, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> Delete(long id, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Environment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "AppBlocks.InteractiveReports.Tests";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
