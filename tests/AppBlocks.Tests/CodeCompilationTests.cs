using System.Runtime.Loader;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AppBlocks.Code;
using AppBlocks.DataSources;
using AppBlocks.Forms;

namespace AppBlocks.Tests;

/// <summary>
/// The C# folder compiled at run time with the Roslyn this test project references, as a site with the development-mode package has it:
/// the registries pick up compiled classes beside registered ones, a broken save keeps the last good classes, and a site without a
/// compiler compiles nothing.
/// </summary>
public sealed class CodeCompilationTests : IDisposable
{
    /// <summary>A service a compiled class takes in its constructor, to show they are made with injection.</summary>
    public sealed class Greeting { public string Text => "made by injection"; }

    private readonly string root = Path.Combine(Directory.CreateTempSubdirectory("code-compilation-").FullName, "Code");
    private readonly List<ServiceProvider> providers = [];

    public void Dispose()
    {
        foreach (var provider in providers) provider.Dispose();
        try { Directory.Delete(Path.GetDirectoryName(root)!, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private ServiceProvider Site(bool compiler = true)
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<Greeting>();
        collection.AddSingleton(provider => new CodeCompilation(new CodeFolder(root), compiler ? RoslynCompiler.Find() : RoslynCompiler.Find("No.Such.Compilation, No.Such.Assembly"),
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<ILogger<CodeCompilation>>()));
        collection.AddScoped<CompiledTypes>();
        collection.AddScoped<IFormResource, SiteOrders>();
        collection.AddScoped<IDataSourceHandler, SiteRules>();
        collection.AddScoped<IBindVariableProvider, SiteProvider>();
        collection.AddScoped<IDataSourcePolicy, CompiledDataSourcePolicies>();
        collection.AddScoped<FormResourceRegistry>();
        collection.AddScoped<DataSourceHandlers>();
        collection.AddScoped<BindVariableProviders>();
        var provider = collection.BuildServiceProvider();
        providers.Add(provider);
        return provider;
    }

    private async Task Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, path))!);
        await File.WriteAllTextAsync(Path.Combine(root, path), text);
    }

    private const string Classes = """
        using System.ComponentModel.DataAnnotations;
        using System.Text.Json;
        using AppBlocks.DataSources;
        using AppBlocks.Forms;
        using AppBlocks.Tests;

        namespace Site.Code;

        public sealed class OrderRules(CodeCompilationTests.Greeting greeting) : IDataSourceHandler
        {
            public string Alias => "orders.rules";
            public string Made => greeting.Text;
        }

        public sealed record NoteInput
        {
            [Required, MaxLength(20)] public string? Title { get; init; }
        }

        public sealed class Notes : FormResource<NoteInput, NoteInput>
        {
            public override string Alias => "notes";
            public override IReadOnlyList<FormField> Fields => [new("title", "Title", "text", Required: true, MaxLength: 20)];
            public override Task<FormResult> InitializeAsync(FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Missing());
            public override Task<FormResult> LoadAsync(string key, FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Missing());
            public override Task<FormResult> DeleteAsync(string key, string? version, FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Denied());
            protected override Task<FormResult> CreateAsync(NoteInput input, FormExecutionContext context, CancellationToken ct)
                => Task.FromResult(FormResult.Ok(new FormRecord("1", "1", new Dictionary<string, object?> { ["title"] = input.Title }, new())));
            protected override Task<FormResult> UpdateAsync(string key, NoteInput input, string? version, FormExecutionContext context, CancellationToken ct)
                => Task.FromResult(FormResult.Denied());
        }

        public sealed class NoDeletes : IDataSourcePolicy
        {
            public ValueTask<bool> IsAllowedAsync(DataSourceRequest request, CancellationToken ct) => ValueTask.FromResult(request.Action != DataSourceAction.Delete);
        }

        public sealed class Tenant : IBindVariableProvider
        {
            public ValueTask<IReadOnlyDictionary<string, object?>?> ResolveAsync(FormExecutionContext context, CancellationToken ct)
                => ValueTask.FromResult<IReadOnlyDictionary<string, object?>?>(new Dictionary<string, object?> { ["tenant"] = "compiled", ["shared"] = "compiled" });
        }
        """;

    [Fact]
    public async Task The_registries_offer_compiled_classes_made_with_injection()
    {
        await Write("Orders/Classes.cs", Classes);
        var site = Site();
        var status = await site.GetRequiredService<CodeCompilation>().CompileAsync("test", default);
        Assert.True(status.Succeeded, string.Join("\n", status.Diagnostics));
        Assert.Equal(1, status.Generation);
        Assert.Contains(status.Classes, c => c is { Type: "Site.Code.OrderRules", ExtensionPoint: "IDataSourceHandler", Alias: "orders.rules", File: "Orders/Classes.cs", Line: 9 });

        using var scope = site.CreateScope();
        var services = scope.ServiceProvider;
        var handler = services.GetRequiredService<DataSourceHandlers>().Find("orders.rules");
        Assert.NotNull(handler);
        Assert.True(AssemblyLoadContext.GetLoadContext(handler.GetType().Assembly)!.IsCollectible);
        Assert.Equal("made by injection", handler.GetType().GetProperty("Made")!.GetValue(handler));
        Assert.NotNull(services.GetRequiredService<DataSourceHandlers>().Find("site.rules"));

        var registry = services.GetRequiredService<FormResourceRegistry>();
        Assert.Contains("notes", registry.RegisteredResources.Keys);
        var notes = await registry.FindAsync("notes");
        var context = new FormExecutionContext(new ClaimsPrincipal(new ClaimsIdentity()), Guid.NewGuid(), Guid.NewGuid(), "1");
        var created = await notes!.CreateAsync(JsonDocument.Parse("""{"title":"Hello"}""").RootElement, context, default);
        Assert.Equal(200, created.Status);
        Assert.Equal("Hello", created.Record!.Values["title"]);
        var invalid = await notes.CreateAsync(JsonDocument.Parse("""{"title":""}""").RootElement, context, default);
        Assert.Equal(422, invalid.Status);
        Assert.Contains("title", invalid.Errors!.Keys);

        var policies = services.GetServices<IDataSourcePolicy>().ToArray();
        DataSourceRequest Request(DataSourceAction action) => new("orders", action, DataSourceUser.Anonymous, context, null, null, null);
        Assert.True(await policies.Single().IsAllowedAsync(Request(DataSourceAction.Create), default));
        Assert.False(await policies.Single().IsAllowedAsync(Request(DataSourceAction.Delete), default));

        var values = await services.GetRequiredService<BindVariableProviders>().ResolveAsync(context, default);
        Assert.Equal("compiled", values["tenant"]);
        Assert.Equal("site", values["shared"]);
    }

    [Fact]
    public async Task Code_compiled_into_the_site_keeps_its_alias_and_the_clash_is_reported()
    {
        await Write("Clash.cs", """
            using AppBlocks.DataSources;
            namespace Site.Code;

            public sealed class Rules : IDataSourceHandler
            {
                public string Alias => "site.rules";
            }

            public sealed class Spaced : IDataSourceHandler
            {
                public string Alias => "not an alias";
            }

            internal sealed class Hidden : IDataSourceHandler
            {
                public string Alias => "hidden.rules";
            }
            """);
        var site = Site();
        var status = await site.GetRequiredService<CodeCompilation>().CompileAsync("test", default);
        Assert.True(status.Succeeded);
        Assert.Contains(status.Diagnostics, d => d is { Id: "AB0004", File: "Clash.cs", Line: 4, Severity: "error" } && d.Message.Contains("site.rules"));
        Assert.Contains(status.Diagnostics, d => d is { Id: "AB0003", File: "Clash.cs", Line: 9 });
        Assert.Contains(status.Diagnostics, d => d is { Id: "AB0001", File: "Clash.cs", Line: 14 });
        Assert.Empty(status.Classes);

        using var scope = site.CreateScope();
        var handlers = scope.ServiceProvider.GetRequiredService<DataSourceHandlers>();
        Assert.IsType<SiteRules>(handlers.Find("site.rules"));
        Assert.Equal(["site.rules"], handlers.All.Keys);
    }

    [Fact]
    public async Task A_save_that_does_not_compile_keeps_the_last_good_classes_serving()
    {
        await Write("Rules.cs", """
            namespace Site.Code;
            public sealed class Rules : AppBlocks.DataSources.IDataSourceHandler { public string Alias => "orders.rules"; }
            """);
        var site = Site();
        var compilation = site.GetRequiredService<CodeCompilation>();
        Assert.True((await compilation.CompileAsync("test", default)).Succeeded);

        await Write("Broken.cs", "namespace Site.Code;\n\npublic sealed class Broken\n{\n    public int Value => 1\n}\n");
        var broken = await compilation.CompileAsync("test", default);
        Assert.False(broken.Succeeded);
        var error = Assert.Single(broken.Diagnostics, d => d.IsError);
        Assert.Equal(("Broken.cs", 5, 26, "CS1002"), (error.File, error.Line, error.Column, error.Id));
        Assert.Equal(1, broken.Generation);
        Assert.Equal(1, compilation.Serving.Number);
        using (var scope = site.CreateScope())
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<DataSourceHandlers>().Find("orders.rules"));
        Assert.Same(broken, await compilation.CompileAsync("unchanged", default));

        await Write("Broken.cs", "namespace Site.Code;\npublic sealed class Fixed : AppBlocks.DataSources.IDataSourceHandler { public string Alias => \"orders.fixed\"; }\n");
        var fixedStatus = await compilation.CompileAsync("test", default);
        Assert.True(fixedStatus.Succeeded);
        Assert.Equal(2, fixedStatus.Generation);
        using (var scope = site.CreateScope())
            Assert.Equal(["orders.fixed", "orders.rules", "site.rules"], scope.ServiceProvider.GetRequiredService<DataSourceHandlers>().All.Keys.Order(StringComparer.Ordinal));

        File.Delete(Path.Combine(root, "Rules.cs"));
        File.Delete(Path.Combine(root, "Broken.cs"));
        Assert.Equal(0, (await compilation.CompileAsync("test", default)).Generation);
        using (var scope = site.CreateScope())
            Assert.Equal(["site.rules"], scope.ServiceProvider.GetRequiredService<DataSourceHandlers>().All.Keys);
    }

    [Fact]
    public async Task A_cancelled_compile_does_not_hide_the_folder_from_the_next_one()
    {
        // Large enough that the compile, not the read, is running when the request goes away.
        var methods = string.Concat(Enumerable.Range(0, 4000).Select(i => $"    public static int M{i}(int x) => x * {i} + (x > {i} ? {i} : -x);\n"));
        await Write("Rules.cs", "namespace Site.Code;\npublic sealed class Rules : AppBlocks.DataSources.IDataSourceHandler { public string Alias => \"orders.rules\"; }\n"
            + "public static class Many\n{\n" + methods + "}\n");
        var compilation = Site().GetRequiredService<CodeCompilation>();

        using var request = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => compilation.CompileAsync("save", request.Token));
        Assert.Equal(0, compilation.Serving.Number);

        var next = await compilation.CompileAsync("file change", default);
        Assert.True(next.Succeeded);
        Assert.Equal(1, next.Generation);
    }

    [Fact]
    public async Task Without_a_compiler_the_folder_is_never_compiled_or_created()
    {
        var site = Site(compiler: false);
        var compilation = site.GetRequiredService<CodeCompilation>();
        await compilation.StartAsync(default);
        Assert.False(compilation.CanCompile);
        Assert.False(Directory.Exists(root));

        await Write("Rules.cs", "namespace Site.Code;\npublic sealed class Rules : AppBlocks.DataSources.IDataSourceHandler { public string Alias => \"orders.rules\"; }\n");
        var status = await compilation.CompileAsync("test", default);
        Assert.False(status.CanCompile);
        Assert.Null(status.Attempted);
        Assert.Equal(0, compilation.Serving.Number);
        using var scope = site.CreateScope();
        Assert.Null(scope.ServiceProvider.GetRequiredService<DataSourceHandlers>().Find("orders.rules"));
        Assert.Single(compilation.Folder.Files());
    }

    [Fact]
    public async Task Startup_compiles_an_existing_folder_and_the_watcher_picks_up_changes()
    {
        await Write("Rules.cs", "namespace Site.Code;\npublic sealed class Rules : AppBlocks.DataSources.IDataSourceHandler { public string Alias => \"orders.rules\"; }\n");
        var site = Site();
        var compilation = site.GetRequiredService<CodeCompilation>();
        await compilation.StartAsync(default);
        // The first reader waits for the startup compilation rather than seeing an empty folder.
        Assert.Equal(1, compilation.Serving.Number);

        await Write("Rules.cs", "namespace Site.Code;\npublic sealed class Rules : AppBlocks.DataSources.IDataSourceHandler { public string Alias => \"orders.changed\"; }\n");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (compilation.Status.Generation < 2 && DateTime.UtcNow < deadline) await Task.Delay(100);
        Assert.Equal(2, compilation.Status.Generation);
        Assert.Contains(compilation.Status.Classes, c => c.Alias == "orders.changed");
        await compilation.StopAsync(default);
    }

    [Fact]
    public async Task A_compiled_class_asking_for_its_own_registry_neither_hangs_startup_nor_recurses()
    {
        await Write("Rules.cs", """
            namespace Site.Code;
            public sealed class Rules(AppBlocks.DataSources.DataSourceHandlers handlers) : AppBlocks.DataSources.IDataSourceHandler
            {
                public string Alias => "orders.rules";
                public int Peers => handlers.All.Count;
            }
            """);
        var site = Site();
        var compilation = site.GetRequiredService<CodeCompilation>();
        await compilation.StartAsync(default);
        var serving = Task.Run(() => compilation.Serving.Number);
        Assert.Same(serving, await Task.WhenAny(serving, Task.Delay(TimeSpan.FromSeconds(60))));
        Assert.Equal(1, await serving);
        Assert.True(compilation.Status.Succeeded);
        using var scope = site.CreateScope();
        var lookup = Task.Run(() => scope.ServiceProvider.GetRequiredService<DataSourceHandlers>().Find("orders.rules"));
        Assert.Same(lookup, await Task.WhenAny(lookup, Task.Delay(TimeSpan.FromSeconds(30))));
        Assert.Equal("orders.rules", (await lookup)?.Alias);
        await compilation.StopAsync(default);
    }

    [Fact]
    public void A_missing_compiler_type_is_no_compiler()
    {
        Assert.Null(RoslynCompiler.Find("No.Such.Compilation, No.Such.Assembly"));
        Assert.Null(RoslynCompiler.Find("System.String"));
        Assert.NotNull(RoslynCompiler.Find());
    }

    private sealed class SiteOrders : IFormResource
    {
        public string Alias => "site.orders";
        public string ContractVersion => "1";
        public IReadOnlyList<FormField> Fields => [];
        public Task<FormResult> InitializeAsync(FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Denied());
        public Task<FormResult> LoadAsync(string key, FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Denied());
        public Task<FormResult> CreateAsync(JsonElement input, FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Denied());
        public Task<FormResult> UpdateAsync(string key, JsonElement input, string? version, FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Denied());
        public Task<FormResult> DeleteAsync(string key, string? version, FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Denied());
    }

    private sealed class SiteRules : IDataSourceHandler { public string Alias => "site.rules"; }

    private sealed class SiteProvider : IBindVariableProvider
    {
        public ValueTask<IReadOnlyDictionary<string, object?>?> ResolveAsync(FormExecutionContext context, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyDictionary<string, object?>?>(new Dictionary<string, object?> { ["shared"] = "site" });
    }
}
