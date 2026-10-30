using InteractiveReport.AspNetCore;

namespace UmbracoAppHost.Databases;

/// <summary>Compatibility for sites registering application databases through Interactive Reports.</summary>
public static class ReportApplicationDatabases
{
    public static InteractiveReportBuilder AddApplicationDatabase(this InteractiveReportBuilder reports, string name,
        ApplicationSchema? schema = null, string? defaultFile = null)
        => reports.AddConnection(name, provider => ApplicationDatabase.CreateApplicationConnection(provider, name, schema, defaultFile));
}
