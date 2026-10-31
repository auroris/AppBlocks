using AppBlocks.Connections;
using AppBlocks.Databases;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

namespace AppBlocks.Tests;

/// <summary>An application database's schema belongs to its name, whichever source supplies its connection string.</summary>
public sealed class ApplicationDatabaseResolutionTests : IDisposable
{
    private const string Name = "InnDb";
    private readonly string root = Directory.CreateTempSubdirectory("appblocks-app-db-").FullName;
    private readonly List<ServiceProvider> providers = [];
    private int steps;

    private string DefaultFile => Path.Combine(root, "App_Data", Name + ".sqlite.db");
    private string OtherFile => Path.Combine(root, "other.db");
    private string OtherConnection => $"Data Source={OtherFile};Pooling=False";

    public void Dispose()
    {
        foreach (var provider in providers) provider.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void A_configured_connection_string_gets_the_schema_once_per_process()
    {
        var site = Site(Configured());
        Open(site.Connections);
        Open(site.Connections);
        Assert.True(HasTables(OtherFile));
        Assert.False(File.Exists(DefaultFile));
        Assert.Equal(1, steps);
    }

    [Fact]
    public async Task A_saved_connection_wins_over_the_registration_and_gets_the_schema()
    {
        var site = Site();
        await site.Unregistered.SaveAsync(new(Name, "sqlite", OtherConnection), default);
        Open(site.Connections);
        Assert.True(HasTables(OtherFile));
        Assert.False(File.Exists(DefaultFile));
        Assert.Equal(1, steps);
    }

    [Fact]
    public void A_registration_with_no_connection_string_still_gets_the_schema_in_its_default_file()
    {
        var site = Site();
        Open(site.Connections);
        Assert.True(HasTables(DefaultFile));
        Assert.Equal(1, steps);
    }

    [Fact]
    public void Registering_the_name_again_as_a_plain_connection_takes_the_schema_away()
    {
        var site = Site(Configured());
        site.Builder.AddConnection(Name, _ => new SqliteConnection(OtherConnection));
        Open(site.Connections);
        Assert.False(HasTables(OtherFile));
        Assert.Equal(0, steps);
    }

    [Fact]
    public void A_failed_ensure_fails_the_open_and_the_next_open_tries_again()
    {
        var schemaRoot = SchemaFolder("CREATE TABEL NOPE (ID INTEGER);");
        var site = Site(ApplicationSchema.FromDirectory(schemaRoot), Configured());
        using (var failing = site.Connections.CreateConnection(Name))
            Assert.Throws<ApplicationSchemaException>(() => failing.Open());
        File.WriteAllText(Path.Combine(schemaRoot, "sqlite", "001-things.sql"), "CREATE TABLE IF NOT EXISTS THINGS (ID INTEGER PRIMARY KEY);");
        Open(site.Connections);
        Assert.True(HasTables(OtherFile));
    }

    /// <summary>The table picker lists what the data source will find, so it opens as any use does.</summary>
    [Fact]
    public async Task Listing_the_tables_of_a_configured_application_database_finds_its_schema()
    {
        var site = Site(Configured());
        var tables = await new ConnectionTables(site.Catalog).ListAsync(Name, default);
        Assert.Contains(tables, table => table.Name == "THINGS");
        Assert.Equal(1, steps);
    }

    /// <summary>The backoffice's Test button says whether a database can be reached, and leaves it as it found it.</summary>
    [Theory]
    [InlineData("configured")]
    [InlineData("saved")]
    [InlineData("registered")]
    [InlineData("typed")]
    public async Task Testing_a_connection_never_ensures_the_schema(string source)
    {
        var site = source == "configured" ? Site(Configured()) : Site();
        if (source == "saved") await site.Unregistered.SaveAsync(new(Name, "sqlite", OtherConnection), default);
        var file = source == "registered" ? DefaultFile : OtherFile;
        // SQLite is tested read-only, so what is tested has to exist.
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using (var empty = new SqliteConnection($"Data Source={file};Pooling=False")) empty.Open();

        await site.Catalog.TestAsync(new(Name, "sqlite", source == "typed" ? OtherConnection : null), default);

        Assert.False(HasTables(file));
        Assert.Equal(0, steps);
    }

    private static void Open(IApplicationConnectionFactory connections)
    {
        using var connection = connections.CreateConnection(Name);
        connection.Open();
    }

    private (string, string?)[] Configured() => [($"ConnectionStrings:{Name}", OtherConnection), ($"ConnectionStrings:{Name}_ProviderName", "Microsoft.Data.Sqlite")];

    private Fixture Site(params (string Key, string? Value)[] settings)
        => Site(ApplicationSchema.FromDirectory(SchemaFolder("CREATE TABLE IF NOT EXISTS THINGS (ID INTEGER PRIMARY KEY);")).Then((_, _) => steps++), settings);

    private Fixture Site(ApplicationSchema schema, params (string Key, string? Value)[] settings)
    {
        var values = new Dictionary<string, string?> { ["AppBlocks:DataRoot"] = root };
        foreach (var (key, value) in settings) values[key] = value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var environment = new Environment(root);
        var protection = new EphemeralDataProtectionProvider();
        var builder = new AppBlocksBuilder().AddApplicationDatabase(Name, schema);
        var collection = new ServiceCollection();
        collection.AddSingleton<IConfiguration>(configuration);
        collection.AddSingleton<IWebHostEnvironment>(environment);
        var provider = collection.BuildServiceProvider();
        providers.Add(provider);
        var catalog = new ConnectionCatalog(configuration, environment, protection, builder, provider);
        return new(builder, catalog, new(configuration, environment, protection), new ApplicationConnections(provider, builder, catalog));
    }

    private string SchemaFolder(string script)
    {
        var folder = Path.Combine(root, "schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "sqlite"));
        File.WriteAllText(Path.Combine(folder, "sqlite", "001-things.sql"), script);
        return folder;
    }

    private static bool HasTables(string file)
    {
        using var connection = new SqliteConnection($"Data Source={file};Pooling=False;Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('THINGS', 'APP_SCHEMA')";
        return (long)command.ExecuteScalar()! > 0;
    }

    private sealed record Fixture(AppBlocksBuilder Builder, ConnectionCatalog Catalog, ConnectionCatalog Unregistered, ApplicationConnections Connections);

    private sealed class Environment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "AppBlocks.Tests";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
