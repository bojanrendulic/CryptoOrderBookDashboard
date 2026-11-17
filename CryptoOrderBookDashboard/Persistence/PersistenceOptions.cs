namespace CryptoOrderBookDashboard.Persistence;

public sealed class PersistenceOptions
{
    public const string SectionName = "Persistence";

    // Relative paths are resolved against the content root. The file is created on first start.
    public string DatabasePath { get; set; } = "data/dashboard.db";

    // Rows older than this are deleted once a day; 0 keeps everything forever.
    public int RetentionDays { get; set; } = 365;

    // Pending writes are committed in one transaction once this many are queued or
    // FlushIntervalMilliseconds has passed, whichever comes first.
    public int BatchSize { get; set; } = 500;

    public int FlushIntervalMilliseconds { get; set; } = 1000;

    // Bound on writes waiting for the writer. When full, new writes are dropped and counted
    // instead of blocking the exchange feeds.
    public int QueueCapacity { get; set; } = 50_000;
}
