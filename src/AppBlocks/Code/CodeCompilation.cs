using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using AppBlocks.DataSources;
using AppBlocks.Forms;
using Microsoft.Extensions.Options;

namespace AppBlocks.Code;

public sealed class CodeOptions
{
    public const string Section = "AppBlocks";
    /// <summary>The C# folder, relative to the content root unless rooted. Keep it equal to the <c>AppBlocksCodeDirectory</c> MSBuild property.</summary>
    public string CodeDirectory { get; set; } = CodeFolder.DefaultDirectory;
    /// <summary>User Group aliases allowed to use the C# section, alongside Administrators.</summary>
    public string[] CodeEditorGroups { get; set; } = [];
}

/// <summary>The extension points AppBlocks reads from the compiled folder, in the order the section lists them.</summary>
public static class CodeExtensionPoints
{
    public static readonly Type[] All = [typeof(IFormResource), typeof(IDataSourceHandler), typeof(IDataSourcePolicy), typeof(IBindVariableProvider)];
}

/// <summary>
/// One compiled folder in its own collectible load context: the classes it has for each extension point, and when it was made. Number 0 is
/// the folder with nothing compiled, before the first compilation or when it has no files.
/// </summary>
public sealed class CodeGeneration
{
    internal CodeGeneration(int number, Assembly? assembly, AssemblyLoadContext? context, DateTimeOffset compiled)
    {
        Number = number; Assembly = assembly; Context = context; Compiled = compiled;
        var types = assembly is null ? [] : Loadable(assembly);
        Implementations = CodeExtensionPoints.All.ToDictionary(point => point, point => (IReadOnlyList<Type>)types
            .Where(type => type.IsClass && !type.IsAbstract && !type.ContainsGenericParameters && type.IsVisible && point.IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray());
        Hidden = types.Where(type => type.IsClass && !type.IsAbstract && !type.IsVisible && !type.Name.StartsWith('<') && CodeExtensionPoints.All.Any(point => point.IsAssignableFrom(type))).ToArray();
    }

    internal static CodeGeneration Empty { get; } = new(0, null, null, DateTimeOffset.MinValue);

    public int Number { get; }
    public Assembly? Assembly { get; }
    internal AssemblyLoadContext? Context { get; }
    public DateTimeOffset Compiled { get; }
    /// <summary>The public classes implementing each of <see cref="CodeExtensionPoints.All"/>, in name order.</summary>
    public IReadOnlyDictionary<Type, IReadOnlyList<Type>> Implementations { get; }
    /// <summary>Classes implementing an extension point that are not public, which AppBlocks therefore never uses.</summary>
    internal IReadOnlyList<Type> Hidden { get; }
    public IReadOnlyList<Type> Of(Type point) => Implementations.TryGetValue(point, out var types) ? types : [];

    private static Type[] Loadable(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException error) { return error.Types.OfType<Type>().ToArray(); }
    }
}

/// <summary>Where the folder stands: whether it can compile, the last attempt's messages, and the generation serving requests.</summary>
public sealed record CodeStatus(bool CanCompile, string Root, bool Succeeded, DateTimeOffset? Attempted, int Generation, DateTimeOffset? Compiled,
    IReadOnlyList<CodeDiagnostic> Diagnostics, IReadOnlyList<CodeClass> Classes)
{
    public string Package => RoslynCompiler.Package;
}

/// <summary>A compiled class AppBlocks uses, with the extension point and alias it was found under.</summary>
public sealed record CodeClass(string Type, string ExtensionPoint, string? Alias, string? File, int Line);

/// <summary>
/// Compiles the C# folder at run time, the way Umbraco's development mode compiles Razor views: the whole folder into one assembly in a
/// collectible load context, referencing the framework and the application's loaded assemblies, again on every save and, debounced, on every
/// change the file watcher sees. A generation serves until the next one compiles without errors, so a broken save never takes working classes
/// away. The previous context is then unloaded, which the runtime completes only once nothing holds its types, so unloading is not guaranteed:
/// a static cache or a running request that still references an old class keeps its assembly in memory.
/// Without a compiler (<see cref="RoslynCompiler"/>) nothing is compiled, and the classes in the folder are not used at all.
/// </summary>
public sealed class CodeCompilation : IHostedService, IDisposable
{
    internal static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(400);
    /// <summary>Set while compiled classes are made to check them, when the generation they belong to is already serving.</summary>
    private static readonly AsyncLocal<bool> registering = new();

    private readonly RoslynCompiler? compiler;
    private readonly IServiceScopeFactory scopes;
    private readonly ILogger<CodeCompilation> logger;
    private readonly SemaphoreSlim compiling = new(1, 1);
    private readonly Lock watching = new();
    private volatile CodeGeneration serving = CodeGeneration.Empty;
    private volatile CodeStatus status;
    private Task? initial;
    private string? attemptedHash;
    private FileSystemWatcher? watcher;
    private Timer? debounce;

    public CodeCompilation(IHostEnvironment environment, IOptions<CodeOptions> options, IServiceScopeFactory scopes, ILogger<CodeCompilation> logger)
        : this(CodeFolder.Under(environment.ContentRootPath, options.Value.CodeDirectory), RoslynCompiler.Find(), scopes, logger) { }

    internal CodeCompilation(CodeFolder folder, RoslynCompiler? compiler, IServiceScopeFactory scopes, ILogger<CodeCompilation> logger)
    {
        Folder = folder; this.compiler = compiler; this.scopes = scopes; this.logger = logger;
        status = new(CanCompile, folder.Root, true, null, 0, null, [], []);
    }

    public CodeFolder Folder { get; }
    /// <summary>Whether the site has a compiler; without one the section is read-only.</summary>
    public bool CanCompile => compiler is not null;
    public CodeStatus Status => status;

    /// <summary>The generation serving requests, waiting for the first compilation of an existing folder to finish.</summary>
    public CodeGeneration Serving
    {
        get
        {
            // A class made by Register may ask a registry for its peers; the compilation it would wait for is the one making it.
            if (registering.Value || Volatile.Read(ref initial) is not { IsCompleted: false } pending) return serving;
            pending.GetAwaiter().GetResult();
            return serving;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (CanCompile && Folder.Exists) Begin();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        StopWatching();
        return Task.CompletedTask;
    }

    /// <summary>Starts watching and compiling a folder that exists, once: called on startup and after the write that creates the folder.</summary>
    internal void Begin()
    {
        if (!CanCompile || !Folder.Exists) return;
        lock (watching)
        {
            if (watcher is not null) return;
            Volatile.Write(ref initial, Task.Run(async () => {
                try { await CompileAsync("startup", CancellationToken.None); }
                catch (Exception error) { logger.LogError(error, "The C# folder could not be compiled on startup."); }
            }));
            try
            {
                watcher = new FileSystemWatcher(Folder.Root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
                watcher.Changed += Changed; watcher.Created += Changed; watcher.Deleted += Changed; watcher.Renamed += Changed;
                watcher.Error += (_, args) => { logger.LogWarning(args.GetException(), "The C# folder watcher failed; the folder is compiled again on the next save."); Changed(this, null!); };
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception error) when (error is IOException or ArgumentException or PlatformNotSupportedException)
            {
                logger.LogWarning(error, "The C# folder {Root} cannot be watched; it is compiled on each save from the backoffice.", Folder.Root);
            }
        }
    }

    private void Changed(object sender, FileSystemEventArgs? args)
    {
        lock (watching)
        {
            debounce ??= new Timer(_ => _ = CompileAsync("file change", CancellationToken.None).ContinueWith(task => logger.LogError(task.Exception, "The C# folder could not be compiled."), TaskContinuationOptions.OnlyOnFaulted));
            debounce.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void StopWatching()
    {
        lock (watching)
        {
            watcher?.Dispose(); watcher = null;
            debounce?.Dispose(); debounce = null;
        }
    }

    public void Dispose()
    {
        StopWatching();
        compiling.Dispose();
    }

    /// <summary>
    /// Compiles the folder unless it is unchanged since the last attempt, and swaps in the result if it has no errors. A folder with no files
    /// compiles to nothing, which takes every compiled class away.
    /// </summary>
    public async Task<CodeStatus> CompileAsync(string reason, CancellationToken ct, bool force = false)
    {
        if (compiler is null) return status;
        await compiling.WaitAsync(ct);
        try
        {
            var sources = new List<(string Path, string Text)>();
            // A file left out is reported and the rest compile, so one file the folder cannot take does not hold back every other change.
            var skipped = new List<CodeDiagnostic>();
            foreach (var entry in Folder.Files())
            {
                if (entry.Size > CodeFolder.MaxFileBytes) { skipped.Add(new(CodeDiagnostic.Warning, "AB0100", $"The file is left out: it is larger than {CodeFolder.MaxFileBytes / 1024} KB.", entry.Path, 1, 1)); continue; }
                string full;
                try { full = Folder.Resolve(entry.Path, file: true); }
                catch (CodePathException error) { skipped.Add(new(CodeDiagnostic.Warning, "AB0101", "The file is left out: " + error.Message, entry.Path, 1, 1)); continue; }
                sources.Add((full, await File.ReadAllTextAsync(full, ct)));
            }
            // What is left out counts too, so a file growing past the limit is reported although nothing compiled has changed.
            var hash = Hash([.. sources, .. skipped.Select(d => ("\0" + d.File, d.Message))]);
            if (!force && hash == attemptedHash) return status;
            attemptedHash = hash;
            var started = DateTimeOffset.UtcNow;
            if (sources.Count == 0)
            {
                Swap(CodeGeneration.Empty);
                logger.LogInformation("The C# folder has no files to compile ({Reason}); no compiled classes are in use.", reason);
                return status = new(true, Folder.Root, true, started, 0, null, skipped, []);
            }
            var number = serving.Number + 1;
            RoslynOutput output;
            try { output = await Task.Run(() => compiler.Compile($"AppBlocks.Code.{number}", sources, Folder.Relative, ct), ct); }
            catch
            {
                // A compile that did not finish, cancelled with its request or failed, is no attempt: the next change compiles again.
                attemptedHash = null;
                throw;
            }
            if (output.Image is null)
            {
                var errors = output.Diagnostics.Count(d => d.IsError);
                logger.LogWarning("The C# folder did not compile ({Reason}): {Errors} error(s). Generation {Generation} stays in use. First: {First}", reason, errors, serving.Number,
                    output.Diagnostics.FirstOrDefault(d => d.IsError) is { } first ? $"{first.File}({first.Line},{first.Column}): {first.Id} {first.Message}" : "none");
                return status = status with { Succeeded = false, Attempted = started, Diagnostics = [.. skipped, .. output.Diagnostics] };
            }
            var context = new AssemblyLoadContext($"AppBlocks.Code.{number}", isCollectible: true);
            using var image = new MemoryStream(output.Image);
            using var symbols = new MemoryStream(output.Symbols!);
            var generation = new CodeGeneration(number, context.LoadFromStream(image, symbols), context, DateTimeOffset.UtcNow);
            Swap(generation);
            var (classes, registration) = Register(generation, output.Locate);
            var diagnostics = skipped.Concat(output.Diagnostics).Concat(registration).ToArray();
            logger.LogInformation("Compiled the C# folder ({Reason}) into generation {Generation}: {Files} file(s), {Classes} class(es) in use, {Warnings} warning(s).",
                reason, number, sources.Count, classes.Count, diagnostics.Count(d => !d.IsError));
            foreach (var problem in registration) logger.LogWarning("C# folder: {File}({Line}): {Message}", problem.File, problem.Line, problem.Message);
            return status = new(true, Folder.Root, true, started, number, generation.Compiled, diagnostics, classes);
        }
        finally { compiling.Release(); }
    }

    private void Swap(CodeGeneration next)
    {
        var previous = serving;
        serving = next;
        if (previous.Context is { } context && previous != next)
        {
            // Completes once nothing references the old classes; a request still running with them finishes first.
            try { context.Unload(); }
            catch (InvalidOperationException error) { logger.LogDebug(error, "Generation {Generation} could not be unloaded.", previous.Number); }
        }
    }

    /// <summary>
    /// Makes each compiled class once, in a scope of its own, to read its alias and report what AppBlocks will not use: a class that is not
    /// public, one that cannot be constructed, an alias that is not one, or an alias that code compiled into the site, or an earlier class, already has.
    /// </summary>
    private (IReadOnlyList<CodeClass> Classes, IReadOnlyList<CodeDiagnostic> Problems) Register(CodeGeneration generation, Func<string, (string? File, int Line)?> locate)
    {
        var classes = new List<CodeClass>();
        var problems = new List<CodeDiagnostic>();
        CodeDiagnostic Problem(Type type, string id, string message)
        {
            var (file, line) = locate(type.FullName!) ?? (null, 0);
            return new(CodeDiagnostic.Error, id, message, file, line, 1);
        }
        foreach (var type in generation.Hidden) problems.Add(Problem(type, "AB0001", $"{type.Name} implements an AppBlocks extension point but is not public, so AppBlocks does not use it."));
        registering.Value = true;
        try
        {
            using var scope = scopes.CreateScope();
            var services = scope.ServiceProvider;
            var taken = new Dictionary<Type, HashSet<string>> {
                [typeof(IFormResource)] = services.GetServices<IFormResource>().Select(r => r.Alias).ToHashSet(StringComparer.Ordinal),
                [typeof(IDataSourceHandler)] = services.GetServices<IDataSourceHandler>().Select(h => h.Alias).ToHashSet(StringComparer.Ordinal),
            };
            var seen = new Dictionary<Type, HashSet<string>> { [typeof(IFormResource)] = new(StringComparer.Ordinal), [typeof(IDataSourceHandler)] = new(StringComparer.Ordinal) };
            foreach (var point in CodeExtensionPoints.All)
                foreach (var type in generation.Of(point))
                {
                    object instance;
                    try { instance = ActivatorUtilities.CreateInstance(services, type); }
                    catch (Exception error)
                    {
                        problems.Add(Problem(type, "AB0002", $"{type.Name} cannot be constructed, so AppBlocks does not use it: {(error is TargetInvocationException { InnerException: { } inner } ? inner.Message : error.Message)}"));
                        continue;
                    }
                    string? alias = instance switch { IFormResource resource when point == typeof(IFormResource) => resource.Alias, IDataSourceHandler handler when point == typeof(IDataSourceHandler) => handler.Alias, _ => null };
                    (instance as IDisposable)?.Dispose();
                    var where = locate(type.FullName!);
                    if (alias is not null)
                    {
                        if (string.IsNullOrWhiteSpace(alias) || point == typeof(IDataSourceHandler) && !DataSourceDefinition.IsAlias(alias))
                        { problems.Add(Problem(type, "AB0003", $"{type.Name} has the alias '{alias}', which is not one: use letters, digits, '.', '-' and '_', starting with a letter.")); continue; }
                        if (taken[point].Contains(alias))
                        { problems.Add(Problem(type, "AB0004", $"{type.Name} has the alias {alias}, which a class compiled into the site already has. The site's class is used, not this one.")); continue; }
                        if (!seen[point].Add(alias))
                        { problems.Add(Problem(type, "AB0005", $"{type.Name} has the alias {alias}, which another class in this folder already has. The first by full name is used.")); continue; }
                    }
                    classes.Add(new(type.FullName!, point.Name, alias, where?.File, where?.Line ?? 0));
                }
            return (classes, problems);
        }
        finally { registering.Value = false; }
    }

    private static string Hash(IEnumerable<(string Path, string Text)> sources)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (path, text) in sources.OrderBy(s => s.Path, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(path)); hash.AppendData([0]);
            hash.AppendData(Encoding.UTF8.GetBytes(text)); hash.AppendData([0]);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
