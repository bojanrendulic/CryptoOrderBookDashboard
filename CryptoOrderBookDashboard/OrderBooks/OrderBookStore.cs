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
        DateTime UpdateTimeUtc);

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
        decimal? maxNotionalUsd = null)
    {
        if (!_books.TryGetValue(key, out var entry))
        {
            return OrderBookSnapshot.Empty;
        }

        var book = GetOrBuildCache(entry);

        List<PriceLevel> bids;
        List<PriceLevel> asks;

        if (minNotionalUsd is not null || maxNotionalUsd is not null)
        {
            bids = book.Bids
                .Where(l => Notional.InRange(l.Price * l.Quantity, minNotionalUsd, maxNotionalUsd))
                .Take(MaxFilteredRows)
                .ToList();

            asks = book.Asks
                .Where(l => Notional.InRange(l.Price * l.Quantity, minNotionalUsd, maxNotionalUsd))
                .Take(MaxFilteredRows)
                .ToList();
        }
        else
        {
            bids = book.Bids.Take(levels).ToList();
            asks = book.Asks.Take(levels).ToList();
        }

        return new OrderBookSnapshot(bids, asks, book.Status, book.UpdateTimeUtc);
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
