using AppBlocks.Documents;
using Microsoft.Extensions.Caching.Memory;
using InteractiveReport.AspNetCore;
using InteractiveReport.Core.Model;
using System.Security.Cryptography;
using System.Text.Json;

namespace AppBlocks.InteractiveReports.Reports;

// Bounded, short-lived snapshots. A consumed token remains consumed until expiration, including
// failed or cancelled printer requests: a network error cannot prove that paper was not printed.
internal sealed class ReportPrintPreviews : IDisposable
{
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 32 * 1024 * 1024 });
    internal sealed class Preview(string owner, DocumentDestination destination, DocumentRequest document, ReportResult result)
    {
        public string Owner { get; } = owner;
        public DocumentDestination Destination { get; } = destination;
        public DocumentRequest Document { get; } = document;
        public ReportState State { get; } = result.Document!;
        public string Fingerprint { get; } = DataFingerprint(result);
        private int consumed;
        public bool Consume() => Interlocked.CompareExchange(ref consumed, 1, 0) == 0;
    }

    public string Add(Preview preview)
    {
        var token = Guid.NewGuid().ToString("N");
        cache.Set(token, preview, new MemoryCacheEntryOptions {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5), Size = preview.Document.Html.Length * 2L + 1024,
        });
        return token;
    }

    public Preview? Find(string token, string owner) => cache.TryGetValue<Preview>(token, out var preview)
        && preview?.Owner == owner ? preview : null;
    public static string DataFingerprint(ReportResult result) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
        result.Rows, result.Columns, result.ConfiguredLabels, result.Aggregates, result.BreakTotals, result.BreakContinues, result.TotalRows, result.Page,
    }, IrJson.Options)));
    public void Dispose() => cache.Dispose();
}
