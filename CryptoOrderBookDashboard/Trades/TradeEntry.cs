namespace CryptoOrderBookDashboard.Trades;

public readonly record struct TradeEntry(decimal Price, decimal Quantity, bool BuyerIsMaker, DateTime TradeTimeUtc);
