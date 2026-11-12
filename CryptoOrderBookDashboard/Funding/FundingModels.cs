namespace CryptoOrderBookDashboard.Funding;

// Live mark price stream data for one perpetual. EstimatedRate is the rate the *next*
// settlement will charge if conditions stay as they are - it moves until settlement.
public sealed record FundingTicker(
    string Symbol,
    decimal MarkPrice,
    decimal IndexPrice,
    decimal EstimatedRate,
    DateTime NextFundingTimeUtc);

public sealed record FundingSettlementRecord(string Symbol, DateTime TimeUtc, decimal Rate);

// Aggregates of the settled (actually charged) rates over the history window.
public sealed record FundingHistorySummary(
    decimal Cumulative,
    IReadOnlyList<decimal> DailySums,
    DateTime? LastSettlementUtc,
    decimal? LastSettledRate);

// Positioning data is only available per symbol from Binance, so it is fetched for tracked
// symbols on a timer and for anything else on demand.
public sealed record PositioningSnapshot(
    decimal OpenInterest,
    decimal? OpenInterestChange24hPct,
    decimal? GlobalLongAccountPct,
    decimal? GlobalLongShortRatio,
    decimal? TopTraderLongShortPositionRatio,
    DateTime FetchedUtc);

public enum FundingSentiment
{
    StrongBearish,
    Bearish,
    Neutral,
    Bullish,
    StrongBullish,
}

public sealed record FundingRow(
    string Symbol,
    string DisplayName,
    bool IsTracked,
    decimal MarkPrice,
    decimal PremiumPct,
    decimal EstimatedRate,
    int IntervalHours,
    decimal Rate8h,
    decimal AnnualizedPct,
    DateTime NextFundingTimeUtc,
    FundingSentiment Sentiment,
    FundingHistorySummary? History,
    PositioningSnapshot? Positioning)
{
    public decimal? OpenInterestUsd => Positioning is null ? null : Positioning.OpenInterest * MarkPrice;

    // How far the current rate sits from the neutral baseline, used for the "most extreme" sort.
    public decimal DistanceFromNeutral => Math.Abs(Rate8h - FundingMath.NeutralRatePer8h);
}
