using System.Security.Claims;
using InteractiveReport.AspNetCore;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Services;

namespace AppBlocks.InteractiveReports.Reports;

/// <summary>Directory choices use the same identifiers as Umbraco's backoffice and member cookies.</summary>
public sealed class ReportAdministrationUsers(IUserService users, IMemberService members, IOptionsMonitor<InteractiveReportOptions> options)
    : IInteractiveReportUserProvider
{
    public ValueTask<IReadOnlyCollection<InteractiveReportUser>?> SearchUsers(InteractiveReportUserSearch search, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var choices = users.GetAll(0, search.Limit, out _, "Name", Direction.Ascending, null, null, search.Search)
            .Where(user => user.IsApproved && !user.IsLockedOut)
            .Select(user => Choice(user.Key.ToString(), user.Username, user.Name, user.Email, "Backoffice"))
            .Concat(members.GetAll(0L, search.Limit, out _, "Name", Direction.Ascending, true, null, search.Search ?? "")
                .Where(member => member.IsApproved && !member.IsLockedOut)
                .Select(member => Choice(member.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), member.Username, member.Name, member.Email, "Member")))
            .OfType<InteractiveReportUser>()
            .ToArray();
        return ValueTask.FromResult<IReadOnlyCollection<InteractiveReportUser>?>(choices);
    }

    private InteractiveReportUser? Choice(string key, string username, string? name, string email, string kind)
    {
        var identity = new ClaimsIdentity([new(ClaimTypes.NameIdentifier, key), new(ClaimTypes.Name, username), new(ClaimTypes.Email, email)]);
        var choice = InteractiveReportUser.FromIdentity(identity, options.CurrentValue.IdentityClaim);
        return choice is null ? null : choice with { Display = $"{name ?? username} / {username} ({kind})" };
    }
}
