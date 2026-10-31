using System.Security.Claims;
using AppBlocks.InteractiveReports.DataSources;

namespace AppBlocks.InteractiveReports.Reports;

/// <summary>Short-lived, user-owned definitions for configuring an unpublished block's starting view.</summary>
public sealed class ReportSourcePreviews(TimeProvider? time = null)
{
    public const string Prefix = "appblocks-preview-";
    private sealed record Entry(string Owner, PublishedReport Report, DateTimeOffset Expires);
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly TimeProvider clock = time ?? TimeProvider.System;

    public string Add(PublishedReport report, ClaimsPrincipal user)
    {
        var owner = Owner(user) ?? throw new UnauthorizedAccessException();
        lock (entries)
        {
            foreach (var key in entries.Where(e => e.Value.Expires <= clock.GetUtcNow()).Select(e => e.Key).ToArray()) entries.Remove(key);
            if (entries.Count >= 100) entries.Remove(entries.MinBy(e => e.Value.Expires).Key);
            var name = Prefix + Guid.NewGuid().ToString("N");
            entries[name] = new(owner, report, clock.GetUtcNow().AddMinutes(10));
            return name;
        }
    }

    public PublishedReport? Find(string name, ClaimsPrincipal? user)
    {
        lock (entries) return entries.TryGetValue(name, out var entry)
            && entry.Expires > clock.GetUtcNow() && entry.Owner == Owner(user) ? entry.Report : null;
    }

    private static string? Owner(ClaimsPrincipal? user) => user?.Identity?.IsAuthenticated == true
        ? user.FindFirstValue(ClaimTypes.NameIdentifier) : null;
}
