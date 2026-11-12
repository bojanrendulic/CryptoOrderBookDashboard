namespace CryptoOrderBookDashboard.OrderBooks.Configuration;

public sealed class TrackedSymbolOptions
{
    public string Symbol { get; set; } = string.Empty;

    public List<MarketType> Markets { get; set; } = [];
}
