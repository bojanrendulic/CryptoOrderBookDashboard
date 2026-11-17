namespace CryptoOrderBookDashboard.Liquidations;

public sealed class LiquidationOptions
{
    public const string SectionName = "Liquidations";

    // Individual liquidations at or above this USD value are stored in the database (all of
    // them still count towards the per-minute totals, which are always stored).
    public decimal MinPersistUsd { get; set; } = 200_000m;
}

// Which position was force-closed. Binance reports the side of the liquidation *order*: a SELL
// closes a long, a BUY closes a short.
public enum LiquidatedSide
{
    Long,
    Short,
}

// One liquidation as received from !forceOrder@arr. UsdValue is average fill price x filled
// quantity. Restored marks events loaded from the database after a restart: only the large ones
// are stored, so the tape is thinner before the restart.
public sealed record LiquidationEvent(
    string Symbol,
    LiquidatedSide Side,
    decimal Price,
    decimal Quantity,
    decimal UsdValue,
    DateTime TimeUtc,
    bool Restored = false);

public sealed record SideTotals(decimal LongUsd, decimal ShortUsd, int LongCount, int ShortCount)
{
    public static readonly SideTotals Zero = new(0, 0, 0, 0);

    public decimal TotalUsd => LongUsd + ShortUsd;

    public decimal LongShare => TotalUsd > 0 ? LongUsd / TotalUsd : 0.5m;
}

// Totals over one window. Covered is how much of the window the feed was actually receiving
// (less than Window after a restart or a disconnect).
public sealed record LiquidationWindow(TimeSpan Window, SideTotals Totals, LiquidationEvent? Largest, TimeSpan Covered)
{
    public bool FullyCovered => Covered >= Window - TimeSpan.FromMinutes(1);
}

public sealed record SymbolLiquidations(string Symbol, decimal LongUsd, decimal ShortUsd, int Count)
{
    public decimal TotalUsd => LongUsd + ShortUsd;
}

// Seconds in the last hour in which a symbol had a liquidation, and how many of those directly
// followed another such second. Binance sends at most one liquidation per symbol per second, so
// a run of consecutive seconds almost certainly hid more liquidations than we saw.
public sealed record SamplingPressure(string Symbol, int ActiveSeconds, int SaturatedSeconds);

public sealed record FeedGap(DateTime StartUtc, DateTime EndUtc)
{
    public TimeSpan Duration => EndUtc - StartUtc;
}

public sealed record LiquidationSummary(
    LiquidationWindow Hour,
    LiquidationWindow Day,
    IReadOnlyList<SymbolLiquidations> TopSymbols,
    IReadOnlyList<SamplingPressure> Pressure,
    DateTime? CollectingSinceUtc,
    IReadOnlyList<FeedGap> Gaps,
    bool Connected)
{
    public static readonly LiquidationSummary Empty = new(
        new LiquidationWindow(TimeSpan.FromHours(1), SideTotals.Zero, null, TimeSpan.Zero),
        new LiquidationWindow(TimeSpan.FromHours(24), SideTotals.Zero, null, TimeSpan.Zero),
        [],
        [],
        null,
        [],
        false);
}

// A stored per-minute bucket (all liquidations, not only large ones).
public sealed record LiquidationMinute(string Symbol, DateTime MinuteUtc, LiquidatedSide Side, decimal Usd, int Count);

public sealed record FeedSession(DateTime StartUtc, DateTime EndUtc);
