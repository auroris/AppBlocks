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
}

/// <summary>
/// Asks every registered provider once per operation and merges their answers; nothing is asked when no provider is registered. Providers
/// compiled from the C# folder are asked first, so a registered provider wins a name they share.
/// </summary>
public sealed class BindVariableProviders(IEnumerable<IBindVariableProvider>? providers = null, Code.CompiledTypes? compiled = null)
{
    private IBindVariableProvider[]? all;
    // Compiled providers are made on first use, not in the constructor, so one that takes this class is given this instance.
    private IBindVariableProvider[] Providers => all ??= [.. compiled?.Create<IBindVariableProvider>() ?? [], .. providers ?? []];
    public static readonly IReadOnlyDictionary<string, object?> None = new Dictionary<string, object?>();

    public bool Any => Providers.Length > 0;

    public async ValueTask<IReadOnlyDictionary<string, object?>> ResolveAsync(FormExecutionContext context, CancellationToken ct)
    {
        if (Providers.Length == 0) return None;
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in Providers)
            foreach (var (name, value) in await provider.ResolveAsync(context, ct) ?? None)
                if (SqlTemplate.IsIdentifier(name)) values[name] = value;
        return values;
    }

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
