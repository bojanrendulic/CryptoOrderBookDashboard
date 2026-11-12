namespace CryptoOrderBookDashboard.OrderBooks.Configuration;

public sealed class TrackedSymbolOptions
{
    private static readonly string[] QuoteAssets = ["USDT", "USDC", "FDUSD"];

    public string Symbol { get; set; } = string.Empty;

    // Optional label for the symbol dropdown; defaults to the symbol without its quote asset.
    public string? DisplayName { get; set; }

    public List<MarketType> Markets { get; set; } = [];

    // Methods rather than properties so the configuration binder never tries to bind them.
    public List<MarketType> GetMarkets() =>
        Markets.Count > 0 ? Markets : [MarketType.Spot, MarketType.UsdFutures];

    public string GetDisplayName()
    {
        if (!string.IsNullOrWhiteSpace(DisplayName))
        {
            return DisplayName;
        }

        var quote = QuoteAssets.FirstOrDefault(q => Symbol.Length > q.Length && Symbol.EndsWith(q, StringComparison.OrdinalIgnoreCase));
        return quote is null ? Symbol : Symbol[..^quote.Length];
    }
}
