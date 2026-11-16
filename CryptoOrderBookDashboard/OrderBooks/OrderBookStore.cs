using System.Collections.Concurrent;
using CryptoExchange.Net.Interfaces;
using CryptoExchange.Net.Objects;
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
        DateTime UpdateTimeUtc,
        SnapshotBoundary? Boundary)
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

        // Range of the snapshot behind the current sync (null until it has been seen).
        public SnapshotBoundary? Boundary;

        // 1 while waiting for the next snapshot: set on start and whenever the book leaves
        // Synced (reconnect, resync), cleared once the snapshot update has been captured.
        public int AwaitingSnapshot = 1;
    }

    private readonly ConcurrentDictionary<OrderBookKey, Entry> _books = new();

    public event Action? Changed;

    public void Register(OrderBookKey key, ISymbolOrderBook book)
    {
        var entry = new Entry(book);
        book.OnOrderBookUpdate += update =>
        {
            if (Volatile.Read(ref entry.AwaitingSnapshot) == 1)
            {
                CaptureBoundary(entry, update.Bids, update.Asks);
            }

            Interlocked.Increment(ref entry.Version);
        };
        book.OnStatusChange += (_, newStatus) =>
        {
            if (newStatus != OrderBookStatus.Synced)
            {
                Volatile.Write(ref entry.Boundary, null);
                Volatile.Write(ref entry.AwaitingSnapshot, 1);
            }

            Interlocked.Increment(ref entry.Version);
        };
        _books[key] = entry;
    }

    // The first update after a (re)sync carries the whole REST snapshot (verified against
    // Binance.Net 13.3: 5000 levels per side on spot, 1000 on futures; on spot it can even arrive
    // before the status turns Synced). Its outermost prices mark how far the book is complete;
    // reading the book on the status change instead is unreliable, because on futures the book is
    // still empty then and on spot buffered diffs have already been applied.
    private static void CaptureBoundary(
        Entry entry,
        IEnumerable<ISymbolOrderBookEntry> bids,
        IEnumerable<ISymbolOrderBookEntry> asks)
    {
        decimal? lowestBid = null;
        decimal? highestAsk = null;
        foreach (var bid in bids)
        {
            lowestBid = lowestBid is null || bid.Price < lowestBid ? bid.Price : lowestBid;
        }

        foreach (var ask in asks)
        {
            highestAsk = highestAsk is null || ask.Price > highestAsk ? ask.Price : highestAsk;
        }

        // Diffs are buffered until the snapshot is applied, so the first update is the snapshot;
        // a one-sided book (nothing to bound) just waits for the next update.
        if (lowestBid is null || highestAsk is null)
        {
            return;
        }

        Volatile.Write(ref entry.Boundary, new SnapshotBoundary(lowestBid.Value, highestAsk.Value, DateTime.UtcNow));
        Volatile.Write(ref entry.AwaitingSnapshot, 0);
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
            book.UpdateTimeUtc,
            book.Boundary);
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
            DepthBand.FormatLabel(percent),
            book.Boundary);

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

        // The sums are only complete if the last snapshot reached the band edge on both sides.
        // The local book often extends much further (levels learned from live updates), but
        // beyond the snapshot unchanged resting orders are missing, so that proves nothing.
        var complete = book.Boundary is { } boundary
            && boundary.CoversBid(lowerBound)
            && boundary.CoversAsk(upperBound);

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
                entry.Book.UpdateTime,
                Volatile.Read(ref entry.Boundary));

            Volatile.Write(ref entry.Cache, cache);
            return cache;
        }
    }
}
