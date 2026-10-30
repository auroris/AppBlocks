using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using AppBlocks.Connections;
using AppBlocks.Samples;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using UmbracoAppHost.Blocks;
using UmbracoAppHost.Forms;

namespace AppBlocks.Tests;

public sealed class PackageTests
{
    private sealed class Environment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "AppBlocks.Tests";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.Combine(root, "wwwroot");
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public async Task Connections_default_to_Umbraco_and_round_trip_encrypted_changes_without_a_restart()
    {
        var root = Directory.CreateTempSubdirectory("appblocks-connections-").FullName;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:umbracoDbDSN"] = "Data Source=" + Path.Combine(root, "cms.db"),
            ["ConnectionStrings:umbracoDbDSN_ProviderName"] = "Microsoft.Data.Sqlite",
        }).Build();
        var protection = new EphemeralDataProtectionProvider();
        var catalog = new ConnectionCatalog(config, new Environment(root), protection);
        var defaults = Assert.Single(catalog.List());
        Assert.True(defaults.IsDefault); Assert.True(defaults.ReadOnly); Assert.Equal("sqlite", defaults.Provider);
        using (var connection = catalog.Openable("UmbracoDb")) Assert.Contains("cms.db", connection.ConnectionString);
        const string secret = "A-connection-secret-that-must-not-be-exported";
        await catalog.SaveAsync(new("Business", "sqlServer", "Server=localhost;User ID=app;Password=" + secret), default);
        Assert.DoesNotContain(secret, File.ReadAllText(Path.Combine(root, "App_Data", "AppBlocks", "connections.json")));
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(catalog.List()));
        var reopened = new ConnectionCatalog(config, new Environment(root), protection);
        using (var connection = reopened.Openable("Business")) Assert.Contains(secret, connection.ConnectionString);
        var before = reopened.Revision("Business");
        await catalog.SaveAsync(new("Business", "sqlServer", "Server=another-server;User ID=app;Password=" + secret), default);
        Assert.NotEqual(before, reopened.Revision("Business"));
        using (var connection = reopened.Openable("Business")) Assert.Contains("another-server", connection.ConnectionString);
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.SaveAsync(new("UmbracoDb", "sqlite", "Data Source=other.db"), default));
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.SaveAsync(new("../escape", "sqlite", "Data Source=other.db"), default));
    }

    [Fact]
    public async Task Testing_a_missing_SQLite_file_does_not_create_it()
    {
        var root = Directory.CreateTempSubdirectory("appblocks-test-").FullName;
        var file = Path.Combine(root, "missing.db");
        var catalog = new ConnectionCatalog(new ConfigurationBuilder().Build(), new Environment(root), new EphemeralDataProtectionProvider());
        await Assert.ThrowsAsync<SqliteException>(() => catalog.TestAsync(new("Example", "sqlite", "Data Source=" + file), default));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Discovery_keeps_multiple_forms_and_their_nested_fields_separate()
    {
        var first = Guid.NewGuid(); var second = Guid.NewGuid(); var a = Guid.NewGuid(); var b = Guid.NewGuid();
        object Block(Guid key, string alias, string field = "") => new
        {
            key, contentTypeKey = AppBlockSchema.Key(alias), values = new[] { new { alias = "field", value = field } },
        };
        object Layout(Guid form, Guid field) => new { contentKey = form, areas = new[] { new { alias = "fields", items = new[] { new { contentKey = field, areas = Array.Empty<object>() } } } } };
        var json = JsonSerializer.Serialize(new
        {
            contentData = new[] { Block(first, "appForm"), Block(a, "appText", "title"), Block(second, "appForm"), Block(b, "appNumber", "amount") },
            layout = new Dictionary<string, object> { ["Umbraco.BlockGrid"] = new[] { Layout(first, a), Layout(second, b) } },
        });
        var forms = FormInventory.Parse(Guid.NewGuid(), "Draft page", false, "fr", json);
        Assert.Equal(2, forms.Count);
        Assert.Equal("title", Assert.Single(forms[0].Fields).Key);
        Assert.Equal("amount", Assert.Single(forms[1].Fields).Key);
        Assert.All(forms, f => { Assert.False(f.Published); Assert.Equal("fr", f.Culture); });
    }

    [Fact]
    public async Task Generated_sample_compiles_and_round_trips_values_with_auth_and_concurrency()
    {
        var form = new DiscoveredForm(Guid.NewGuid(), Guid.NewGuid(), null, "Example", "Example", "", false,
            [new("title\"with-characters", "Title", "text", Required: true, MaxLength: 100), new("amount", "Amount", "number"), new("done", "Done", "checkbox")]);
        var source = FormSampleGenerator.Generate(form);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(SqliteConnection).Assembly.Location).Distinct().Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("GeneratedSample" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText("global using System; global using System.Threading; global using System.Threading.Tasks;\n" + source)],
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var assembly = Assembly.Load(stream.ToArray());
        var type = assembly.GetType("AppBlocks.Samples.Generated.SampleFormRepository")!;
        var valuesType = assembly.GetType("AppBlocks.Samples.Generated.SampleFormValues")!;
        var recordType = assembly.GetType("AppBlocks.Samples.Generated.SampleFormRecord")!;
        var root = Directory.CreateTempSubdirectory("appblocks-sample-").FullName;
        var connectionString = "Data Source=" + Path.Combine(root, "sample.db");
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = (string)type.GetField("ExampleSchema")!.GetRawConstantValue()!;
            await command.ExecuteNonQueryAsync();
        }
        var repository = Activator.CreateInstance(type, connectionString, (Func<ClaimsPrincipal, bool>)(_ => true))!;
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "member-1")], "test"));
        var values = Activator.CreateInstance(valuesType)!;
        valuesType.GetProperty("Field1")!.SetValue(values, "O'Brien; DROP TABLE SampleForm; --");
        valuesType.GetProperty("Field2")!.SetValue(values, 123.45m);
        valuesType.GetProperty("Field3")!.SetValue(values, true);
        async Task<bool> Save(long version) => await (Task<bool>)type.GetMethod("UpsertAsync")!.Invoke(repository, ["record-1", version, values, principal, CancellationToken.None])!;
        Assert.True(await Save(0));
        Assert.False(await Save(0));
        var read = (Task)type.GetMethod("ReadAsync")!.Invoke(repository, ["record-1", principal, CancellationToken.None])!;
        await read;
        var record = read.GetType().GetProperty("Result")!.GetValue(read)!;
        var actualValues = recordType.GetProperty("Values")!.GetValue(record);
        Assert.Equal("O'Brien; DROP TABLE SampleForm; --", valuesType.GetProperty("Field1")!.GetValue(actualValues));
        Assert.True(await Save(1)); Assert.False(await Save(1));
        var denied = (Task)type.GetMethod("ReadAsync")!.Invoke(repository, ["record-1", new ClaimsPrincipal(), CancellationToken.None])!;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => denied);
    }
}
