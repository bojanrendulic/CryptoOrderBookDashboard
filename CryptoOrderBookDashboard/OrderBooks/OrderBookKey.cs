namespace CryptoOrderBookDashboard.OrderBooks;

public readonly record struct OrderBookKey(MarketType Market, string Symbol)
{
    public override string ToString() => $"{Market}:{Symbol}";
}
