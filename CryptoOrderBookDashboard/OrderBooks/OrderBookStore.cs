using System.Collections.Concurrent;
using CryptoExchange.Net.Interfaces;
using CryptoOrderBookDashboard.Common;

namespace CryptoOrderBookDashboard.OrderBooks;

public sealed class OrderBookStore
{
    private const int MaxFilteredRows = 100;

    private sealed record CachedBook(
        long Version,
        PriceLevel[] Bids,
        PriceLevel[] Asks,
        OrderBookFeedStatus Status,
        DateTime UpdateTimeUtc)
    {
        // Grouped views and depth sums derived from this version, keyed by step / percent and
        // shared by all viewers like the copy itself.
        public ConcurrentDictionary<decimal, (PriceLevel[] Bids, PriceLevel[] Asks)> Grouped { get; } = new();

        public ConcurrentDictionary<decimal, DepthSummary> Depths { get; } = new();

        public ConcurrentDictionary<decimal, DepthCurve> Curves { get; } = new();
    }

    private sealed class Entry(ISymbolOrderBook book)
    {
        public ISymbolOrderBook Book { get; } = book;

        public readonly object CacheGate = new();

        // Bumped on every book update and status change; lets readers tell whether anything
        // changed since they last looked without touching the book itself.
        public long Version;

        public CachedBook? Cache;
    }

    private readonly ConcurrentDictionary<OrderBookKey, Entry> _books = new();

    public event Action? Changed;

    public void Register(OrderBookKey key, ISymbolOrderBook book)
    {
        var entry = new Entry(book);
        book.OnOrderBookUpdate += _ => Interlocked.Increment(ref entry.Version);
        book.OnStatusChange += (_, _) => Interlocked.Increment(ref entry.Version);
        _books[key] = entry;
    }

    public void Unregister(OrderBookKey key) => _books.TryRemove(key, out _);

    public void RaiseChanged() => Changed?.Invoke();

    // -1 for an unknown key, so a component that rendered "not registered yet" still refreshes
    // once the book appears.
    public long GetVersion(OrderBookKey key) =>
        _books.TryGetValue(key, out var entry) ? Interlocked.Read(ref entry.Version) : -1;

    public OrderBookSnapshot GetSnapshot(
        OrderBookKey key,
        int levels,
        decimal? minNotionalUsd = null,
        decimal? maxNotionalUsd = null,
        decimal? groupPercent = null,
        decimal? depthPercent = null)
    {
        if (!_books.TryGetValue(key, out var entry))
        {
            return OrderBookSnapshot.Empty;
        }

        var book = GetOrBuildCache(entry);

        decimal? bestBid = book.Bids.Length > 0 ? book.Bids[0].Price : null;
        decimal? bestAsk = book.Asks.Length > 0 ? book.Asks[0].Price : null;
        decimal? mid = bestBid is not null && bestAsk is not null ? (bestBid + bestAsk) / 2 : null;

        var sourceBids = book.Bids;
        var sourceAsks = book.Asks;
        decimal? groupStep = null;

        if (groupPercent is > 0 && mid is not null)
        {
            var step = PriceGrouping.NiceStep(mid.Value * groupPercent.Value / 100m);
            if (step > 0)
            {
                groupStep = step;
                (sourceBids, sourceAsks) = book.Grouped.GetOrAdd(
                    step,
                    s => (Group(book.Bids, s, roundUp: false), Group(book.Asks, s, roundUp: true)));
            }
        }

        List<PriceLevel> bids;
        List<PriceLevel> asks;

        if (minNotionalUsd is not null || maxNotionalUsd is not null)
        {
            bids = sourceBids
                .Where(l => Notional.InRange(l.Price * l.Quantity, minNotionalUsd, maxNotionalUsd))
                .Take(MaxFilteredRows)
                .ToList();

            asks = sourceAsks
                .Where(l => Notional.InRange(l.Price * l.Quantity, minNotionalUsd, maxNotionalUsd))
                .Take(MaxFilteredRows)
                .ToList();
        }
        else
        {
            bids = sourceBids.Take(levels).ToList();
            asks = sourceAsks.Take(levels).ToList();
        }

        DepthSummary? depth = null;
        if (depthPercent is > 0 && mid is not null)
        {
            depth = book.Depths.GetOrAdd(depthPercent.Value, p => ComputeDepth(book, mid.Value, p));
        }

        return new OrderBookSnapshot(
            bids,
            asks,
            bestBid,
            bestAsk,
            groupStep,
            depth,
            book.Status,
            book.UpdateTimeUtc);
    }

    // Merges levels into price buckets of `step`. Bids round down and asks round up (as on
    // Binance), so a bucket never crosses the spread. Input is best-first, so equal buckets
    // are always adjacent.
    private static PriceLevel[] Group(PriceLevel[] levels, decimal step, bool roundUp)
    {
        var result = new List<PriceLevel>();
        foreach (var level in levels)
        {
            var bucket = (roundUp ? Math.Ceiling(level.Price / step) : Math.Floor(level.Price / step)) * step;
            if (result.Count > 0 && result[^1].Price == bucket)
            {
                result[^1] = result[^1] with { Quantity = result[^1].Quantity + level.Quantity };
            }
            else
            {
                result.Add(new PriceLevel(bucket, level.Quantity));
            }
        }

        return result.ToArray();
    }

    // Cumulative depth curve for the depth chart, from the unfiltered, ungrouped book. Cached per
    // book version and band like the depth sums, so all viewers share one computation.
    public DepthCurve? GetDepthCurve(OrderBookKey key, decimal percent)
    {
        if (percent <= 0 || !_books.TryGetValue(key, out var entry))
        {
            return null;
        }

        var book = GetOrBuildCache(entry);
        if (book.Bids.Length == 0 || book.Asks.Length == 0)
        {
            return null;
        }

        var mid = (book.Bids[0].Price + book.Asks[0].Price) / 2;
        return book.Curves.GetOrAdd(percent, p => ComputeCurve(book, mid, p));
    }

    private static DepthCurve ComputeCurve(CachedBook book, decimal mid, decimal percent) =>
        DepthCurve.Build(
            mid,
            mid * (1 - percent / 100m),
            mid * (1 + percent / 100m),
            book.Bids,
            book.Asks,
            bookEnds: true,
            DepthBand.FormatLabel(percent));

    private static DepthSummary ComputeDepth(CachedBook book, decimal mid, decimal percent)
    {
        var lowerBound = mid * (1 - percent / 100m);
        var upperBound = mid * (1 + percent / 100m);

        var bidsUsd = 0m;
        foreach (var level in book.Bids)
        {
            if (level.Price < lowerBound)
            {
                break;
            }

            bidsUsd += level.Price * level.Quantity;
        }

        var asksUsd = 0m;
        foreach (var level in book.Asks)
        {
            if (level.Price > upperBound)
            {
                break;
            }

            asksUsd += level.Price * level.Quantity;
        }

        // The sums are only complete if the local book reaches past the band on both sides.
        var complete = book.Bids.Length > 0 && book.Bids[^1].Price < lowerBound
            && book.Asks.Length > 0 && book.Asks[^1].Price > upperBound;

        return new DepthSummary(DepthBand.FormatLabel(percent), bidsUsd, asksUsd, complete);
    }

    // book.Book copies the whole (full-depth) book under the library's lock. Doing that per
    // viewer per pulse doesn't scale, so the copy is shared by everyone reading the same
    // version and only rebuilt after the book has actually changed.
    private static CachedBook GetOrBuildCache(Entry entry)
    {
        var version = Interlocked.Read(ref entry.Version);
        var cache = Volatile.Read(ref entry.Cache);
        if (cache?.Version == version)
        {
            return cache;
        }

        lock (entry.CacheGate)
        {
            cache = entry.Cache;
            if (cache?.Version == version)
            {
                return cache;
            }

            // Version is read before copying: if the book changes mid-copy the cache is tagged
            // with the older version and simply gets rebuilt on the next read.
            // Both sides come from one call so they reflect the same book state.
            var (bids, asks) = entry.Book.Book;
            cache = new CachedBook(
                version,
                bids.Select(e => new PriceLevel(e.Price, e.Quantity)).ToArray(),
                asks.Select(e => new PriceLevel(e.Price, e.Quantity)).ToArray(),
                OrderBookFeedStatusMapper.FromLibraryStatus(entry.Book.Status),
                entry.Book.UpdateTime);

            Volatile.Write(ref entry.Cache, cache);
            return cache;
        }
    }
}
