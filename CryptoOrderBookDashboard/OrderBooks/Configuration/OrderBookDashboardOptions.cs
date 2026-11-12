namespace CryptoOrderBookDashboard.OrderBooks.Configuration;

public sealed class OrderBookDashboardOptions
{
    public const string SectionName = "OrderBookDashboard";

    public int DepthLevels { get; set; } = 20;

    public int UpdateThrottleMilliseconds { get; set; } = 250;

    public List<TrackedSymbolOptions> Symbols { get; set; } = [];
}
