using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Microsoft.AspNetCore.StaticFiles;

namespace AppBlocks.Documents.PaperReports;

/// <summary>
/// Packages a rendered report's stylesheets, images, fonts and scripts into one portable HTML file. Packaging is best
/// effort: an asset that cannot be read keeps its original reference and is logged, so one missing image never costs
/// the download. The site's own assets come from disk only. Other http(s) assets are fetched without credentials,
/// cookies or redirects, and never from loopback or link-local addresses.
/// </summary>
public sealed partial class SelfContainedHtml(IWebHostEnvironment environment, IHttpClientFactory clients, ILogger<SelfContainedHtml> logger)
{
    /// <summary>The named client that fetches authored http(s) assets; <see cref="CreateHandler"/> is its handler.</summary>
    public const string ClientName = "PaperReportAssets";
    private static readonly FileExtensionContentTypeProvider MimeTypes = new();

    public async Task<string> EmbedAsync(string html, Uri documentUri, PathString pathBase, string? printStylesheet, CancellationToken ct, string? pageStyle = null)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        var origin = documentUri;
        var baseElement = document.DocumentNode.SelectSingleNode("//base[@href]");
        if (baseElement is not null)
        {
            if (Uri.TryCreate(documentUri, HtmlEntity.DeEntitize(baseElement.GetAttributeValue("href", "")), out var based))
            {
                documentUri = based;
                baseElement.Remove();
            }
            else logger.LogWarning("A paper report's base address {Base} is not a URL; its assets resolve against the page.", baseElement.GetAttributeValue("href", ""));
        }
        // An asset that cannot be read is remembered as null, so a repeated reference is tried once per document.
        var assets = new Dictionary<Uri, (byte[] Bytes, string MediaType)?>();
        var head = document.DocumentNode.SelectSingleNode("//head");
        if (head is not null && printStylesheet is not null)
        {
            var link = document.CreateElement("link");
            link.SetAttributeValue("rel", "stylesheet");
            link.SetAttributeValue("href", new Uri(origin, pathBase + printStylesheet).AbsoluteUri);
            head.PrependChild(link);
        }
        if (head is not null && pageStyle is not null)
        {
            var style = document.CreateElement("style");
            style.InnerHtml = pageStyle;
            head.AppendChild(style);
        }

        async Task<(byte[] Bytes, string MediaType)?> Read(Uri uri)
        {
            if (!assets.TryGetValue(uri, out var asset)) assets[uri] = asset = await Load(uri);
            return asset;
        }

        async Task<(byte[] Bytes, string MediaType)?> Load(Uri uri)
        {
            if (uri.Scheme == origin.Scheme && uri.Authority == origin.Authority)
            {
                var path = Uri.UnescapeDataString(uri.AbsolutePath);
                if (pathBase.HasValue && path.StartsWith(pathBase.Value + "/", StringComparison.OrdinalIgnoreCase)) path = path[pathBase.Value.Length..];
                var file = environment.WebRootFileProvider.GetFileInfo(path.TrimStart('/'));
                if (file.Exists)
                {
                    await using var stream = file.CreateReadStream();
                    using var memory = new MemoryStream();
                    await stream.CopyToAsync(memory, ct);
                    return (memory.ToArray(), MimeTypes.TryGetContentType(path, out var mime) ? mime : "application/octet-stream");
                }
                // The origin is the request's Host header, so it never decides where the server connects.
                logger.LogDebug("Paper report asset {Asset} is not a file of the site; its reference is kept.", uri);
                return null;
            }
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                logger.LogDebug("Paper report asset {Asset} is not an http or https address; its reference is kept.", uri);
                return null;
            }
            if (IsRefusedHost(uri))
            {
                logger.LogWarning("Paper report asset {Asset} is on a loopback or link-local address, which assets are never fetched from; its reference is kept.", uri);
                return null;
            }
            try
            {
                using var response = await clients.CreateClient(ClientName).GetAsync(uri, ct);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Paper report asset {Asset} answered {Status}; its reference is kept.", uri, (int)response.StatusCode);
                    return null;
                }
                return (await response.Content.ReadAsByteArrayAsync(ct), response.Content.Headers.ContentType?.MediaType
                    ?? (MimeTypes.TryGetContentType(uri.AbsolutePath, out var guessed) ? guessed : "application/octet-stream"));
            }
            catch (Exception error) when (error is HttpRequestException or NotSupportedException or UriFormatException or InvalidOperationException
                || error is TaskCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogWarning("Paper report asset {Asset} could not be read ({Failure}: {Message}); its reference is kept.", uri, error.GetType().Name, error.Message);
                return null;
            }
        }

        // The packaged data URL, the value itself when it needs no packaging, or null to keep the reference.
        async Task<string?> Data(string value, Uri relativeTo)
        {
            var text = HtmlEntity.DeEntitize(value.Trim());
            if (text.Length == 0 || text.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || text[0] == '#') return text;
            if (!Uri.TryCreate(relativeTo, text, out var uri))
            {
                logger.LogWarning("Paper report asset {Asset} is not a URL; its reference is kept.", text);
                return null;
            }
            return await Read(uri) is { } asset ? "data:" + asset.MediaType + ";base64," + Convert.ToBase64String(asset.Bytes) + uri.Fragment : null;
        }

        async Task<string> Css(string css, Uri relativeTo, HashSet<Uri> importing)
        {
            css = await ReplaceAsync(Imports(), css, async match => {
                if (!Uri.TryCreate(relativeTo, match.Groups["url"].Value, out var uri))
                {
                    logger.LogWarning("Paper report stylesheet {Asset} is not a URL; its import is kept.", match.Groups["url"].Value);
                    return match.Value;
                }
                if (!importing.Add(uri)) return ""; // A cyclic CSS import contributes no new rules.
                try
                {
                    if (await Read(uri) is not { } asset) return match.Value;
                    var nested = await Css(Encoding.UTF8.GetString(asset.Bytes), uri, importing);
                    var media = match.Groups["media"].Value.Trim();
                    return media.Length == 0 ? nested : "@media " + media + " {\n" + nested + "\n}";
                }
                finally { importing.Remove(uri); }
            });
            return await ReplaceAsync(Urls(), css, async match => await Data(match.Groups["url"].Value, relativeTo) is { } data ? "url(\"" + data + "\")" : match.Value);
        }

        foreach (var link in document.DocumentNode.SelectNodes("//link[@href]")?.ToArray() ?? [])
        {
            var href = link.GetAttributeValue("href", "");
            if (link.GetAttributeValue("rel", "").Split(' ').Contains("stylesheet", StringComparer.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(documentUri, HtmlEntity.DeEntitize(href), out var uri))
                {
                    logger.LogWarning("Paper report stylesheet {Asset} is not a URL; its link is kept.", href);
                    continue;
                }
                // An unreadable stylesheet keeps its link, which a browser printing the file can still follow.
                if (await Read(uri) is not { } asset) continue;
                var style = document.CreateElement("style");
                if (link.Attributes["media"] is { } media) style.SetAttributeValue("media", media.Value);
                style.InnerHtml = await Css(Encoding.UTF8.GetString(asset.Bytes), uri, [uri]);
                link.ParentNode!.ReplaceChild(style, link);
            }
            else if (link.GetAttributeValue("rel", "").Contains("icon", StringComparison.OrdinalIgnoreCase) && await Data(href, documentUri) is { } icon)
                link.SetAttributeValue("href", icon);
        }
        foreach (var style in document.DocumentNode.SelectNodes("//style")?.ToArray() ?? [])
            style.InnerHtml = await Css(style.InnerHtml, documentUri, []);
        foreach (var node in document.DocumentNode.SelectNodes("//*[@style]")?.ToArray() ?? [])
            node.SetAttributeValue("style", await Css(HtmlEntity.DeEntitize(node.GetAttributeValue("style", "")), documentUri, []));
        foreach (var node in document.DocumentNode.SelectNodes("//img|//source|//script|//video|//audio|//*[local-name()='image']")?.ToArray() ?? [])
        {
            foreach (var attribute in new[] { "src", "poster", "href", "xlink:href" })
                if (node.Attributes[attribute]?.Value is { } value && await Data(value, documentUri) is { } data) node.SetAttributeValue(attribute, data);
            if (node.Attributes["srcset"]?.Value is { } srcset)
            {
                var entries = new List<string>();
                foreach (var (url, descriptor) in SourceSet(srcset))
                    entries.Add((await Data(url, documentUri) ?? url) + (descriptor.Length > 0 ? " " + descriptor : ""));
                node.SetAttributeValue("srcset", string.Join(", ", entries));
            }
        }
        return document.DocumentNode.OuterHtml;
    }

    /// <summary>
    /// Loopback, link-local and unspecified addresses. No legitimate asset lives there, while the server's own
    /// services and cloud metadata endpoints (169.254.169.254) do.
    /// </summary>
    public static bool IsRefusedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;
        return address.AddressFamily == AddressFamily.InterNetwork ? address.GetAddressBytes() is [169, 254, ..] : address.IsIPv6LinkLocal;
    }

    /// <summary>
    /// The asset client's handler: no redirects, cookies or credentials, and no connection to a refused address, which
    /// a host name that resolves to one would otherwise reach. Through a proxy, the proxy decides where requests go.
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        Credentials = null,
        ConnectCallback = async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = await Dns.GetHostAddressesAsync(host, ct);
            if (string.Equals(host.Trim('[', ']'), context.InitialRequestMessage.RequestUri?.IdnHost.Trim('[', ']'), StringComparison.OrdinalIgnoreCase))
                addresses = addresses.Where(address => !IsRefusedAddress(address)).ToArray();
            if (addresses.Length == 0) throw new HttpRequestException($"{host} has no address that paper report assets may be fetched from.");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    private static bool IsRefusedHost(Uri uri) => uri.IsLoopback || uri.IdnHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
        || uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 && IPAddress.TryParse(uri.DnsSafeHost.Trim('[', ']'), out var address) && IsRefusedAddress(address);

    private static IEnumerable<(string Url, string Descriptor)> SourceSet(string value)
    {
        var i = 0;
        while (i < value.Length)
        {
            while (i < value.Length && (char.IsWhiteSpace(value[i]) || value[i] == ',')) i++;
            var start = i;
            while (i < value.Length && !char.IsWhiteSpace(value[i])) i++;
            var url = value[start..i];
            if (url.Length == 0) break;
            if (url.EndsWith(',')) { yield return (url.TrimEnd(','), ""); continue; }
            start = i;
            while (i < value.Length && value[i] != ',') i++;
            yield return (url, value[start..i].Trim());
        }
    }

    private static async Task<string> ReplaceAsync(Regex pattern, string input, Func<Match, Task<string>> replace)
    {
        var text = new StringBuilder();
        var start = 0;
        foreach (Match match in pattern.Matches(input))
        {
            text.Append(input, start, match.Index - start).Append(await replace(match));
            start = match.Index + match.Length;
        }
        return text.Append(input, start, input.Length - start).ToString();
    }

    [GeneratedRegex("""@import\s+(?:url\(\s*(?:"(?<url>[^"]+)"|'(?<url>[^']+)'|(?<url>[^)\s]+))\s*\)|"(?<url>[^"]+)"|'(?<url>[^']+)')(?<media>[^;]*);""", RegexOptions.IgnoreCase)]
    private static partial Regex Imports();
    [GeneratedRegex("""url\(\s*(?:"(?<url>[^"]*)"|'(?<url>[^']*)'|(?<url>[^)]*?))\s*\)""", RegexOptions.IgnoreCase)]
    private static partial Regex Urls();
}
