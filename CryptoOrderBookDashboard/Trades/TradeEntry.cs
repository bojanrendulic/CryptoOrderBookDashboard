namespace CryptoOrderBookDashboard.Trades;

// Id is Binance's aggregate trade id - monotonically increasing per symbol and market,
// which is what lets the store merge REST backfills with the live stream without duplicates.
public readonly record struct TradeEntry(long Id, decimal Price, decimal Quantity, bool BuyerIsMaker, DateTime TradeTimeUtc);
