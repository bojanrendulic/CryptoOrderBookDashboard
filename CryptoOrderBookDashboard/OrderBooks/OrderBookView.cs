using System.Globalization;

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

// Band around the mid price over which bid and ask liquidity is summed. Percent null is the
// "Rows" mode: the depth bar and chart follow the visible (filtered, grouped) rows instead.
public sealed record DepthBand(string Key, decimal? Percent)
{
    public static readonly IReadOnlyList<DepthBand> Presets =
    [
        new("0.5", 0.5m),
        new("1", 1m),
        new("2", 2m),
        new("5", 5m),
        new("rows", null),
    ];

    public static DepthBand Default => Presets[1];

    // Used in Rows mode when there is no size filter: the unfiltered rows are only the top few
    // levels, far too narrow for a meaningful chart.
    public const decimal RowsFallbackPercent = 1m;

    // Rows mode never draws further than this from mid, so one absurd far-away order can't
    // squeeze everything near the price into a few pixels.
    public const decimal RowsMaxPercent = 50m;

    public string Label => Percent is { } percent ? FormatLabel(percent) : "Rows";

    // Invariant culture so the label reads "±0.5%" like every other number on the page.
    public static string FormatLabel(decimal percent) =>
        "±" + percent.ToString("0.#", CultureInfo.InvariantCulture) + "%";

    public static DepthBand FromKey(string? key) =>
        Presets.FirstOrDefault(d => d.Key == key) ?? Default;
}

// Bid and ask USD liquidity behind the depth bar. Complete is false when the locally held book
// ends inside the band (Binance only snapshots a limited number of levels), so the sum is a
// lower bound. Label describes what was summed ("±1%", "Rows").
public sealed record DepthSummary(string Label, decimal BidsUsd, decimal AsksUsd, bool Complete)
{
    public decimal BidShare => BidsUsd + AsksUsd > 0 ? BidsUsd / (BidsUsd + AsksUsd) : 0.5m;
}

// Cumulative USD depth for the depth chart on a linear price axis from Low to High (Mid need not
// be centred). Each side is sampled at Steps fixed price steps from mid outwards: Bids[i] is the
// cumulative notional from mid down to mid - (i + 1) / Steps * (Mid - Low), Asks likewise up to
// High. A fixed number of points keeps the chart (and its SVG diff) the same size whatever the
// book's tick size or the range. BidSteps/AskSteps count the leading points the local data
// reaches; points beyond them are unknown and not drawn. BidReliable/AskReliable count the leading
// points inside the snapshot boundary: past them the curve is drawn but only learned from live
// updates, so it may be too low.
public sealed record DepthCurve(
    decimal Mid,
    decimal Low,
    decimal High,
    IReadOnlyList<decimal> Bids,
    IReadOnlyList<decimal> Asks,
    int BidSteps,
    int AskSteps,
    string Label)
{
    public int BidReliable { get; init; } = Steps;

    public int AskReliable { get; init; } = Steps;

    public SnapshotBoundary? Boundary { get; init; }

    public bool Reliable => BidReliable >= BidSteps && AskReliable >= AskSteps;

    public const int Steps = 120;

    // Rows mode: visible rows left out because they are beyond ±RowsMaxPercent of mid.
    public int RowsBeyondCap { get; init; }

    public bool Complete => BidSteps == Steps && AskSteps == Steps;

    public decimal MaxUsd => Math.Max(
        BidSteps > 0 ? Bids[BidSteps - 1] : 0m,
        AskSteps > 0 ? Asks[AskSteps - 1] : 0m);

    // levels must be best-first (bids descending, asks ascending). When `bookEnds` is true the
    // last level marks the end of known data, so points past it are left uncovered; for an
    // explicit row list every point is covered.
    public static DepthCurve Build(
        decimal mid,
        decimal low,
        decimal high,
        IReadOnlyList<PriceLevel> bids,
        IReadOnlyList<PriceLevel> asks,
        bool bookEnds,
        string label,
        SnapshotBoundary? boundary)
    {
        var bidStep = (mid - low) / Steps;
        var askStep = (high - mid) / Steps;
        var (bidSums, bidSteps) = Accumulate(bids, i => mid - (i + 1) * bidStep, (price, edge) => price >= edge, bookEnds);
        var (askSums, askSteps) = Accumulate(asks, i => mid + (i + 1) * askStep, (price, edge) => price <= edge, bookEnds);

        // Without a captured boundary (still syncing) nothing can be vouched for.
        var bidReliable = 0;
        var askReliable = 0;
        while (boundary is not null && bidReliable < Steps && boundary.CoversBid(mid - (bidReliable + 1) * bidStep))
        {
            bidReliable++;
        }

        while (boundary is not null && askReliable < Steps && boundary.CoversAsk(mid + (askReliable + 1) * askStep))
        {
            askReliable++;
        }

        return new DepthCurve(mid, low, high, bidSums, askSums, bidSteps, askSteps, label)
        {
            BidReliable = bidReliable,
            AskReliable = askReliable,
            Boundary = boundary,
        };
    }

    private static (decimal[] Cumulative, int Covered) Accumulate(
        IReadOnlyList<PriceLevel> levels,
        Func<int, decimal> edgeAt,
        Func<decimal, decimal, bool> isInside,
        bool bookEnds)
    {
        var cumulative = new decimal[Steps];
        if (levels.Count == 0)
        {
            return (cumulative, bookEnds ? 0 : Steps);
        }

        var lastPrice = levels[^1].Price;
        var covered = bookEnds ? 0 : Steps;
        var sum = 0m;
        var index = 0;

        for (var i = 0; i < Steps; i++)
        {
            var edge = edgeAt(i);
            while (index < levels.Count && isInside(levels[index].Price, edge))
            {
                sum += levels[index].Price * levels[index].Quantity;
                index++;
            }

            cumulative[i] = sum;

            // A point is only covered if the book reaches at least to its edge price; past the
            // book's last level the sum is unknown.
            if (bookEnds && !isInside(lastPrice, edge))
            {
                covered = i + 1;
            }
        }

        return (cumulative, covered);
    }
}

// Price range of the depth chart in Rows mode, derived from the visible rows. Expands at once
// when a row appears further out, but only shrinks after the narrower range has held for
// ShrinkDelay, so a far wall flickering in and out doesn't make the axis jump every second.
public sealed class RowsRange
{
    public static readonly TimeSpan ShrinkDelay = TimeSpan.FromSeconds(10);

    private decimal _low;
    private decimal _high;
    private long? _shrinkSinceTicks;

    public void Reset()
    {
        _low = 0;
        _high = 0;
        _shrinkSinceTicks = null;
    }

    public (decimal Low, decimal High) Update(decimal mid, IReadOnlyList<PriceLevel> bids, IReadOnlyList<PriceLevel> asks)
    {
        var (low, high) = Target(mid, bids, asks);
        var now = Environment.TickCount64;

        if (_high <= _low)
        {
            (_low, _high) = (low, high);
            _shrinkSinceTicks = null;
        }
        else if (low < _low || high > _high)
        {
            // A row further out (the target always contains mid, so this also covers mid moving
            // past an edge): widen at once, keeping the other side as it is.
            _low = Math.Min(_low, low);
            _high = Math.Max(_high, high);
            _shrinkSinceTicks = null;
        }
        else if (low > _low || high < _high)
        {
            _shrinkSinceTicks ??= now;
            if (now - _shrinkSinceTicks.Value >= (long)ShrinkDelay.TotalMilliseconds)
            {
                (_low, _high) = (low, high);
                _shrinkSinceTicks = null;
            }
        }
        else
        {
            _shrinkSinceTicks = null;
        }

        return (_low, _high);
    }

    // Outermost visible row on each side, capped at ±RowsMaxPercent and padded slightly so the
    // last step isn't drawn on the very edge. Never narrower than 0.1% per side.
    private static (decimal Low, decimal High) Target(decimal mid, IReadOnlyList<PriceLevel> bids, IReadOnlyList<PriceLevel> asks)
    {
        var floor = mid * (1 - DepthBand.RowsMaxPercent / 100m);
        var ceiling = mid * (1 + DepthBand.RowsMaxPercent / 100m);
        var minSpan = mid * 0.001m;

        var bidSpan = bids.Count > 0 ? mid - Math.Max(bids[^1].Price, floor) : 0m;
        var askSpan = asks.Count > 0 ? Math.Min(asks[^1].Price, ceiling) - mid : 0m;

        return (mid - Math.Max(bidSpan * 1.03m, minSpan), mid + Math.Max(askSpan * 1.03m, minSpan));
    }
}

// Price range covered by the REST snapshot the local book was last synced from (Binance sends
// at most 5000 levels per side on spot and 1000 on futures, often only ±0.2% of price on
// futures). Inside it the book is complete; outside it a level is only known once it has changed
// since SyncedUtc, so resting orders that haven't moved are missing.
public sealed record SnapshotBoundary(decimal LowestBid, decimal HighestAsk, DateTime SyncedUtc)
{
    public bool CoversBid(decimal price) => price >= LowestBid;

    public bool CoversAsk(decimal price) => price <= HighestAsk;

    // How far below / above mid the snapshot reaches, in percent (0 once price has moved past it).
    public decimal BidReachPercent(decimal mid) => mid > 0 ? Math.Max(0, (mid - LowestBid) / mid * 100m) : 0;

    public decimal AskReachPercent(decimal mid) => mid > 0 ? Math.Max(0, (HighestAsk - mid) / mid * 100m) : 0;
}
