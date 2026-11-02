using System.Security.Claims;
using AppBlocks.Authentication;
using AppBlocks.DataSources;
using Microsoft.Extensions.Options;

namespace AppBlocks.Code;

/// <summary>
/// The compiled folder's classes for one request scope, made through <see cref="ActivatorUtilities"/> on the scope's provider so their
/// constructors take services as a registered class's would. Each extension point is made once per scope from the generation serving when
/// it is first asked for, so a recompile reaches the next request. A class that cannot be made is left out and logged; the section has
/// already reported it.
/// </summary>
public sealed class CompiledTypes(CodeCompilation compilation, IServiceProvider services, ILogger<CompiledTypes> logger)
{
    private readonly Dictionary<Type, object> made = [];
    private readonly HashSet<Type> making = [];

    /// <summary>
    /// The compiled classes implementing <typeparamref name="T"/>. A class whose constructor asks, through a registry, for the classes of its own
    /// extension point while they are being made gets none, rather than making itself again without end.
    /// </summary>
    public IReadOnlyList<T> Create<T>() where T : class
    {
        if (made.TryGetValue(typeof(T), out var known)) return (IReadOnlyList<T>)known;
        if (!making.Add(typeof(T))) return [];
        try
        {
            var instances = new List<T>();
            foreach (var type in compilation.Serving.Of(typeof(T)))
            {
                try { instances.Add((T)ActivatorUtilities.CreateInstance(services, type)); }
                catch (Exception error) { logger.LogWarning(error, "The compiled class {Type} could not be constructed and is not used.", type.FullName); }
            }
            made[typeof(T)] = instances;
            return instances;
        }
        finally { making.Remove(typeof(T)); }
    }
}

/// <summary>Every compiled <see cref="IDataSourcePolicy"/> as one registered policy: like registered ones, each must allow the operation.</summary>
internal sealed class CompiledDataSourcePolicies(CompiledTypes compiled) : IDataSourcePolicy
{
    public async ValueTask<bool> IsAllowedAsync(DataSourceRequest request, CancellationToken ct)
    {
        foreach (var policy in compiled.Create<IDataSourcePolicy>())
            if (!await policy.IsAllowedAsync(request, ct)) return false;
        return true;
    }
}

/// <summary>
/// Who may use the C# section: a backoffice User in Administrators or a User Group <c>AppBlocks:CodeEditorGroups</c> names. Code saved there
/// runs inside the site with its full rights, so the backoffice condition and every endpoint ask this.
/// </summary>
public sealed class CodeEditorAccess(ApplicationAccessPolicy access, IOptions<CodeOptions> options)
{
    public async Task<bool> IsAllowedAsync(ClaimsPrincipal principal)
        => await access.ActiveUserAsync(principal) is { } user && user.Groups.Any(group =>
            group.Alias.Equals(Umbraco.Cms.Core.Constants.Security.AdminGroupAlias, StringComparison.OrdinalIgnoreCase)
            || options.Value.CodeEditorGroups.Contains(group.Alias, StringComparer.OrdinalIgnoreCase));
}
