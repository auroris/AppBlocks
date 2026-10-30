namespace AppBlocks.Documents;

/// <summary>
/// Optional server-side document gateway, registered by AppBlocks.Documents. Callers authorize access
/// to their document and destination before submitting. Printer submissions require an explicit user
/// action (a protected POST in a web application); never invoke them from a GET or retry automatically.
/// </summary>
public interface IDocumentOutputService
{
    IReadOnlyList<DocumentDestination> GetDestinations();
    Task<DocumentOutputResult> SubmitAsync(Guid configurationId, DocumentRequest document, CancellationToken cancellationToken = default);
}

public enum DocumentOutputKind { Pdf, Printer, Html }
public sealed record DocumentDestination(Guid Id, string Name, DocumentOutputKind Output, string? Printer);

/// <summary>Complete, self-contained HTML. Credentials and Pressroom URLs stay inside Documents.</summary>
public sealed record DocumentRequest(string Html, string FileName = "document.pdf", DocumentRenderOptions? Options = null);

public sealed record DocumentRenderOptions
{
    public DocumentPageSize? PageSize { get; init; }
    public bool Landscape { get; init; }
    public DocumentMargins? Margins { get; init; }
    public bool PreferCssPageSize { get; init; }
    public bool PrintBackground { get; init; } = true;
    public double Scale { get; init; } = 1;
    public bool DisplayHeaderFooter { get; init; } = true;
    public string? HeaderTemplate { get; init; } = "<div style=\"font-size:9px;width:100%;text-align:center\"><span class=\"title\"></span></div>";
    public string? FooterTemplate { get; init; } = "<div style=\"font-size:9px;width:100%;text-align:center\">Page <span class=\"pageNumber\"></span> of <span class=\"totalPages\"></span></div>";
    public bool Tagged { get; init; } = true;
    public bool Outline { get; init; } = true;
    public string Media { get; init; } = "print";
    public bool JavaScript { get; init; } = true;
    public string Locale { get; init; } = "en-CA";
    public string TimeZone { get; init; } = "America/Edmonton";
    public DocumentReadiness? Readiness { get; init; }
    public int? TimeoutMs { get; init; }
}
public sealed record DocumentPageSize(string? Name = null, string? Width = null, string? Height = null);
public sealed record DocumentMargins(string Top = "20mm", string Right = "15mm", string Bottom = "20mm", string Left = "15mm");
public sealed record DocumentReadiness(string? Selector = null, string? Function = null, int DelayMs = 0, int TimeoutMs = 5000);

/// <summary>Keep the result alive while consuming a file stream; then dispose it, including on cancellation.</summary>
public abstract class DocumentOutputResult : IDisposable, IAsyncDisposable
{
    public abstract DocumentOutputKind Output { get; }
    public abstract void Dispose();
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}

public sealed class DocumentFileResult(Stream content, string contentType, string fileName, bool isHtmlFallback,
    IDisposable? owner = null) : DocumentOutputResult
{
    public Stream Content { get; } = content;
    public string ContentType { get; } = contentType;
    public string FileName { get; } = fileName;
    public bool IsHtmlFallback { get; } = isHtmlFallback;
    public override DocumentOutputKind Output => IsHtmlFallback ? DocumentOutputKind.Html : DocumentOutputKind.Pdf;
    private bool disposed;
    public override void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (owner is not null) owner.Dispose(); else Content.Dispose();
    }
}

/// <summary>A submission receipt. It does not confirm physical printing or identify a Windows spooler job.</summary>
public sealed class DocumentPrintResult(string jobId, string printer, int pageCount) : DocumentOutputResult
{
    public string JobId { get; } = jobId;
    public string Printer { get; } = printer;
    public int PageCount { get; } = pageCount;
    public override DocumentOutputKind Output => DocumentOutputKind.Printer;
    public override void Dispose() { }
}

public sealed class DocumentPrintException(string message, bool mayHavePrinted, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>Check the printer/queue before offering another submission. Documents never retries printing.</summary>
    public bool MayHavePrinted { get; } = mayHavePrinted;
}
