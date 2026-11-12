using System.Collections.Concurrent;
using CryptoExchange.Net.Interfaces;

namespace CryptoOrderBookDashboard.OrderBooks;

public sealed class OrderBookStore
{
    private readonly ConcurrentDictionary<OrderBookKey, ISymbolOrderBook> _books = new();

    public event Action? Changed;

    public IReadOnlyCollection<OrderBookKey> TrackedKeys => _books.Keys.ToList();

    public void Register(OrderBookKey key, ISymbolOrderBook book) => _books[key] = book;

    public void Unregister(OrderBookKey key) => _books.TryRemove(key, out _);

    public void RaiseChanged() => Changed?.Invoke();

    private const int MaxFilteredRows = 100;

    public OrderBookSnapshot GetSnapshot(
        OrderBookKey key,
        int levels,
        decimal? minNotionalUsd = null,
        decimal? maxNotionalUsd = null)
    {
        if (!_books.TryGetValue(key, out var book))
        {
            return OrderBookSnapshot.Empty;
        }

        // Read both sides in one call so they come from the same book state - reading Bids and
        // Asks separately can straddle an update and briefly show a crossed book.
        var (bookBids, bookAsks) = book.Book;

        List<PriceLevel> bids;
        List<PriceLevel> asks;

        if (minNotionalUsd is not null || maxNotionalUsd is not null)
        {
            bids = bookBids
                .Where(e => MatchesRange(e.Price * e.Quantity, minNotionalUsd, maxNotionalUsd))
                .Take(MaxFilteredRows)
                .Select(e => new PriceLevel(e.Price, e.Quantity))
                .ToList();

            asks = bookAsks
                .Where(e => MatchesRange(e.Price * e.Quantity, minNotionalUsd, maxNotionalUsd))
                .Take(MaxFilteredRows)
                .Select(e => new PriceLevel(e.Price, e.Quantity))
                .ToList();
        }
        else
        {
            bids = bookBids
                .Take(levels)
                .Select(e => new PriceLevel(e.Price, e.Quantity))
                .ToList();

            asks = bookAsks
                .Take(levels)
                .Select(e => new PriceLevel(e.Price, e.Quantity))
                .ToList();
        }

        return new OrderBookSnapshot(
            bids,
            asks,
            OrderBookFeedStatusMapper.FromLibraryStatus(book.Status),
            book.UpdateTime);
    }

    private static bool MatchesRange(decimal notional, decimal? min, decimal? max) =>
        (min is null || notional >= min.Value) && (max is null || notional < max.Value);
}
