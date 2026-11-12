using System.Collections.Concurrent;
using CryptoOrderBookDashboard.OrderBooks;

namespace CryptoOrderBookDashboard.Trades;

public sealed class TradeStore
{
    private const int Capacity = 1000;
    private const int DefaultDisplayRows = 100;
    private const int MaxFilteredRows = 100;

    private sealed class TradeBucket
    {
        public readonly object Gate = new();
        public readonly List<TradeEntry> Trades = new(Capacity);
    }

    private readonly ConcurrentDictionary<OrderBookKey, TradeBucket> _buckets = new();

    public event Action? Changed;

    public void Register(OrderBookKey key) => _buckets.TryAdd(key, new TradeBucket());

    public void Unregister(OrderBookKey key) => _buckets.TryRemove(key, out _);

    // Merges a REST backfill into whatever the live stream has already delivered. The feed
    // subscribes before backfilling (and backfills again after a reconnect), so the two sets
    // overlap - de-duplicating by aggregate trade id closes the gap without double-counting.
    public void Merge(OrderBookKey key, IEnumerable<TradeEntry> trades)
    {
        if (!_buckets.TryGetValue(key, out var bucket))
        {
            return;
        }

        lock (bucket.Gate)
        {
            var merged = bucket.Trades
                .Concat(trades)
                .DistinctBy(t => t.Id)
                .OrderByDescending(t => t.Id)
                .Take(Capacity)
                .ToList();

            bucket.Trades.Clear();
            bucket.Trades.AddRange(merged);
        }
    }

    public void RaiseChanged() => Changed?.Invoke();

    public void AddTrade(OrderBookKey key, TradeEntry trade)
    {
        if (!_buckets.TryGetValue(key, out var bucket))
        {
            return;
        }

        lock (bucket.Gate)
        {
            // Stream ids only ever increase, so anything at or below the newest stored id was
            // already added (e.g. by a backfill that raced the stream).
            if (bucket.Trades.Count > 0 && trade.Id <= bucket.Trades[0].Id)
            {
                return;
            }

            bucket.Trades.Insert(0, trade);
            if (bucket.Trades.Count > Capacity)
            {
                bucket.Trades.RemoveAt(bucket.Trades.Count - 1);
            }
        }
    }

    public IReadOnlyList<TradeEntry> GetTrades(
        OrderBookKey key,
        decimal? minNotionalUsd = null,
        decimal? maxNotionalUsd = null)
    {
        if (!_buckets.TryGetValue(key, out var bucket))
        {
            return [];
        }

        lock (bucket.Gate)
        {
            if (minNotionalUsd is null && maxNotionalUsd is null)
            {
                return bucket.Trades.Take(DefaultDisplayRows).ToList();
            }

            return bucket.Trades
                .Where(t => MatchesRange(t.Price * t.Quantity, minNotionalUsd, maxNotionalUsd))
                .Take(MaxFilteredRows)
                .ToList();
        }
    }

    public (decimal BuyVolumeUsd, decimal SellVolumeUsd) GetVolumeDelta(
        OrderBookKey key,
        TimeSpan window,
        decimal? minNotionalUsd = null,
        decimal? maxNotionalUsd = null)
    {
        if (!_buckets.TryGetValue(key, out var bucket))
        {
            return (0m, 0m);
        }

        var cutoff = DateTime.UtcNow - window;
        var buy = 0m;
        var sell = 0m;

        lock (bucket.Gate)
        {
            // Trades are stored newest-first, so we can stop as soon as we hit one older than the window.
            foreach (var trade in bucket.Trades)
            {
                if (trade.TradeTimeUtc < cutoff)
                {
                    break;
                }

                var notional = trade.Price * trade.Quantity;
                if (!MatchesRange(notional, minNotionalUsd, maxNotionalUsd))
                {
                    continue;
                }

                if (trade.BuyerIsMaker)
                {
                    sell += notional;
                }
                else
                {
                    buy += notional;
                }
            }
        }

        return (buy, sell);
    }

    private static bool MatchesRange(decimal notional, decimal? min, decimal? max) =>
        (min is null || notional >= min.Value) && (max is null || notional < max.Value);
}
