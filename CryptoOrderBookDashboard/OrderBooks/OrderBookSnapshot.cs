namespace CryptoOrderBookDashboard.OrderBooks;

// BestBid/BestAsk and Depth always come from the full, unfiltered and ungrouped book, so spread,
// mid and depth stay correct whatever filter or grouping is applied to the rows.
// GroupStep is the price bucket the rows were grouped by (null = not grouped).
public sealed record OrderBookSnapshot(
    IReadOnlyList<PriceLevel> Bids,
    IReadOnlyList<PriceLevel> Asks,
    decimal? BestBid,
    decimal? BestAsk,
    decimal? GroupStep,
    DepthSummary? Depth,
    OrderBookFeedStatus Status,
    DateTime LastUpdateUtc)
{
    public static readonly OrderBookSnapshot Empty = new(
        Array.Empty<PriceLevel>(),
        Array.Empty<PriceLevel>(),
        null,
        null,
        null,
        null,
        OrderBookFeedStatus.Connecting,
        DateTime.MinValue);
}
