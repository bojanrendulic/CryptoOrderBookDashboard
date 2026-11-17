using CryptoOrderBookDashboard.Liquidations;

namespace CryptoOrderBookDashboard.Tests;

public sealed class LiquidationStoreTests
{
    private static LiquidationEvent Liq(string symbol, LiquidatedSide side, decimal usd, DateTime time) =>
        new(symbol, side, 100m, usd / 100m, usd, time);

    [Fact]
    public void Windows_split_long_and_short_totals_and_find_the_largest()
    {
        var now = DateTime.UtcNow;
        var store = new LiquidationStore();
        store.BeginSession(now.AddHours(-30));
        store.Add(Liq("BTCUSDT", LiquidatedSide.Long, 1_000_000m, now.AddMinutes(-10)));
        store.Add(Liq("ETHUSDT", LiquidatedSide.Short, 250_000m, now.AddMinutes(-5)));
        store.Add(Liq("SOLUSDT", LiquidatedSide.Long, 50_000m, now.AddHours(-3))); // in 24h, not in 1h

        var summary = store.GetSummary(now);

        Assert.Equal(1_000_000m, summary.Hour.Totals.LongUsd);
        Assert.Equal(250_000m, summary.Hour.Totals.ShortUsd);
        Assert.Equal(1_050_000m, summary.Day.Totals.LongUsd);
        Assert.Equal(3, summary.Day.Totals.LongCount + summary.Day.Totals.ShortCount);
        Assert.Equal("BTCUSDT", summary.Day.Largest?.Symbol);
        Assert.Equal("BTCUSDT", summary.TopSymbols[0].Symbol);
        Assert.True(summary.Day.FullyCovered);
        Assert.True(summary.Connected);
    }

    [Fact]
    public void Add_returns_absolute_minute_totals_on_top_of_restored_ones()
    {
        var minute = DateTime.UtcNow.AddMinutes(-2);
        minute = new DateTime(minute.Year, minute.Month, minute.Day, minute.Hour, minute.Minute, 0, DateTimeKind.Utc);
        var store = new LiquidationStore();

        // Same minute was partly collected before a restart.
        store.Restore([], [new LiquidationMinute("BTCUSDT", minute, LiquidatedSide.Long, 10_000m, 3)], []);
        var bucket = store.Add(Liq("BTCUSDT", LiquidatedSide.Long, 5_000m, minute.AddSeconds(30)));

        Assert.Equal(minute, bucket.MinuteUtc);
        Assert.Equal(15_000m, bucket.Usd);
        Assert.Equal(4, bucket.Count);
    }

    [Fact]
    public void Restored_events_are_flagged_and_listed_newest_first()
    {
        var now = DateTime.UtcNow;
        var store = new LiquidationStore();
        store.Restore([Liq("BTCUSDT", LiquidatedSide.Long, 300_000m, now.AddMinutes(-30))], [], []);
        store.Add(Liq("ETHUSDT", LiquidatedSide.Short, 20_000m, now.AddMinutes(-1)));

        var tape = store.GetTape(_ => true, 10);

        Assert.Equal(["ETHUSDT", "BTCUSDT"], tape.Select(e => e.Symbol));
        Assert.False(tape[0].Restored);
        Assert.True(tape[1].Restored);
    }

    [Fact]
    public void Consecutive_seconds_count_as_sampling_pressure()
    {
        var now = DateTime.UtcNow;
        var start = now.AddMinutes(-10);
        var store = new LiquidationStore();

        // BTC: 5 consecutive seconds -> 4 of them follow another one. ETH: isolated seconds.
        for (var i = 0; i < 5; i++)
        {
            store.Add(Liq("BTCUSDT", LiquidatedSide.Long, 10_000m, start.AddSeconds(i)));
        }

        store.Add(Liq("ETHUSDT", LiquidatedSide.Long, 10_000m, start));
        store.Add(Liq("ETHUSDT", LiquidatedSide.Long, 10_000m, start.AddSeconds(10)));

        var pressure = Assert.Single(store.GetSummary(now).Pressure);
        Assert.Equal("BTCUSDT", pressure.Symbol);
        Assert.Equal(5, pressure.ActiveSeconds);
        Assert.Equal(4, pressure.SaturatedSeconds);
    }

    [Fact]
    public void Gaps_and_partial_coverage_are_reported()
    {
        var now = DateTime.UtcNow;
        var store = new LiquidationStore();

        // Collected 10h ago for 2h, then down for 6h, then up again for the last 2h.
        store.Restore([], [], [new FeedSession(now.AddHours(-10), now.AddHours(-8))]);
        store.BeginSession(now.AddHours(-2));

        var summary = store.GetSummary(now);

        var gap = Assert.Single(summary.Gaps);
        Assert.Equal(TimeSpan.FromHours(6), gap.Duration);
        Assert.Equal(now.AddHours(-10), summary.CollectingSinceUtc);
        Assert.False(summary.Day.FullyCovered);
        Assert.Equal(TimeSpan.FromHours(4), summary.Day.Covered);
        Assert.True(summary.Hour.FullyCovered);
    }

    [Fact]
    public void Ended_session_is_returned_once_for_persisting()
    {
        var now = DateTime.UtcNow;
        var store = new LiquidationStore();
        store.BeginSession(now.AddMinutes(-5));

        Assert.Equal(new FeedSession(now.AddMinutes(-5), now), store.EndSession(now));
        Assert.Null(store.EndSession(now));
        Assert.Null(store.CurrentSession(now));
        Assert.False(store.GetSummary(now).Connected);
    }
}
