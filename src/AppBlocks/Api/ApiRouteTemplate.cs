using AppBlocks.DataSources;

namespace AppBlocks.Api;

/// <summary>
/// An API endpoint's route below the base path, such as <c>orders/:id/lines</c>: up to eight segments, each a literal or a
/// <c>:name</c> parameter that binds as <c>:PARAM.name</c>. Literals compare ignoring case, as ASP.NET Core routes do.
/// </summary>
public sealed class ApiRouteTemplate
{
    public const int MaxSegments = 8;
    public const int MaxLength = 400;

    private ApiRouteTemplate(string text, IReadOnlyList<Segment> segments)
    {
        Text = text;
        Segments = segments;
    }

    /// <summary>One segment: a literal, or a parameter when <see cref="IsParameter"/>, whose <see cref="Value"/> is its name.</summary>
    public sealed record Segment(string Value, bool IsParameter);

    /// <summary>The template as written, without a leading slash.</summary>
    public string Text { get; }
    public IReadOnlyList<Segment> Segments { get; }
    public IEnumerable<string> Parameters => Segments.Where(s => s.IsParameter).Select(s => s.Value);
    public int Literals => Segments.Count(s => !s.IsParameter);

    /// <summary>Reads a template, reporting every mistake to <paramref name="errors"/>; one leading slash is allowed and dropped.</summary>
    public static ApiRouteTemplate? Parse(string text, ICollection<string> errors)
    {
        var start = errors.Count;
        text = text.Trim();
        if (text.StartsWith('/')) text = text[1..];
        if (text.Length == 0) { errors.Add("Enter the route, such as orders/:id."); return null; }
        if (text.Length > MaxLength) { errors.Add($"The route is longer than {MaxLength} characters."); return null; }
        if (text.IndexOfAny(['?', '#']) >= 0) errors.Add("The route is a path only; query parameters are listed under Query parameters.");
        var parts = text.Split('/');
        if (parts.Length > MaxSegments) errors.Add($"The route has more than {MaxSegments} segments.");
        var segments = new List<Segment>();
        foreach (var part in parts)
        {
            if (part.Length == 0) { errors.Add("The route has an empty segment: remove the doubled or trailing '/'."); continue; }
            if (part.StartsWith(':'))
            {
                var name = part[1..];
                if (!SqlTemplate.IsIdentifier(name)) errors.Add($"'{part}' is not a route parameter: write a colon and a name of letters, digits and '_', starting with a letter or '_'.");
                else if (segments.Any(s => s.IsParameter && s.Value.Equals(name, StringComparison.OrdinalIgnoreCase))) errors.Add($"The route names the parameter :{name} twice.");
                else segments.Add(new(name, true));
            }
            else if (part is "." or "..") errors.Add("The route cannot have '.' or '..' segments.");
            else if (!part.All(IsLiteralChar)) errors.Add($"'{part}' may use only letters, digits, '-', '.', '_' and '~'.");
            else segments.Add(new(part, false));
        }
        return errors.Count == start ? new ApiRouteTemplate(text, segments) : null;
    }

    private static bool IsLiteralChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~';

    /// <summary>Whether two templates answer the same requests: as many segments, with a parameter or the same literal at each.</summary>
    public bool Overlaps(ApiRouteTemplate other) => Segments.Count == other.Segments.Count
        && Segments.Zip(other.Segments).All(pair => pair.First.IsParameter == pair.Second.IsParameter
            && (pair.First.IsParameter || pair.First.Value.Equals(pair.Second.Value, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The parameter values when the path's segments match, decoded; null otherwise, and for an empty parameter value.</summary>
    public IReadOnlyDictionary<string, string>? Match(IReadOnlyList<string> path)
    {
        if (path.Count != Segments.Count) return null;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < path.Count; i++)
        {
            var segment = Segments[i];
            if (!segment.IsParameter) { if (!segment.Value.Equals(path[i], StringComparison.OrdinalIgnoreCase)) return null; }
            else if (path[i].Length == 0) return null;
            else values[segment.Value] = path[i];
        }
        return values;
    }

    /// <summary>
    /// The order requests are matched in: more literal segments first, then the template whose first literal comes earlier, so
    /// <c>orders/new</c> wins over <c>orders/:id</c>, and <c>orders/:id</c> over <c>:kind/new</c>; the text breaks a remaining tie.
    /// </summary>
    public static int Precedence(ApiRouteTemplate a, ApiRouteTemplate b)
    {
        var literals = b.Literals.CompareTo(a.Literals);
        if (literals != 0) return literals;
        for (var i = 0; i < Math.Min(a.Segments.Count, b.Segments.Count); i++)
            if (a.Segments[i].IsParameter != b.Segments[i].IsParameter) return a.Segments[i].IsParameter ? 1 : -1;
        return StringComparer.OrdinalIgnoreCase.Compare(a.Text, b.Text);
    }

    /// <summary>
    /// The segments of a request path below the base path. ASP.NET Core leaves an encoded '/' in the path as <c>%2F</c>, which
    /// belongs to the segment's value; everything else is already decoded.
    /// </summary>
    public static IReadOnlyList<string> SplitPath(string path)
        => (path.StartsWith('/') ? path[1..] : path).Split('/').Select(s => s.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)).ToArray();

    public override string ToString() => Text;
}
