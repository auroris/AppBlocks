using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppBlocks.Forms;

public sealed record FormChoice(string Value, string Label);
public sealed record FormField(string Key, string Label, string Kind, bool Required = false,
    bool ReadOnly = false, int? MaxLength = null, decimal? Minimum = null, decimal? Maximum = null,
    IReadOnlyList<FormChoice>? Choices = null, bool HasServerDefault = false);
public sealed record FormCapabilities(bool Create = false, bool Update = false, bool Delete = false);
public sealed record FormRecord(string? Key, string? Version, IReadOnlyDictionary<string, object?> Values, FormCapabilities Capabilities);
/// <param name="Member">The authenticated Umbraco Member or backoffice User. Resources decide which account types and groups may perform each operation.</param>
/// <param name="Parameters">
/// The query parameters of the form request, which data sources bind as <c>:PARAM.name</c>. They come from the
/// browser like any other input, so a resource may filter or default with them but must never authorize with them.
/// </param>
public sealed record FormExecutionContext(ClaimsPrincipal Member, Guid PageKey, Guid FormKey, string DefinitionVersion, string? SubmissionKey = null,
    IReadOnlyDictionary<string, string>? Parameters = null);

public sealed record FormResult(int Status, FormRecord? Record = null, IReadOnlyDictionary<string, string[]>? Errors = null)
{
    public static FormResult Ok(FormRecord record) => new(200, record);
    public static FormResult Invalid(string field, string error) => new(422, Errors: new Dictionary<string, string[]> { [field] = [error] });
    public static FormResult Denied() => new(403);
    public static FormResult Missing() => new(404);
    public static FormResult Conflict() => new(409, Errors: new Dictionary<string, string[]> { [""] = ["This record has changed. Reload it before saving."] });
}

/// <summary>
/// A C# resource owns persistence, current-state authorization and business validation.
/// Every operation must enforce permission independently of the capabilities displayed by the UI.
/// </summary>
public interface IFormResource
{
    string Alias { get; }
    string ContractVersion { get; }
    IReadOnlyList<FormField> Fields { get; }
    Task<FormResult> InitializeAsync(FormExecutionContext context, CancellationToken ct);
    Task<FormResult> LoadAsync(string key, FormExecutionContext context, CancellationToken ct);
    Task<FormResult> CreateAsync(JsonElement input, FormExecutionContext context, CancellationToken ct);
    Task<FormResult> UpdateAsync(string key, JsonElement input, string version, FormExecutionContext context, CancellationToken ct);
    Task<FormResult> DeleteAsync(string key, string version, FormExecutionContext context, CancellationToken ct);
}

/// <summary>Typed command adapter with strict JSON binding and data-annotation validation.</summary>
public abstract class FormResource<TCreate, TUpdate> : IFormResource where TCreate : class where TUpdate : class
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
    public abstract string Alias { get; }
    public virtual string ContractVersion => "1";
    public abstract IReadOnlyList<FormField> Fields { get; }
    public abstract Task<FormResult> InitializeAsync(FormExecutionContext context, CancellationToken ct);
    public abstract Task<FormResult> LoadAsync(string key, FormExecutionContext context, CancellationToken ct);
    public abstract Task<FormResult> DeleteAsync(string key, string version, FormExecutionContext context, CancellationToken ct);
    protected abstract Task<FormResult> CreateAsync(TCreate input, FormExecutionContext context, CancellationToken ct);
    protected abstract Task<FormResult> UpdateAsync(string key, TUpdate input, string version, FormExecutionContext context, CancellationToken ct);
    public Task<FormResult> CreateAsync(JsonElement input, FormExecutionContext context, CancellationToken ct)
        => Bind<TCreate>(input, value => CreateAsync(value, context, ct));
    public Task<FormResult> UpdateAsync(string key, JsonElement input, string version, FormExecutionContext context, CancellationToken ct)
        => Bind<TUpdate>(input, value => UpdateAsync(key, value, version, context, ct));

    private static Task<FormResult> Bind<T>(JsonElement input, Func<T, Task<FormResult>> execute) where T : class
    {
        try {
            var value = input.Deserialize<T>(Json) ?? throw new JsonException();
            var errors = new List<ValidationResult>();
            if (!Validator.TryValidateObject(value, new ValidationContext(value), errors, true))
                return Task.FromResult(new FormResult(422, Errors: errors.SelectMany(e => e.MemberNames.DefaultIfEmpty("").Select(name => (name, e.ErrorMessage)))
                    .GroupBy(e => JsonNamingPolicy.CamelCase.ConvertName(e.name)).ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage ?? "Invalid value.").ToArray())));
            return execute(value);
        } catch (JsonException) { return Task.FromResult(FormResult.Invalid("", "One or more values have an invalid type.")); }
    }
}

/// <summary>
/// Supplies resources that are defined outside code, such as the published data source nodes, so the form
/// endpoints, the publishing validator and the back-office pickers find them by alias beside the C# resources.
/// </summary>
public interface IFormResourceProvider
{
    IEnumerable<IFormResource> Resources { get; }
}

/// <summary>
/// The resources a form can bind to, by alias. C# resources come first and keep their alias when a provider
/// offers the same one: code is the authority, and publishing a data source with a taken alias is refused.
/// Provider resources are read on first use, so a request that only needs a C# resource never reads content.
/// </summary>
public sealed class FormResourceRegistry(IEnumerable<IFormResource> resources, IEnumerable<IFormResourceProvider> providers)
{
    private readonly Dictionary<string, IFormResource> code = resources.ToDictionary(r => r.Alias, StringComparer.Ordinal);
    private IReadOnlyDictionary<string, IFormResource>? all;

    public IReadOnlyDictionary<string, IFormResource> Resources => all ??= Merge();
    public IReadOnlyDictionary<string, IFormResource> RegisteredResources => code;
    public IFormResource? Find(string alias) => code.GetValueOrDefault(alias) ?? Resources.GetValueOrDefault(alias);

    private Dictionary<string, IFormResource> Merge()
    {
        var merged = new Dictionary<string, IFormResource>(code, StringComparer.Ordinal);
        foreach (var resource in providers.SelectMany(provider => provider.Resources)) merged.TryAdd(resource.Alias, resource);
        return merged;
    }
}
