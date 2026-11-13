namespace CryptoOrderBookDashboard.OrderBooks;

// BestBid/BestAsk always come from the full, unfiltered book, so spread and mid stay correct
// when a size filter hides the levels closest to the market.
public sealed record OrderBookSnapshot(
    IReadOnlyList<PriceLevel> Bids,
    IReadOnlyList<PriceLevel> Asks,
    decimal? BestBid,
    decimal? BestAsk,
    OrderBookFeedStatus Status,
    DateTime LastUpdateUtc)
{
    public static readonly OrderBookSnapshot Empty = new(
        Array.Empty<PriceLevel>(),
        Array.Empty<PriceLevel>(),
        null,
        null,
        OrderBookFeedStatus.Connecting,
        DateTime.MinValue);
}
