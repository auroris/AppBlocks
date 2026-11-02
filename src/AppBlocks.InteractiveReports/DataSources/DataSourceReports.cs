using System.Security.Claims;
using AppBlocks.InteractiveReports.Reports;
using InteractiveReport.AspNetCore;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using AppBlocks.Blocks;
using AppBlocks.Forms;
using AppBlocks.DataSources;

namespace AppBlocks.InteractiveReports.DataSources;

internal static class DataSourceReportParameters
{
    internal static Dictionary<string, string>? From(HttpContext? http)
    {
        if (http is null) return null;
        IEnumerable<KeyValuePair<string, StringValues>> parameters = http.Request.Query.Where(p => !p.Key.Equals(AppBlockContent.CultureParameter, StringComparison.OrdinalIgnoreCase));
        // Keep page parameters separate from the adapter's own pageSize, search and export parameters.
        if (http.Request.Query.TryGetValue("appPageQuery", out var pageQuery) && pageQuery.Count == 1)
            parameters = QueryHelpers.ParseQuery(pageQuery.ToString());
        return parameters.Where(p => p.Value.Count == 1)
            .ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>Request-owned values: report SQL carries parameters, never interpolated user or URL data.</summary>
public sealed class DataSourceReportBindings(DataSourceRuntime runtime, IDataSourceUserResolver users, IHttpContextAccessor http, BindVariableProviders? providers = null)
{
    internal const string Prefix = "apphost:data-source:";
    private readonly Dictionary<string, object?> values = new(StringComparer.Ordinal);
    internal object? Get(string token) => values.GetValueOrDefault(token);

    /// <param name="page">The page a block report renders on, and <paramref name="block"/> its block, for the providers of <c>:APP.</c> values; a configured or previewed report has neither, and its providers see empty keys.</param>
    public async Task<ReportDefinition> BindAsync(ReportDefinition report, DataSourceDefinition source, CancellationToken ct, Guid page = default, Guid block = default)
    {
        var plan = runtime.Plan(source).Plan ?? throw new DataSourceException("The report's data source is not available.");
        var principal = http.HttpContext?.User ?? new ClaimsPrincipal();
        var user = await users.ResolveAsync(principal, ct) ?? DataSourceUser.Anonymous;
        var query = DataSourceReportParameters.From(http.HttpContext);
        var parameters = new SqlParameters(plan.Dialect);
        var now = runtime.Now();
        var app = providers is null ? null : await providers.ResolveAsync(new FormExecutionContext(principal, page, block, source.ComputeFingerprint(), Parameters: query), ct);
        object? Value(BindVariable variable) => DataSourceQueries.Bind(variable, user, query, now, plan.Dialect, app);
        var from = plan.From(parameters, Value);
        var predicate = "{{RowRestriction}}" + (plan.Restriction is null ? "" : " AND (" + plan.Restriction.Render(parameters, Value) + ")");
        report.Sql = plan.Dialect.Select(from, plan.Columns.Select(c => c.Column), [], predicate);
        report.Connection = source.Connection;
        report.ContextParams = new(StringComparer.OrdinalIgnoreCase);
        var sourceToken = Prefix + source.ComputeFingerprint() + ":";
        foreach (var parameter in parameters.Bindings)
        {
            // Stable specifications preserve the adapter's schema cache; the values themselves live only in this request.
            var token = sourceToken + parameter.Name;
            values[token] = parameter.Value is TypedValue typed ? typed.Value : parameter.Value;
            report.ContextParams[parameter.Name] = new ContextParamSpec { Claim = token };
        }
        return report;
    }
}

public sealed record OriginalContextParameters(IContextParameterResolver Resolver);

/// <summary>Preserves the host's resolver for configured reports and resolves only this request's data-source bindings.</summary>
public sealed class DataSourceContextParameters(OriginalContextParameters original, IHttpContextAccessor http) : IContextParameterResolver
{
    public ValueTask<object?> Resolve(string name, ContextParamSpec spec, ClaimsPrincipal? user, CancellationToken ct = default)
    {
        if (spec.Claim?.StartsWith(DataSourceReportBindings.Prefix, StringComparison.Ordinal) != true)
            return original.Resolver.Resolve(name, spec, user, ct);
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(http.HttpContext?.RequestServices.GetRequiredService<DataSourceReportBindings>().Get(spec.Claim));
    }
}

public sealed class DataSourceReportAccess(PublishedReportCatalog catalog, DataSourceQueries queries, IHttpContextAccessor http,
    AppBlockContent? content = null, ReportSourcePreviews? previews = null)
{
    public async ValueTask<RowRestriction> RestrictAsync(InteractiveReportRowRestrictionRequest request, CancellationToken ct)
    {
        var alias = request.SourceReportName ?? request.ReportName;
        DataSourceDefinition? source;
        if (alias.StartsWith(ReportSourcePreviews.Prefix, StringComparison.Ordinal))
        {
            source = previews?.Find(alias, request.User)?.DataSource;
            if (source is null) return RowRestriction.Deny;
        }
        else if (AppBlockContent.ParseReportName(alias, out _, out _))
        {
            if (content is null || await content.ReportAsync(alias) is not { } instance) return RowRestriction.Deny;
            var selection = instance.Block.Content.Value<string>("dataSource", culture: instance.Culture) ?? "";
            source = catalog.ResolveBlockSource(instance.Block.Content.Key, selection, new List<string>());
            if (source is null) return RowRestriction.Deny;
        }
        else { await catalog.LoadAsync(); source = catalog.SourceForReport(alias); }
        if (source is null) return RowRestriction.NotApplicable;
        AppBlockContent.ParseReportName(request.ReportName, out var page, out var block);
        var parameters = DataSourceReportParameters.From(http.HttpContext);
        var context = new FormExecutionContext(request.User, page, block, source.ComputeFingerprint(), Parameters: parameters);
        try
        {
            await queries.AuthorizeAsync(source, context, ct);
            return RowRestriction.Unrestricted;
        }
        catch (UnauthorizedAccessException) { return RowRestriction.Deny; }
    }
}
