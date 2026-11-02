using System.Security.Claims;
using InteractiveReport.AspNetCore;
using Umbraco.Cms.Core.Services;

namespace AppBlocks.InteractiveReports.Reports;

/// <summary>Directory choices use the same identifiers as Umbraco's backoffice and member cookies.</summary>
public sealed class ReportAdministrationUsers(IUserService users, IMemberService members)
{
    public ValueTask<IEnumerable<ClaimsIdentity>?> Search(InteractiveReportUserSearch search, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var choices = users.GetAll(0, search.Limit, out _, filter: search.Search)
            .Where(user => user.IsApproved && !user.IsLockedOut)
            .Select(user => Identity(user.Key.ToString(), user.Name ?? user.Username, user.Email, "Backoffice"))
            .Concat(members.GetAll(0L, search.Limit, out _, filter: search.Search)
                .Where(member => member.IsApproved && !member.IsLockedOut)
                .Select(member => Identity(member.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), member.Name ?? member.Username, member.Email, "Member")))
            .ToArray();
        return ValueTask.FromResult<IEnumerable<ClaimsIdentity>?>(choices);
    }

    private static ClaimsIdentity Identity(string key, string name, string email, string kind) => new([
        new(ClaimTypes.NameIdentifier, key), new(ClaimTypes.Name, $"{name} ({kind})"), new(ClaimTypes.Email, email),
    ]);
}
