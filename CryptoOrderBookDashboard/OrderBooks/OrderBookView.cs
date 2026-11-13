namespace CryptoOrderBookDashboard.OrderBooks;

// Price grouping as a percentage of the mid price, so one preset works for BTC and DOGE alike.
// The actual bucket is rounded to a 1/2/5 step (0.1% of 68,750 -> 50).
public sealed record PriceGrouping(string Key, string Label, decimal? Percent)
{
    public static readonly IReadOnlyList<PriceGrouping> Presets =
    [
        new("none", "None", null),
        new("0.01", "0.01%", 0.01m),
        new("0.05", "0.05%", 0.05m),
        new("0.1", "0.1%", 0.1m),
        new("0.5", "0.5%", 0.5m),
    ];

    public static PriceGrouping Default => Presets[0];

    public static PriceGrouping FromKey(string? key) =>
        Presets.FirstOrDefault(g => g.Key == key) ?? Default;

    public static decimal NiceStep(decimal raw)
    {
        if (raw <= 0)
        {
            return 0;
        }

        var exponent = (int)Math.Floor(Math.Log10((double)raw));
        var magnitude = Pow10(exponent);
        var fraction = raw / magnitude;
        var nice = fraction switch
        {
            < 1.5m => 1m,
            < 3.5m => 2m,
            < 7.5m => 5m,
            _ => 10m,
        };
        return nice * magnitude;
    }

    private static decimal Pow10(int exponent)
    {
        var result = 1m;
        for (var i = 0; i < Math.Abs(exponent); i++)
        {
            result *= 10m;
        }

        return exponent >= 0 ? result : 1m / result;
    }
}

// Band around the mid price over which bid and ask liquidity is summed.
public sealed record DepthBand(string Key, decimal Percent)
{
    public static readonly IReadOnlyList<DepthBand> Presets =
    [
        new("0.5", 0.5m),
        new("1", 1m),
        new("2", 2m),
        new("5", 5m),
    ];

    public static DepthBand Default => Presets[1];

    public string Label => $"±{Percent:0.#}%";

    public static DepthBand FromKey(string? key) =>
        Presets.FirstOrDefault(d => d.Key == key) ?? Default;
}

// USD liquidity within ±Percent of the mid price. Complete is false when the locally held book
// ends inside the band (Binance only snapshots a limited number of levels), so the sum is a
// lower bound.
public sealed record DepthSummary(decimal Percent, decimal BidsUsd, decimal AsksUsd, bool Complete)
{
    public decimal BidShare => BidsUsd + AsksUsd > 0 ? BidsUsd / (BidsUsd + AsksUsd) : 0.5m;
}
