using Binance.Net.Interfaces.Clients;
using Binance.Net.Objects.Options;
using Binance.Net.SymbolOrderBooks;
using CryptoExchange.Net.Interfaces;
using CryptoOrderBookDashboard.OrderBooks.Configuration;
using Microsoft.Extensions.Options;

namespace CryptoOrderBookDashboard.OrderBooks;

public sealed class OrderBookHostedService(
    IOptions<OrderBookDashboardOptions> options,
    OrderBookStore store,
    ILoggerFactory loggerFactory,
    IBinanceRestClient restClient,
    IBinanceSocketClient socketClient,
    ILogger<OrderBookHostedService> logger) : BackgroundService
{
    private readonly List<(OrderBookKey Key, ISymbolOrderBook Book)> _books = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;

        foreach (var tracked in settings.Symbols)
        {
            var markets = tracked.Markets.Count > 0
                ? tracked.Markets
                : [MarketType.Spot, MarketType.UsdFutures];

            foreach (var market in markets)
            {
                var key = new OrderBookKey(market, tracked.Symbol);
                ISymbolOrderBook book = market switch
                {
                    MarketType.Spot => new BinanceSpotSymbolOrderBook(
                        tracked.Symbol,
                        Configure(settings),
                        loggerFactory,
                        restClient,
                        socketClient),
                    MarketType.UsdFutures => new BinanceFuturesUsdtSymbolOrderBook(
                        tracked.Symbol,
                        Configure(settings),
                        loggerFactory,
                        restClient,
                        socketClient),
                    _ => throw new NotSupportedException($"Unsupported market type '{market}'")
                };

                book.OnStatusChange += (oldStatus, newStatus) =>
                    logger.LogInformation("Order book {Key} status changed: {Old} -> {New}", key, oldStatus, newStatus);

                store.Register(key, book);
                _books.Add((key, book));
            }
        }

        await Task.WhenAll(_books.Select(b => b.Book.StartAsync(stoppingToken)));

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var (key, book) in _books)
        {
            await book.StopAsync();
            (book as IDisposable)?.Dispose();
            store.Unregister(key);
        }

        await base.StopAsync(cancellationToken);
    }

    private static Action<BinanceOrderBookOptions> Configure(OrderBookDashboardOptions settings) => o =>
    {
        // Leave Limit unset (null) so the library does a full diff-stream sync instead of the
        // fixed-levels partial-depth stream (which only allows 5/10/20) - needed so the size
        // filter can find large orders that sit deeper than the top N levels shown by default.
        o.UpdateInterval = 100;
    };
}
