using Binance.Net.Interfaces.Clients;
using Binance.Net.Objects.Models.Spot.Socket;
using CryptoExchange.Net.Objects.Sockets;
using CryptoOrderBookDashboard.OrderBooks;
using CryptoOrderBookDashboard.OrderBooks.Configuration;
using Microsoft.Extensions.Options;

namespace CryptoOrderBookDashboard.Trades;

public sealed class TradeFeedHostedService(
    IOptions<OrderBookDashboardOptions> options,
    TradeStore store,
    IBinanceRestClient restClient,
    IBinanceSocketClient socketClient,
    ILogger<TradeFeedHostedService> logger) : BackgroundService
{
    private const int BackfillLimit = 1000;

    private readonly List<(OrderBookKey Key, UpdateSubscription Subscription)> _subscriptions = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;

        foreach (var tracked in settings.Symbols)
        {
            foreach (var market in tracked.GetMarkets())
            {
                var key = new OrderBookKey(market, tracked.Symbol);
                var symbol = tracked.Symbol;
                store.Register(key);

                // Subscribe first, then backfill: the backfill overlaps whatever the stream has
                // already delivered and the store de-duplicates by id, so no trades fall into a
                // gap between the REST snapshot and the first stream message.
                var result = market switch
                {
                    MarketType.Spot => await socketClient.SpotApi.ExchangeData.SubscribeToAggregatedTradeUpdatesAsync(
                        tracked.Symbol,
                        update => OnTrade(key, update.Data),
                        stoppingToken),
                    MarketType.UsdFutures => await socketClient.UsdFuturesApi.ExchangeData.SubscribeToAggregatedTradeUpdatesAsync(
                        tracked.Symbol,
                        update => OnTrade(key, update.Data),
                        stoppingToken),
                    _ => throw new NotSupportedException($"Unsupported market type '{market}'")
                };

                if (result.Success)
                {
                    _subscriptions.Add((key, result.Data));

                    // Trades executed while the socket was down are never replayed by the
                    // stream, so fill that hole from REST once the connection comes back.
                    result.Data.ConnectionRestored += downtime => _ = SafeBackfillAsync(key, market, symbol, stoppingToken);
                }
                else
                {
                    logger.LogError("Failed to subscribe to trades for {Key}: {Error}", key, result.Error);
                }

                await SafeBackfillAsync(key, market, symbol, stoppingToken);
            }
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
    }

    private void OnTrade(OrderBookKey key, BinanceStreamAggregatedTrade trade) =>
        store.AddTrade(key, new TradeEntry(trade.Id, trade.Price, trade.Quantity, trade.BuyerIsMaker, trade.TradeTime));

    private async Task SafeBackfillAsync(OrderBookKey key, MarketType market, string symbol, CancellationToken ct)
    {
        try
        {
            await BackfillAsync(key, market, symbol, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Trade backfill for {Key} failed", key);
        }
    }

    private async Task BackfillAsync(OrderBookKey key, MarketType market, string symbol, CancellationToken ct)
    {
        var result = market switch
        {
            MarketType.Spot => await restClient.SpotApi.ExchangeData.GetAggregatedTradeHistoryAsync(
                symbol,
                limit: BackfillLimit,
                ct: ct),
            MarketType.UsdFutures => await restClient.UsdFuturesApi.ExchangeData.GetAggregatedTradeHistoryAsync(
                symbol,
                limit: BackfillLimit,
                ct: ct),
            _ => throw new NotSupportedException($"Unsupported market type '{market}'")
        };

        if (!result.Success)
        {
            logger.LogWarning("Failed to backfill trade history for {Key}: {Error}", key, result.Error);
            return;
        }

        var trades = result.Data
            .Select(t => new TradeEntry(t.Id, t.Price, t.Quantity, t.BuyerIsMaker, t.TradeTime));

        store.Merge(key, trades);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var (key, subscription) in _subscriptions)
        {
            await subscription.CloseAsync();
            store.Unregister(key);
        }

        await base.StopAsync(cancellationToken);
    }
}
