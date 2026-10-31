using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Primitives;
using Pressroom.Contracts;
using AppBlocks.Blocks;
using AppBlocks.DataSources;
using AppBlocks.Forms;

namespace AppBlocks.Documents.PaperReports;

public sealed record PaperReportDefinition(Guid DataSourceKey, IReadOnlyDictionary<string, string> Parameters,
    RenderOptions Options, string FileName, string Culture, int MaximumRows)
{
    public const string Alias = "paperReport";

    /// <summary>Every row is held in memory, rendered and encoded, so a report's result is bounded like any other query's.</summary>
    public const int DefaultMaximumRows = 5000, MaximumRowsLimit = 50_000;

    public static PaperReportDefinition Read(Func<string, object?> value)
    {
        string Text(string alias) => Convert.ToString(value(alias), CultureInfo.InvariantCulture)?.Trim() ?? "";
        var source = value("dataSource");
        var key = source is Umbraco.Cms.Core.Models.PublishedContent.IPublishedContent node ? node.Key : PickedKey(Text("dataSource"));
        if (key is null) throw new DataSourceException("Choose a published SQL data source for the paper report.");
        var parameters = Text("parameters") is { Length: > 0 } json
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [] : new Dictionary<string, string>();
        var options = Text("pageOptions") is { Length: > 0 } settings
            ? JsonSerializer.Deserialize<RenderOptions>(settings, JsonDefaults.Options) ?? new() : new RenderOptions();
        var errors = RequestValidator.Validate(new RenderRequest { Html = "<!doctype html><html><body></body></html>", Options = options }, maxTimeoutMs: 300_000);
        if (errors.Count > 0) throw new DataSourceException(string.Join(" ", errors.SelectMany(e => e.Value)));
        var rows = Text("maximumRows");
        var maximumRows = rows.Length == 0 ? DefaultMaximumRows
            : int.TryParse(rows, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed is > 0 and <= MaximumRowsLimit ? parsed
            : throw new DataSourceException($"Maximum rows must be a whole number from 1 to {MaximumRowsLimit}, or empty for {DefaultMaximumRows}.");
        var culture = Text("culture") is { Length: > 0 } chosen ? chosen : "en-CA";
        // A bind variable resolves on each download; a literal is checked here rather than failing every download.
        if (!culture.StartsWith(':'))
        {
            try { CultureInfo.GetCultureInfo(culture); }
            catch (CultureNotFoundException)
            { throw new DataSourceException($"'{culture}' is not a culture. Enter a name such as en-CA or fr-CA, or a bind variable such as :PARAM.culture."); }
        }
        return new(key.Value, parameters, options,
            Text("fileName") is { Length: > 0 } name ? name : "report-{date}.pdf", culture, maximumRows);
    }

    /// <summary>
    /// The request's parameters, bound as forms bind them: bounded, single-valued, and without the culture or the
    /// delivery's own <c>format</c> switch.
    /// </summary>
    public static IQueryCollection Query(IQueryCollection query) => new QueryCollection(FormEndpoints.Parameters(query, "format")
        .ToDictionary(p => p.Key, p => new StringValues(p.Value), StringComparer.OrdinalIgnoreCase));

    public static Guid? PickedKey(string value)
    {
        if (Guid.TryParse(value, out var key)) return key;
        const string prefix = "umb://document/";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Guid.TryParse(value[prefix.Length..], out key) ? key : null;
    }

    public IReadOnlyDictionary<string, string> BindParameters(AppBindVariables variables)
    {
        // An empty map forwards the request's parameters (see Query). A map can rename them or supply literal/user defaults.
        if (Parameters.Count == 0) return variables.Query.ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        return Parameters.Select(p => (p.Key, Value: variables.Resolve(p.Value, "text")))
            .Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value!, StringComparer.OrdinalIgnoreCase);
    }

    public CultureInfo ResolveCulture(AppBindVariables variables) => CultureInfo.GetCultureInfo(variables.Resolve(Culture, "text") ?? "en-CA");

    public string ResolveFileName(IReadOnlyDictionary<string, string> parameters, DateTimeOffset now)
    {
        var name = Regex.Replace(FileName, @"\{([^{}]+)\}", match => {
            var token = match.Groups[1].Value;
            if (token.Equals("date", StringComparison.OrdinalIgnoreCase)) return now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (token.StartsWith("date:", StringComparison.OrdinalIgnoreCase)) return now.ToString(token[5..], CultureInfo.InvariantCulture);
            return parameters.TryGetValue(token, out var parameter) ? parameter : "";
        });
        return RequestValidator.SanitizeFileName(name);
    }
}
