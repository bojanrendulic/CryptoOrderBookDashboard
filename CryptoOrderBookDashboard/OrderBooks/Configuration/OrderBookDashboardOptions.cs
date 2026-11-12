namespace CryptoOrderBookDashboard.OrderBooks.Configuration;

public sealed class OrderBookDashboardOptions
{
    public const string SectionName = "OrderBookDashboard";

    public int DepthLevels { get; set; } = 20;

    public int UpdateThrottleMilliseconds { get; set; } = 250;

    // Server-wide cap on concurrently running historical trade searches. All users share the
    // server's Binance IP rate limit (also used by the live feeds), so extra searches queue.
    public int MaxConcurrentHistoricalSearches { get; set; } = 2;

    public List<TrackedSymbolOptions> Symbols { get; set; } = [];

    public TrackedSymbolOptions? FindSymbol(string? symbol) =>
        Symbols.FirstOrDefault(s => string.Equals(s.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
}
