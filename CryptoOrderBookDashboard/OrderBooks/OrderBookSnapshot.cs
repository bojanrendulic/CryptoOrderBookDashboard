namespace CryptoOrderBookDashboard.OrderBooks;

public sealed record OrderBookSnapshot(
    IReadOnlyList<PriceLevel> Bids,
    IReadOnlyList<PriceLevel> Asks,
    OrderBookFeedStatus Status,
    DateTime LastUpdateUtc)
{
    public static readonly OrderBookSnapshot Empty = new(
        Array.Empty<PriceLevel>(),
        Array.Empty<PriceLevel>(),
        OrderBookFeedStatus.Connecting,
        DateTime.MinValue);
}
