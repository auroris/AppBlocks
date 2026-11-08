using System.Collections.Concurrent;
using System.Globalization;
using AppBlocks.Forms;

namespace AppBlocks.DataSources;

/// <summary>
/// Values the application computes for editor SQL and field defaults, written as <c>:APP.name</c>: a tenant, a
/// fiscal period, a site section, whatever the business rules know how to decide. A provider receives the execution
/// context, the page key and form key, the signed-in person and the request's parameters, and answers with named
/// values. It is the application's own code, so a value may filter a row source, restrict rows and fill insert defaults,
/// which browser input never may. Register one with <c>AddScoped&lt;IBindVariableProvider, YourProvider&gt;()</c>;
/// several providers are merged, later registrations winning a name.
/// </summary>
public interface IBindVariableProvider
{
    /// <summary>The values for this operation, by name in any letter case; null or empty when the provider has none. A value is bound as it is: text, a number, a date or null.</summary>
    ValueTask<IReadOnlyDictionary<string, object?>?> ResolveAsync(FormExecutionContext context, CancellationToken ct);

    /// <summary>
    /// The names this provider answers, such as <c>tenant</c>, so publishing can warn about an <c>:APP.</c> name that nobody answers,
    /// which would bind NULL. Empty, the default, declares nothing: the provider may answer any name, and publishing checks no name.
    /// </summary>
    IReadOnlyCollection<string> Names => [];
}

/// <summary>
/// Asks every registered provider and merges their answers; nothing is asked when no provider is registered. Providers compiled from the
/// C# folder are asked first, so a registered provider wins a name they share. The merged answer is kept for the lifetime of this
/// instance, which is one request scope, by the signed-in principal, the page, the form and the request's parameters: the facts a provider
/// decides from do not change within one request, so every operation and every row of a request binds the same values.
/// </summary>
public sealed class BindVariableProviders(IEnumerable<IBindVariableProvider>? providers = null, Code.CompiledTypes? compiled = null)
{
    private readonly ConcurrentDictionary<(System.Security.Claims.ClaimsPrincipal Member, Guid Page, Guid Form, string Parameters), IReadOnlyDictionary<string, object?>> resolved = new();
    private IBindVariableProvider[]? all;
    // Compiled providers are made on first use, not in the constructor, so one that takes this class is given this instance.
    private IBindVariableProvider[] Providers => all ??= [.. compiled?.Create<IBindVariableProvider>() ?? [], .. providers ?? []];
    public static readonly IReadOnlyDictionary<string, object?> None = new Dictionary<string, object?>();

    public bool Any => Providers.Length > 0;

    /// <summary>
    /// Every name the providers declare (<see cref="IBindVariableProvider.Names"/>), ignoring case; null when there is no provider, or
    /// when any provider declares none, since that one may answer any name and nothing can be called unanswered.
    /// </summary>
    public IReadOnlySet<string>? DeclaredNames => Providers.Length == 0 || Providers.Any(provider => provider.Names.Count == 0) ? null
        : Providers.SelectMany(provider => provider.Names).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The warning publishing gives for <c>:APP.</c> names that no provider declares, or null when every name is declared or the
    /// declared names are not known (<see cref="DeclaredNames"/>).
    /// </summary>
    public string? UndeclaredWarning(IEnumerable<string> names)
    {
        if (DeclaredNames is not { } declared) return null;
        var missing = names.Where(name => !declared.Contains(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return missing.Length == 0 ? null
            : $"{string.Join(", ", missing.Select(name => ":APP." + name))} {(missing.Length == 1 ? "is" : "are")} not declared by any bind variable provider, so "
              + $"{(missing.Length == 1 ? "it binds" : "they bind")} NULL. The providers declare {string.Join(", ", declared.Order(StringComparer.OrdinalIgnoreCase).Select(name => ":APP." + name))}.";
    }

    /// <summary>The providers' merged values for the context's principal, page, form and parameters, asked once per request scope.</summary>
    public async ValueTask<IReadOnlyDictionary<string, object?>> ResolveAsync(FormExecutionContext context, CancellationToken ct)
    {
        if (Providers.Length == 0) return None;
        var key = (context.Member, context.PageKey, context.FormKey, Signature(context.Parameters));
        if (resolved.TryGetValue(key, out var known)) return known;
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in Providers)
            foreach (var (name, value) in await provider.ResolveAsync(context, ct) ?? None)
                if (SqlTemplate.IsIdentifier(name)) values[name] = value;
        return resolved[key] = values;
    }

    /// <summary>The request's parameters as one text, names in any letter case, so two contexts with the same parameters share an answer.</summary>
    private static string Signature(IReadOnlyDictionary<string, string>? parameters) => parameters is null ? ""
        : string.Join('\u001e', parameters.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => pair.Key.ToUpperInvariant() + '\u001f' + pair.Value));

    /// <summary>A value as a field default reads it: invariant text, dates as ISO, nothing for null or empty.</summary>
    public static string? Text(object? value) => value switch {
        null or DBNull => null,
        string text => text.Length == 0 ? null : text,
        DateTime moment => moment.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset moment => moment.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) is { Length: > 0 } text ? text : null,
    };
}
