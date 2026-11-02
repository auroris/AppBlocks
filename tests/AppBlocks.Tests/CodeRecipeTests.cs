using System.Security.Claims;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AppBlocks.Code;
using AppBlocks.Connections;
using AppBlocks.DataSources;
using AppBlocks.Forms;
using AppBlocks.Samples;

namespace AppBlocks.Tests;

/// <summary>
/// Every wizard recipe, for every access choice and every database, compiles as the C# folder compiles it: the framework, AppBlocks and
/// Umbraco referenced and the Web SDK's implicit usings in place. A SQLite form class then runs its whole CRUD cycle after compiling at run time.
/// </summary>
public sealed class CodeRecipeTests
{
    private static readonly Lazy<MetadataReference[]> References = new(() => AppDomain.CurrentDomain.GetAssemblies()
        .Where(a => !a.IsDynamic && a.Location.Length > 0).Select(a => a.Location)
        .Concat(((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator))
        .DistinctBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
        .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToArray());

    public static TheoryData<DatabaseDialect, CodeRecipeKind, string> Recipes()
    {
        var data = new TheoryData<DatabaseDialect, CodeRecipeKind, string>();
        foreach (var dialect in Enum.GetValues<DatabaseDialect>())
            foreach (var kind in Enum.GetValues<CodeRecipeKind>())
                foreach (var access in Accesses.Keys) data.Add(dialect, kind, access);
        return data;
    }

    private static readonly Dictionary<string, Func<CodeRecipe, CodeRecipe>> Accesses = new() {
        ["public"] = recipe => recipe with { Access = DataSourceAccess.Public },
        ["public, anonymous writes"] = recipe => recipe with { Access = DataSourceAccess.Public, AllowAnonymousWrites = true },
        ["public, write groups"] = recipe => recipe with { Access = DataSourceAccess.Public, WriteUserGroups = ["editor"] },
        ["not public"] = recipe => recipe with { Access = DataSourceAccess.NotPublic },
        ["not public, write groups"] = recipe => recipe with { Access = DataSourceAccess.NotPublic, WriteMemberGroups = ["Order \"editors\""] },
        ["specific member groups"] = recipe => recipe with { Access = DataSourceAccess.SpecificGroups, ReadMemberGroups = ["Buyers"] },
        ["specific groups"] = recipe => recipe with {
            Access = DataSourceAccess.SpecificGroups, ReadMemberGroups = ["Buyers", "Crew"], ReadUserGroups = ["admin"],
            WriteMemberGroups = ["Order editors"], WriteUserGroups = ["editor", "writer"] },
    };

    /// <summary>A table each database could have, with a column of every kind and one a form cannot show.</summary>
    private static TableSchema Table(DatabaseDialect dialect, bool generatedKey = true)
    {
        TableColumn C(string name, string type, bool nullable = true, int? length = null, bool auto = false, bool defaults = false, bool rowVersion = false, bool computed = false)
            => new(name, type, nullable, defaults, auto, computed, rowVersion, length);
        TableColumn[] columns = dialect switch {
            DatabaseDialect.Sqlite => [C("ORDER_ID", generatedKey ? "INTEGER" : "TEXT", false, auto: generatedKey), C("CUSTOMER", "VARCHAR(100)", false, 100), C("STATUS", "TEXT", false, defaults: true),
                C("TOTAL", "NUMERIC"), C("DUE", "DATE"), C("STARTS", "TIME"), C("CREATED", "DATETIME"), C("PAID", "BOOLEAN"), C("SCAN", "BLOB")],
            DatabaseDialect.SqlServer => [C("OrderId", generatedKey ? "int" : "nvarchar", false, generatedKey ? null : 36, auto: generatedKey), C("Customer", "nvarchar", false, 100), C("Status", "nvarchar", false, 20, defaults: true),
                C("Total", "decimal"), C("Due", "date"), C("Starts", "time"), C("Created", "datetime2"), C("Stamped", "datetimeoffset"), C("Paid", "bit"),
                C("Code", "uniqueidentifier"), C("Label", "nvarchar", computed: true), C("Version", "rowversion", false, rowVersion: true)],
            DatabaseDialect.Postgres => [C("order_id", generatedKey ? "int4" : "uuid", false, auto: generatedKey), C("customer", "varchar", false, 100), C("status", "text", false, defaults: true),
                C("total", "numeric"), C("lines", "int2"), C("weight", "float8"), C("due", "date"), C("starts", "time"), C("created", "timestamp"), C("stamped", "timestamptz"),
                C("paid", "bool"), C("reference", "uuid"), C("scan", "bytea")],
            _ => [C("ORDER_ID", "NUMBER", false, auto: generatedKey), C("CUSTOMER", "VARCHAR2", false, 100), C("STATUS", "VARCHAR2", false, 20, defaults: true),
                C("TOTAL", "NUMBER"), C("DUE", "DATE"), C("CREATED", "TIMESTAMP(6)"), C("STAMPED", "TIMESTAMP(6) WITH TIME ZONE"), C("NOTES", "CLOB"), C("SCAN", "BLOB")],
        };
        return new(new TableName(dialect is DatabaseDialect.Sqlite ? null : "sales", "Order lines"), false, columns, [[columns[0].Name]]);
    }

    private static CodeRecipe Recipe(CodeRecipeKind kind) => new() {
        Connection = "Business", Table = "sales.\"Order lines\"", Kind = kind, ClassName = kind == CodeRecipeKind.FormClass ? "OrderLinesForm" : "OrderLinesRules",
        Alias = kind == CodeRecipeKind.FormClass ? "order-lines" : "order-lines.rules", DataSource = "orderLines",
    };

    private static string Generate(CodeRecipe recipe, TableSchema table, DatabaseDialect dialect) => recipe.Kind == CodeRecipeKind.FormClass
        ? CodeRecipes.FormClass(recipe, table, SqlDialect.For(dialect)!)
        : CodeRecipes.BackingClass(recipe, table, SqlDialect.For(dialect)!, [new("customer", "CUSTOMER", "text"), new("total", "TOTAL", "number")]);

    private static IReadOnlyList<Diagnostic> Compile(string code)
    {
        var compilation = CSharpCompilation.Create("Recipe" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(RoslynCompiler.ImplicitUsings), CSharpSyntaxTree.ParseText(code, path: "Recipe.cs")], References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
                .WithSpecificDiagnosticOptions([new("CS1701", ReportDiagnostic.Suppress), new("CS1702", ReportDiagnostic.Suppress), new("CS1705", ReportDiagnostic.Suppress)]));
        using var stream = new MemoryStream();
        return compilation.Emit(stream).Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).ToArray();
    }

    [Theory]
    [MemberData(nameof(Recipes))]
    public void Every_recipe_compiles_without_errors_or_warnings(DatabaseDialect dialect, CodeRecipeKind kind, string access)
    {
        var recipe = Accesses[access](Recipe(kind));
        Assert.Empty(CodeRecipes.Validate(recipe));
        foreach (var generatedKey in kind == CodeRecipeKind.FormClass ? new[] { true, false } : [true])
        {
            var code = Generate(recipe, Table(dialect, generatedKey), dialect);
            var problems = Compile(code);
            Assert.True(problems.Count == 0, string.Join("\n", problems) + "\n\n" + code);
            Assert.Contains("a class with no check is public", code);
            Assert.Contains("CanWrite(", code);
        }
    }

    [Fact]
    public void A_table_without_a_one_column_key_and_a_bad_choice_are_refused()
    {
        var keyless = Table(DatabaseDialect.Sqlite) with { UniqueKeys = [] };
        Assert.Throws<CodeRecipeException>(() => CodeRecipes.FormClass(Recipe(CodeRecipeKind.FormClass), keyless, SqliteDialect.Instance));
        Assert.Throws<CodeRecipeException>(() => CodeRecipes.FormClass(Recipe(CodeRecipeKind.FormClass), Table(DatabaseDialect.Sqlite) with { UniqueKeys = [["ORDER_ID", "CUSTOMER"]] }, SqliteDialect.Instance));
        Assert.NotEmpty(CodeRecipes.Validate(Recipe(CodeRecipeKind.FormClass) with { ClassName = "class" }));
        Assert.NotEmpty(CodeRecipes.Validate(Recipe(CodeRecipeKind.FormClass) with { ClassName = "Order Form" }));
        Assert.NotEmpty(CodeRecipes.Validate(Recipe(CodeRecipeKind.FormClass) with { Alias = "1orders" }));
        Assert.NotEmpty(CodeRecipes.Validate(Recipe(CodeRecipeKind.FormClass) with { Access = DataSourceAccess.SpecificGroups }));
        Assert.NotEmpty(CodeRecipes.Validate(Recipe(CodeRecipeKind.FormClass) with { Access = DataSourceAccess.NotPublic, AllowAnonymousWrites = true }));
        Assert.NotEmpty(CodeRecipes.Validate(Recipe(CodeRecipeKind.FormClass) with { ReadMemberGroups = ["Line\nbreak"] }));
        Assert.Equal(("OrderLinesForm", "order-lines"), CodeRecipes.Suggest("sales.\"ORDER_LINES\"", CodeRecipeKind.FormClass));
        Assert.Equal(("CustomerAccountsRules", "customer-accounts.rules"), CodeRecipes.Suggest("CustomerAccounts", CodeRecipeKind.BackingClass));
    }

    /// <summary>A Member, or nobody, as the generated class's resolver sees them.</summary>
    private sealed class Users : IDataSourceUserResolver
    {
        public static readonly DataSourceUser Buyer = new("1001", null, "buyer", null, ["Buyers"], false);
        public static readonly DataSourceUser Crew = new("1002", null, "crew", null, ["Crew"], false);
        public Task<DataSourceUser?> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct)
            => Task.FromResult(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value switch { "1001" => Buyer, "1002" => Crew, _ => (DataSourceUser?)null });
    }

    private static FormExecutionContext Caller(string? id) => new(id is null ? new ClaimsPrincipal(new ClaimsIdentity())
        : new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "Test")), Guid.NewGuid(), Guid.NewGuid(), "1");

    [Fact]
    public async Task A_generated_SQLite_form_class_runs_its_whole_cycle_once_compiled_at_run_time()
    {
        var directory = Directory.CreateTempSubdirectory("code-recipe-").FullName;
        var file = Path.Combine(directory, "orders.db");
        await using (var setup = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            await setup.OpenAsync();
            await using var command = setup.CreateCommand();
            command.CommandText = """
                CREATE TABLE "Order lines" (ORDER_ID INTEGER PRIMARY KEY, CUSTOMER VARCHAR(100) NOT NULL, STATUS TEXT NOT NULL DEFAULT 'NEW', TOTAL NUMERIC NULL,
                    DUE DATE NULL, STARTS TIME NULL, CREATED DATETIME NULL, PAID BOOLEAN NULL);
                """;
            await command.ExecuteNonQueryAsync();
        }
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        collection.AddTestConnections(connections => connections.AddConnection("Business", _ => new SqliteConnection($"Data Source={file};Pooling=False")));
        collection.AddSingleton<IDataSourceUserResolver, Users>();
        collection.AddSingleton(provider => new CodeCompilation(new CodeFolder(Path.Combine(directory, "Code")), RoslynCompiler.Find(),
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<ILogger<CodeCompilation>>()));
        collection.AddScoped<CompiledTypes>();
        collection.AddScoped<FormResourceRegistry>();
        await using var services = collection.BuildServiceProvider();

        TableSchema table;
        await using (var connection = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            await connection.OpenAsync();
            table = SqliteDialect.Instance.ReadTable(connection, new TableName(null, "Order lines"))!;
        }
        var recipe = Recipe(CodeRecipeKind.FormClass) with { Access = DataSourceAccess.SpecificGroups, ReadMemberGroups = ["Crew"], WriteMemberGroups = ["Buyers"] };
        var compilation = services.GetRequiredService<CodeCompilation>();
        await compilation.Folder.WriteAsync(CodeRecipes.FilePath(recipe, "Orders"), CodeRecipes.FormClass(recipe, table, SqliteDialect.Instance), create: true, default);
        var status = await compilation.CompileAsync("test", default);
        Assert.True(status.Succeeded, string.Join("\n", status.Diagnostics));
        Assert.Empty(status.Diagnostics);

        using var scope = services.CreateScope();
        var form = await scope.ServiceProvider.GetRequiredService<FormResourceRegistry>().FindAsync("order-lines");
        Assert.NotNull(form);
        Assert.Equal(["orderId", "customer", "status", "total", "due", "starts", "created", "paid"], form.Fields.Select(f => f.Key));
        Assert.True(form.Fields[0].ReadOnly);
        Assert.True(form.Fields[1].Required);
        Assert.Equal(100, form.Fields[1].MaxLength);

        JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
        Assert.Equal(403, (await form.InitializeAsync(Caller(null), default)).Status);
        var crew = await form.InitializeAsync(Caller("1002"), default);
        Assert.Equal(200, crew.Status);
        Assert.False(crew.Record!.Capabilities.Create);
        Assert.Equal(403, (await form.CreateAsync(Json(new { customer = "Hangar Co", status = "NEW" }), Caller("1002"), default)).Status);
        Assert.Equal(422, (await form.CreateAsync(Json(new { customer = "", status = "NEW" }), Caller("1001"), default)).Status);

        var created = await form.CreateAsync(Json(new { customer = "Hangar Co", status = "NEW", total = "12.50", due = "2026-11-30", starts = "09:30:00",
            created = "2026-11-02T10:15:00+00:00", paid = true }), Caller("1001"), default);
        Assert.Equal(200, created.Status);
        var record = created.Record!;
        Assert.Equal("1", record.Key);
        Assert.Equal("Hangar Co", record.Values["customer"]);
        Assert.Equal(12.50m, record.Values["total"]);
        Assert.Equal("2026-11-30", record.Values["due"]);
        Assert.Equal("09:30:00", record.Values["starts"]);
        Assert.Equal(true, record.Values["paid"]);
        Assert.True(record.Capabilities.Update);

        var loaded = await form.LoadAsync("1", Caller("1002"), default);
        Assert.Equal(record.Version, loaded.Record!.Version);
        Assert.False(loaded.Record.Capabilities.Delete);
        Assert.Equal(404, (await form.LoadAsync("99", Caller("1002"), default)).Status);
        Assert.Equal(404, (await form.LoadAsync("not a key", Caller("1002"), default)).Status);

        var updated = await form.UpdateAsync("1", Json(new { customer = "Runway Ltd", status = "PAID", paid = false }), record.Version!, Caller("1001"), default);
        Assert.Equal(200, updated.Status);
        Assert.Equal("Runway Ltd", updated.Record!.Values["customer"]);
        Assert.Equal(false, updated.Record.Values["paid"]);
        Assert.NotEqual(record.Version, updated.Record.Version);
        Assert.Equal(409, (await form.UpdateAsync("1", Json(new { customer = "Stale", status = "NEW" }), record.Version!, Caller("1001"), default)).Status);
        Assert.Equal(409, (await form.DeleteAsync("1", record.Version!, Caller("1001"), default)).Status);
        Assert.Equal(403, (await form.DeleteAsync("1", updated.Record.Version!, Caller("1002"), default)).Status);
        Assert.Equal(204, (await form.DeleteAsync("1", updated.Record.Version!, Caller("1001"), default)).Status);
        Assert.Equal(404, (await form.LoadAsync("1", Caller("1002"), default)).Status);
    }
}
