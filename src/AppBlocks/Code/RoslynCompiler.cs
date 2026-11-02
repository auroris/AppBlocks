using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Text;

namespace AppBlocks.Code;

/// <summary>One compiler message, with the file below the C# folder and a 1-based line and column; no file for one about the whole compilation.</summary>
public sealed record CodeDiagnostic(string Severity, string Id, string Message, string? File = null, int Line = 0, int Column = 0)
{
    public const string Error = "error", Warning = "warning", Info = "info";
    public bool IsError => Severity == Error;
}

/// <summary>What one compilation made: the assembly image and its symbols when it succeeded, and the messages either way.</summary>
internal sealed record RoslynOutput(byte[]? Image, byte[]? Symbols, IReadOnlyList<CodeDiagnostic> Diagnostics, Func<string, (string? File, int Line)?> Locate);

/// <summary>
/// The C# compiler the site brought, reached by type name as <see cref="Connections.DatabaseDrivers"/> reaches a database driver:
/// AppBlocks references no Roslyn assembly and ships none. Umbraco.Cms.DevelopmentMode.Backoffice depends on Microsoft.CodeAnalysis.CSharp,
/// so a site with that package installed has one, and a site without it, which includes every site Umbraco runs in Production mode, has none.
/// Every member is looked up once, when the compiler is found, so a Roslyn whose shape differs is treated as no compiler rather than failing later.
/// </summary>
internal sealed class RoslynCompiler
{
    public const string Package = "Umbraco.Cms.DevelopmentMode.Backoffice";
    public const string CompilationTypeName = "Microsoft.CodeAnalysis.CSharp.CSharpCompilation, Microsoft.CodeAnalysis.CSharp";

    /// <summary>What the Web SDK adds to a site's own C#, so a file compiles here as it would in the site project.</summary>
    internal const string ImplicitUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Threading;
        global using System.Threading.Tasks;
        global using Microsoft.AspNetCore.Builder;
        global using Microsoft.AspNetCore.Hosting;
        global using Microsoft.AspNetCore.Http;
        global using Microsoft.AspNetCore.Routing;
        global using Microsoft.Extensions.Configuration;
        global using Microsoft.Extensions.DependencyInjection;
        global using Microsoft.Extensions.Hosting;
        global using Microsoft.Extensions.Logging;
        """;

    /// <summary>Version mismatches between referenced assemblies, which the SDK also leaves unreported.</summary>
    private static readonly string[] Suppressed = ["CS1701", "CS1702", "CS1705"];

    private readonly MethodInfo parseText, createReference, create, emit, getTypeByMetadataName;
    private readonly Type syntaxTree, metadataReference;
    private readonly object options, emitOptions;
    private readonly PropertyInfo locations;
    private readonly Dictionary<string, object> references = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock gate = new();

    private RoslynCompiler(Type compilation)
    {
        var core = compilation.BaseType!.Assembly;
        var csharp = compilation.Assembly;
        Type CoreType(string name) => core.GetType("Microsoft.CodeAnalysis." + name, throwOnError: true)!;
        Type CSharpType(string name) => csharp.GetType("Microsoft.CodeAnalysis.CSharp." + name, throwOnError: true)!;
        object Enum(Type type, string name) => System.Enum.Parse(type, name);

        syntaxTree = CoreType("SyntaxTree");
        metadataReference = CoreType("MetadataReference");
        locations = CoreType("ISymbol").GetProperty("Locations")!;
        parseText = CSharpType("CSharpSyntaxTree").GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "ParseText" && m.GetCustomAttribute<ObsoleteAttribute>() is null && m.GetParameters() is [{ ParameterType: var first }, ..] && first == typeof(string))
            .OrderBy(m => m.GetParameters().Length).First();
        createReference = metadataReference.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == "CreateFromFile" && m.GetParameters() is [{ ParameterType: var path }, ..] && path == typeof(string));
        create = compilation.GetMethods(BindingFlags.Public | BindingFlags.Static).First(m => m.Name == "Create" && m.GetParameters().Length == 4);
        emit = compilation.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "Emit" && m.GetCustomAttribute<ObsoleteAttribute>() is null && m.GetParameters() is [{ Name: "peStream" }, ..])
            .OrderByDescending(m => m.GetParameters().Length).First();
        getTypeByMetadataName = compilation.GetMethod("GetTypeByMetadataName", [typeof(string)])!;

        var optionsType = CSharpType("CSharpCompilationOptions");
        var constructor = optionsType.GetConstructors().Where(c => c.GetParameters() is [{ Name: "outputKind" }, .. var rest] && rest.All(p => p.HasDefaultValue))
            .OrderBy(c => c.GetParameters().Length).First();
        options = Invoke(constructor, null, new() { ["outputKind"] = Enum(CoreType("OutputKind"), "DynamicallyLinkedLibrary") });
        options = Call(options, "WithNullableContextOptions", Enum(CoreType("NullableContextOptions"), "Enable"));
        options = Call(options, "WithOptimizationLevel", Enum(CoreType("OptimizationLevel"), "Release"));
        var report = CoreType("ReportDiagnostic");
        var pair = typeof(KeyValuePair<,>).MakeGenericType(typeof(string), report);
        var pairs = Array.CreateInstance(pair, Suppressed.Length);
        for (var i = 0; i < Suppressed.Length; i++) pairs.SetValue(Activator.CreateInstance(pair, Suppressed[i], Enum(report, "Suppress")), i);
        var withSpecific = optionsType.GetMethods().First(m => m.Name == "WithSpecificDiagnosticOptions" && m.GetParameters()[0].ParameterType.IsAssignableFrom(pairs.GetType()));
        options = withSpecific.Invoke(options, [pairs])!;

        var emitType = CoreType("Emit.EmitOptions");
        var emitConstructor = emitType.GetConstructors().Where(c => c.GetParameters().All(p => p.HasDefaultValue)).OrderBy(c => c.GetParameters().Length).First();
        emitOptions = Call(Invoke(emitConstructor, null, []), "WithDebugInformationFormat", Enum(CoreType("Emit.DebugInformationFormat"), "PortablePdb"));
    }

    /// <summary>The compiler, or null when the site has not installed one.</summary>
    public static RoslynCompiler? Find(string typeName = CompilationTypeName)
    {
        try { return Type.GetType(typeName, throwOnError: false) is { } compilation ? new RoslynCompiler(compilation) : null; }
        catch (Exception error) when (error is FileLoadException or BadImageFormatException or TargetInvocationException or InvalidOperationException
            or ArgumentException or NullReferenceException or TypeLoadException) { return null; }
    }

    /// <summary>Compiles the sources, by full path, into one library; <paramref name="relative"/> turns a path into the one an editor sees.</summary>
    public RoslynOutput Compile(string assemblyName, IReadOnlyList<(string Path, string Text)> sources, Func<string, string> relative, CancellationToken ct)
    {
        var trees = Array.CreateInstance(syntaxTree, sources.Count + 1);
        trees.SetValue(Parse(ImplicitUsings, "", ct), 0);
        for (var i = 0; i < sources.Count; i++) trees.SetValue(Parse(sources[i].Text, sources[i].Path, ct), i + 1);
        var compilation = create.Invoke(null, [assemblyName, trees, References(), options])!;
        using var image = new MemoryStream();
        using var symbols = new MemoryStream();
        var result = Invoke(emit, compilation, new() { ["peStream"] = image, ["pdbStream"] = symbols, ["options"] = emitOptions, ["cancellationToken"] = ct });
        var success = (bool)result.GetType().GetProperty("Success")!.GetValue(result)!;
        var diagnostics = new List<CodeDiagnostic>();
        foreach (var diagnostic in (IEnumerable)result.GetType().GetProperty("Diagnostics")!.GetValue(result)!)
            if (Read(diagnostic, relative) is { } read) diagnostics.Add(read);
        (string?, int)? Locate(string typeName)
        {
            if (getTypeByMetadataName.Invoke(compilation, [typeName]) is not { } symbol) return null;
            foreach (var location in (IEnumerable)locations.GetValue(symbol)!)
                if (Span(location, relative) is { } span) return (span.File, span.Line);
            return null;
        }
        return new(success ? image.ToArray() : null, success ? symbols.ToArray() : null,
            diagnostics.OrderBy(d => d.File, StringComparer.Ordinal).ThenBy(d => d.Line).ThenBy(d => d.Column).ToArray(), Locate);
    }

    private object Parse(string text, string path, CancellationToken ct)
        => Invoke(parseText, null, new() { ["text"] = text, ["path"] = path, ["encoding"] = Encoding.UTF8, ["cancellationToken"] = ct });

    private static CodeDiagnostic? Read(object diagnostic, Func<string, string> relative)
    {
        var type = diagnostic.GetType();
        var severity = type.GetProperty("Severity")!.GetValue(diagnostic)!.ToString();
        if (severity is not ("Error" or "Warning" or "Info") || type.GetProperty("IsSuppressed")?.GetValue(diagnostic) is true) return null;
        var id = (string)type.GetProperty("Id")!.GetValue(diagnostic)!;
        var message = (string)type.GetMethod("GetMessage", [typeof(IFormatProvider)])!.Invoke(diagnostic, [CultureInfo.InvariantCulture])!;
        var span = Span(type.GetProperty("Location")!.GetValue(diagnostic)!, relative);
        return new(severity.ToLowerInvariant(), id, message, span?.File, span?.Line ?? 0, span?.Column ?? 0);
    }

    /// <summary>Where a Roslyn Location points, 1-based, in a file of the folder; null outside the sources.</summary>
    private static (string File, int Line, int Column)? Span(object location, Func<string, string> relative)
    {
        var span = location.GetType().GetMethod("GetLineSpan", Type.EmptyTypes)!.Invoke(location, null)!;
        var path = (string?)span.GetType().GetProperty("Path")!.GetValue(span);
        if (string.IsNullOrEmpty(path)) return null;
        var start = span.GetType().GetProperty("StartLinePosition")!.GetValue(span)!;
        var line = (int)start.GetType().GetProperty("Line")!.GetValue(start)!;
        var column = (int)start.GetType().GetProperty("Character")!.GetValue(start)!;
        return (relative(path), line + 1, column + 1);
    }

    /// <summary>
    /// The framework's trusted platform assemblies and the application's loaded ones (AppBlocks, Umbraco, the site): what the site's own code
    /// compiles against. Each file is read once; one that holds no metadata, such as a native library, is left out.
    /// </summary>
    private Array References()
    {
        var paths = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic && assembly.Location.Length > 0 && AssemblyLoadContext.GetLoadContext(assembly) is { IsCollectible: false })
            .Select(assembly => assembly.Location)
            .Concat(((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? [])
            .DistinctBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase).ToArray();
        var found = new List<object>();
        lock (gate)
        {
            foreach (var path in paths)
            {
                if (!references.TryGetValue(path, out var reference))
                {
                    reference = HasMetadata(path) ? Invoke(createReference, null, new() { ["path"] = path }) : null!;
                    references[path] = reference;
                }
                if (reference is not null) found.Add(reference);
            }
        }
        var array = Array.CreateInstance(metadataReference, found.Count);
        for (var i = 0; i < found.Count; i++) array.SetValue(found[i], i);
        return array;
    }

    private static bool HasMetadata(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            return reader.HasMetadata;
        }
        catch (Exception error) when (error is IOException or BadImageFormatException or UnauthorizedAccessException) { return false; }
    }

    private static object Call(object target, string method, object argument)
        => target.GetType().GetMethods().First(m => m.Name == method && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == argument.GetType())
            .Invoke(target, [argument])!;

    /// <summary>Calls a method with the named arguments given and every other parameter at its default.</summary>
    private static object Invoke(MethodBase method, object? target, Dictionary<string, object?> named)
    {
        var parameters = method.GetParameters();
        var arguments = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            arguments[i] = named.TryGetValue(parameter.Name!, out var value) ? value
                : parameter.HasDefaultValue && parameter.DefaultValue is not null ? parameter.DefaultValue
                : parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null;
        }
        try { return method is ConstructorInfo constructor ? constructor.Invoke(arguments) : method.Invoke(target, arguments)!; }
        catch (TargetInvocationException error) when (error.InnerException is OperationCanceledException canceled) { throw canceled; }
    }
}
