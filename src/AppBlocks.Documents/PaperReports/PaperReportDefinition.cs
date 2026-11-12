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
    public const string Alias = AppBlocks.DocumentOutput.PaperReportLinks.ContentTypeAlias;

    /// <summary>Every row is held in memory, rendered and encoded, so a report's result is bounded like any other query's.</summary>
    public const int DefaultMaximumRows = 5000, MaximumRowsLimit = 50_000;

    public static PaperReportDefinition Read(Func<string, object?> value)
    {
        string Text(string alias) => Convert.ToString(value(alias), CultureInfo.InvariantCulture)?.Trim() ?? "";
        var source = value("dataSource");
        var key = source is Umbraco.Cms.Core.Models.PublishedContent.IPublishedContent node ? node.Key : AppBlocks.DocumentOutput.PaperReportLinks.PickedKey(Text("dataSource"));
        if (key is null) throw new DataSourceException("Choose a published SQL data source for the paper report.");
        var parameters = Text("parameters") is { Length: > 0 } json
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [] : new Dictionary<string, string>();
        var settings = Text("pageOptions");
        var options = settings.Length > 0 ? JsonSerializer.Deserialize<RenderOptions>(settings, JsonDefaults.Options) ?? new() : new RenderOptions();
        // Pressroom's own defaults are one fixed time zone and an English footer: options that name neither get the web server's
        // time zone and a footer of page numbers alone.
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (settings.Length > 0)
        {
            using var written = JsonDocument.Parse(settings);
            if (written.RootElement.ValueKind == JsonValueKind.Object) named.UnionWith(written.RootElement.EnumerateObject().Select(property => property.Name));
        }
        options = options with {
            TimeZone = named.Contains(nameof(RenderOptions.TimeZone)) ? options.TimeZone : AppBlocks.DocumentOutput.DocumentRenderOptions.ServerTimeZone,
            FooterTemplate = named.Contains(nameof(RenderOptions.FooterTemplate)) ? options.FooterTemplate : AppBlocks.DocumentOutput.DocumentRenderOptions.PageNumberFooter,
        };
        var errors = RequestValidator.Validate(new RenderRequest { Html = "<!doctype html><html><body></body></html>", Options = options }, maxTimeoutMs: 300_000);
        if (errors.Count > 0) throw new DataSourceException(string.Join(" ", errors.SelectMany(e => e.Value)));
        var rows = Text("maximumRows");
        var maximumRows = rows.Length == 0 ? DefaultMaximumRows
            : int.TryParse(rows, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed is > 0 and <= MaximumRowsLimit ? parsed
            : throw new DataSourceException($"Maximum rows must be a whole number from 1 to {MaximumRowsLimit}, or empty for {DefaultMaximumRows}.");
        // Empty is the culture the site serves the download request in.
        var culture = Text("culture");
        // A bind variable resolves on each download; a literal is checked here rather than failing every download.
        if (culture.Length > 0 && !culture.StartsWith(':'))
        {
            try { CultureInfo.GetCultureInfo(culture); }
            catch (CultureNotFoundException)
            { throw new DataSourceException($"'{culture}' is not a culture. Enter a name such as en-GB or fr-FR, a bind variable such as :PARAM.culture, or nothing for the site's culture."); }
        }
        return new(key.Value, parameters, options,
            Text("fileName") is { Length: > 0 } name ? name : "report-{date}.pdf", culture, maximumRows);
    }

    /// <summary>
    /// The request's parameters, bound as forms bind them: bounded, single-valued, and without the culture or the
    /// delivery's own <c>format</c> switch.
    /// </summary>
    public static IQueryCollection Query(IQueryCollection query) => new QueryCollection(FormRequestValues.Parameters(query, "format")
        .ToDictionary(p => p.Key, p => new StringValues(p.Value), StringComparer.OrdinalIgnoreCase));

    public IReadOnlyDictionary<string, string> BindParameters(AppBindVariables variables)
    {
        // An empty map forwards the request's parameters (see Query). A map can rename them or supply literal/user defaults.
        if (Parameters.Count == 0) return variables.Query.ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        return Parameters.Select(p => (p.Key, Value: variables.Resolve(p.Value, "text")))
            .Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value!, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The report's culture: the one it names or its bind variable resolves to, otherwise <paramref name="requestCulture"/>.</summary>
    public CultureInfo ResolveCulture(AppBindVariables variables, CultureInfo requestCulture)
        => Culture.Length > 0 && variables.Resolve(Culture, "text") is { Length: > 0 } name ? CultureInfo.GetCultureInfo(name) : requestCulture;

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
