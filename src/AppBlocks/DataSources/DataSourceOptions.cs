namespace AppBlocks.DataSources;

/// <summary>The limits of the data source engine, from the <c>AppBlocks:DataSources</c> configuration section; each default is what the engine always did.</summary>
public sealed class DataSourceOptions
{
    public const string Section = "AppBlocks:DataSources";
    /// <summary>The command timeout when nothing configures one, as most providers default to.</summary>
    public const int DefaultCommandTimeoutSeconds = 30;

    /// <summary>How long a statement of the engine may run before the provider gives up on it; 0 waits without limit.</summary>
    public int CommandTimeoutSeconds { get; set; } = DefaultCommandTimeoutSeconds;
    /// <summary>The most changes one tabular or detail batch takes.</summary>
    public int MaxChanges { get; set; } = 200;
    /// <summary>How long a create's receipt is kept, so a retried request returns the record it created instead of inserting it twice.</summary>
    public int ReceiptLifetimeMinutes { get; set; } = 15;
    /// <summary>How many receipts the in-memory store holds before it drops expired ones.</summary>
    public int ReceiptCapacity { get; set; } = 10_000;

    /// <summary>Whether every value is in range: a timeout of 0 or more, and the other limits at least 1.</summary>
    internal bool InRange() => CommandTimeoutSeconds >= 0 && MaxChanges >= 1 && ReceiptLifetimeMinutes >= 1 && ReceiptCapacity >= 1;
}

/// <summary>A successful create, remembered by its submission key: the record's key and the hash of the values it was made with.</summary>
public sealed record DataSourceReceipt(string Key, string Hash);

/// <summary>
/// Where create receipts are kept and how creates with the same submission key wait for each other. The default keeps both in this process,
/// which is enough for one web server; a load-balanced site registers a store all its servers share, so a retry that reaches another
/// server finds the receipt. Register it with <c>AddSingleton&lt;IDataSourceReceiptStore, YourStore&gt;()</c>; keep each receipt at least
/// <see cref="DataSourceOptions.ReceiptLifetimeMinutes"/>.
/// </summary>
public interface IDataSourceReceiptStore
{
    /// <summary>Waits until no other create holds any of <paramref name="submissions"/>, and holds them all until the result is disposed.</summary>
    Task<IAsyncDisposable> EnterAsync(IReadOnlyCollection<string> submissions, CancellationToken ct);

    /// <summary>The receipt of a submission that has not expired; null when there is none.</summary>
    ValueTask<DataSourceReceipt?> FindAsync(string submission, CancellationToken ct);

    /// <summary>Remembers a successful create, called while the submission is held.</summary>
    ValueTask AddAsync(string submission, DataSourceReceipt receipt, CancellationToken ct);
}
