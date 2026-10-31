using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Umbraco.Cms.Core.Services;
using AppBlocks.Authentication;

namespace AppBlocks.Blocks;

/// <summary>
/// The values a field block's default can name, fixed when the page renders: who is signed in, the time, and the
/// page's query string. They are APEX's <c>:APP_USER</c> and friends in Umbraco terms, written with the names the
/// data sources use for their bind variables, so an editor learns one vocabulary.
/// </summary>
/// <param name="Key">The application's key for the account (<see cref="ApplicationAccessPolicy.UserKey"/>): the member id, or <c>backoffice:</c> and the user id.</param>
/// <param name="Sid">The Windows SID the account is linked to, its external login key; null for an account that only has a password.</param>
/// <param name="Name">The member's or backoffice user's username.</param>
/// <param name="Email">The account's email address.</param>
/// <param name="Now">The web server's local date and time when the page rendered.</param>
/// <param name="Query">The page's query string, which <c>:PARAM.name</c> reads.</param>
public sealed partial record AppBindVariables(string Key, string? Sid, string? Name, string? Email, DateTimeOffset Now, IQueryCollection Query)
{
    /// <summary>
    /// Resolves a default for a field of the given kind (<see cref="Forms.FormField.Kind"/>): one of the variables,
    /// whole and in any case, or else the literal text. A variable without a value, such as the SID of an account
    /// that has none or a parameter the address does not carry, resolves to null, which leaves the field as the
    /// resource initialised it. <c>:NOW</c> is written as the kind's own value: a date, a time, an ISO date and
    /// time with its offset, or for text the date and time in the invariant <c>yyyy-MM-dd HH:mm</c>; a number or a
    /// checkbox has no "now".
    /// </summary>
    public string? Resolve(string? value, string kind)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        var variable = Variable().Match(text);
        if (!variable.Success) return text;
        var resolved = variable.Groups["param"].Success
            ? Query[variable.Groups["param"].Value] is { Count: > 0 } values ? values[0] : null
            : variable.Groups["name"].Value.ToUpperInvariant() switch {
                "USER_SID" => Sid,
                "USER_NAME" => Name,
                "USER_KEY" => Key,
                "USER_EMAIL" => Email,
                "NOW" => kind switch {
                    "date" => Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    "time" => Now.ToString("HH:mm", CultureInfo.InvariantCulture),
                    "datetime" => Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
                    "text" => Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                    _ => null,
                },
                // Not one of the variables, so the editor meant the text itself.
                _ => text,
            };
        return string.IsNullOrEmpty(resolved) ? null : resolved;
    }

    [GeneratedRegex(@"^:(?:PARAM\.(?<param>[A-Za-z0-9_\-]+)|(?<name>[A-Za-z_]+))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Variable();
}

/// <summary>
/// Reads the signed-in person's facts from Umbraco rather than from the cookie's claims, so a renamed account or
/// a newly linked SID applies at once. A member is read by the integer id its cookie carries, a backoffice user
/// by its key; the SID is the provider key of the account's external login, which every sign-in provider of the
/// platform stores.
/// </summary>
public sealed class AppBindVariableResolver(IUserService users, IMemberService members, IExternalLoginWithKeyService logins)
{
    public async Task<AppBindVariables> ResolveAsync(ClaimsPrincipal person, IQueryCollection query)
    {
        var id = person.FindFirstValue(ClaimTypes.NameIdentifier);
        Guid? key = null; string? name = null, email = null;
        if (ApplicationAccessPolicy.IsBackOfficeIdentity(person))
        {
            // The built-in administrator's legacy id is -1.
            var user = Guid.TryParse(id, out var userKey) ? await users.GetAsync(userKey)
                : int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var legacy) ? users.GetUserById(legacy) : null;
            (key, name, email) = (user?.Key, user?.Username, user?.Email);
        }
        else if (person.Identity?.AuthenticationType == IdentityConstants.ApplicationScheme)
        {
            var member = int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var memberId) ? members.GetById(memberId)
                : Guid.TryParse(id, out var memberKey) ? (await members.GetByKeysAsync(memberKey)).FirstOrDefault() : null;
            (key, name, email) = (member?.Key, member?.Username, member?.Email);
        }
        var sid = key is { } account
            ? logins.GetExternalLogins(account).Select(login => login.ProviderKey).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            : null;
        return new(ApplicationAccessPolicy.UserKey(person), sid, name, NonEmpty(email), DateTimeOffset.Now, query);
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
