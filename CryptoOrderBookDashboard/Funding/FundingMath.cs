namespace CryptoOrderBookDashboard.Funding;

public static class FundingMath
{
    // USDT-M funding includes a fixed interest component of 0.03%/day, i.e. 0.01% per 8h.
    // With a zero premium that is what gets charged, so 0.01%/8h - not 0% - is "neutral".
    public const decimal NeutralRatePer8h = 0.0001m;

    public const decimal NeutralDailyRate = NeutralRatePer8h * 3;

    public const int DefaultIntervalHours = 8;

    // Symbols settle every 8h, 4h or 1h, so rates are only comparable once normalized.
    public static decimal To8h(decimal rate, int intervalHours) => rate * 8m / intervalHours;

    public static decimal AnnualizedPct(decimal rate, int intervalHours) => rate * (24m / intervalHours) * 365m * 100m;

    public static FundingSentiment Classify(decimal rate8h) => rate8h switch
    {
        <= -0.0003m => FundingSentiment.StrongBearish,
        < 0.00005m => FundingSentiment.Bearish,
        <= 0.000125m => FundingSentiment.Neutral,
        <= 0.0005m => FundingSentiment.Bullish,
        _ => FundingSentiment.StrongBullish,
    };

    public static bool IsExtreme(FundingSentiment sentiment) =>
        sentiment is FundingSentiment.StrongBearish or FundingSentiment.StrongBullish;

    public static string Label(FundingSentiment sentiment) => sentiment switch
    {
        FundingSentiment.StrongBearish => "Shorts crowded",
        FundingSentiment.Bearish => "Bearish",
        FundingSentiment.Neutral => "Neutral",
        FundingSentiment.Bullish => "Bullish",
        FundingSentiment.StrongBullish => "Longs crowded",
        _ => sentiment.ToString(),
    };

    // Quarterly contracts (e.g. BTCUSDT_261225) carry no funding, and only USDT-margined
    // perpetuals are shown.
    public static bool IsUsdtPerpetual(string symbol) =>
        symbol.EndsWith("USDT", StringComparison.Ordinal) && !symbol.Contains('_');
}
