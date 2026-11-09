using System.Globalization;
using System.Security.Claims;
using AppBlocks.Authentication;
using AppBlocks.DataSources;
using AppBlocks.Forms;

namespace AppBlocks.Blocks;

/// <summary>
/// The values a field block's default can name, fixed when the page renders: who is signed in, the time, and the
/// page's query string. They use the names the data sources use for their bind variables, so an editor learns one
/// vocabulary.
/// </summary>
/// <param name="Key">The application's key for the account (<see cref="ApplicationAccessPolicy.UserKey"/>): the member id, or <c>backoffice:</c> and the user id. Empty for an anonymous visitor, whose person variables all resolve to nothing.</param>
/// <param name="Sid">The external login provider's key for the account, which is not necessarily a Windows SID; null when no external login is linked.</param>
/// <param name="Name">The member's or backoffice user's username.</param>
/// <param name="Email">The account's email address.</param>
/// <param name="Now">The web server's local date and time when the page rendered, with its offset, from which <c>:NOW_UTC</c> takes the UTC moment.</param>
/// <param name="Query">The page's query string, which <c>:PARAM.name</c> reads.</param>
/// <param name="Application">The values the application's bind variable providers computed for the page, which <c>:APP.name</c> reads; null where none ran.</param>
public sealed record AppBindVariables(string Key, string? Sid, string? Name, string? Email, DateTimeOffset Now, IQueryCollection Query, IReadOnlyDictionary<string, object?>? Application = null)
{
    /// <summary>
    /// Resolves a default for a field of the given kind (<see cref="Forms.FormField.Kind"/>): one of the variables,
    /// whole and in any case, as <see cref="BindVariable.Parse"/> reads it, or else the literal text. A variable without
    /// a value, such as the SID of an account that has none or a parameter the address does not carry, resolves to null,
    /// which leaves the field as the resource initialised it. <c>:NOW</c> is written as the kind's own value: a date, a
    /// time, an ISO date and time with its offset, or for text the date and time in the invariant <c>yyyy-MM-dd HH:mm</c>;
    /// a number or a checkbox has no "now". <c>:NOW_UTC</c> is the same moment in UTC: the UTC date or time, for a date-time
    /// field the ISO date and time with <c>+00:00</c>, and for a text field the ISO form with <c>Z</c>, such as
    /// <c>2026-11-01T07:30:00Z</c>. A date-time value carries its offset, so <c>:NOW</c> and <c>:NOW_UTC</c> give a
    /// date-time field the same moment, which its data source stores in its own convention.
    /// </summary>
    public string? Resolve(string? value, string kind)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        // Not one of the variables, so the editor meant the text itself.
        if (text[0] != ':' || BindVariable.Parse(text[1..]) is not { } variable) return text;
        var resolved = variable.Kind switch {
            BindVariableKind.Param => Query[variable.Name!] is { Count: > 0 } values ? values[0] : null,
            BindVariableKind.App => BindVariableProviders.Text(Application?.GetValueOrDefault(variable.Name!)),
            BindVariableKind.UserSid => Sid,
            BindVariableKind.UserName => Name,
            BindVariableKind.UserKey => Key,
            BindVariableKind.UserEmail => Email,
            BindVariableKind.Now => kind switch {
                FieldKinds.Date => Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                FieldKinds.Time => Now.ToString("HH:mm", CultureInfo.InvariantCulture),
                FieldKinds.DateTime => Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
                FieldKinds.Text => Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                _ => null,
            },
            BindVariableKind.NowUtc => kind switch {
                FieldKinds.Date => Now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                FieldKinds.Time => Now.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                FieldKinds.DateTime => Now.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
                FieldKinds.Text => FieldValues.UtcText(Now),
                _ => null,
            },
            // :USER_GROUPS is a list, and a field default has no record or backing class for :FIELD. and :ARG., so the text stays as written.
            _ => text,
        };
        return string.IsNullOrEmpty(resolved) ? null : resolved;
    }
}

/// <summary>
/// Reads the signed-in person's facts through <see cref="IDataSourceUserResolver"/>, as the data sources do: from Umbraco rather than
/// from the cookie's claims, so a renamed account or a newly linked SID applies at once, and only for an approved account that is not
/// locked out. The time is the data source runtime's, so a page's field defaults and its data sources read one clock.
/// </summary>
public sealed class AppBindVariableResolver(IDataSourceUserResolver users, DataSourceRuntime runtime, BindVariableProviders? providers = null)
{
    /// <param name="pageKey">The page rendering, which the person's page permissions and the providers see; null leaves the application variables without values.</param>
    public async Task<AppBindVariables> ResolveAsync(ClaimsPrincipal person, IQueryCollection query, Guid? pageKey = null)
    {
        // Field defaults are resolved once per page, before any form, so the resolver and the providers see the page and an empty form key.
        var context = new FormExecutionContext(person, pageKey ?? Guid.Empty, Guid.Empty, "",
            Parameters: query.Where(p => p.Value.Count == 1).ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.OrdinalIgnoreCase));
        var user = await users.ResolveAsync(context, default) ?? DataSourceUser.Anonymous;
        var application = pageKey is not null && providers is { Any: true } ? await providers.ResolveAsync(context, default) : null;
        return new(user.Key, user.Sid, user.Name, NonEmpty(user.Email), runtime.Now(), query, application);
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
