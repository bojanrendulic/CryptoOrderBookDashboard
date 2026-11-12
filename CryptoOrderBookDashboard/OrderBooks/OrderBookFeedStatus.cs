using CryptoExchange.Net.Objects;

namespace CryptoOrderBookDashboard.OrderBooks;

public enum OrderBookFeedStatus
{
    Connecting,
    Syncing,
    Synced,
    Reconnecting,
    Disconnected
}

public static class OrderBookFeedStatusMapper
{
    public static OrderBookFeedStatus FromLibraryStatus(OrderBookStatus status) => status switch
    {
        OrderBookStatus.Connecting => OrderBookFeedStatus.Connecting,
        OrderBookStatus.Syncing => OrderBookFeedStatus.Syncing,
        OrderBookStatus.Synced => OrderBookFeedStatus.Synced,
        OrderBookStatus.Reconnecting => OrderBookFeedStatus.Reconnecting,
        _ => OrderBookFeedStatus.Disconnected
    };
}
