using CryptoOrderBookDashboard.Funding;
using CryptoOrderBookDashboard.Liquidations;
using CryptoOrderBookDashboard.OrderBooks;
using CryptoOrderBookDashboard.OrderBooks.Configuration;
using CryptoOrderBookDashboard.Trades;
using Microsoft.Extensions.Options;

namespace CryptoOrderBookDashboard.Common;

// Drives UI refreshes at a fixed rate instead of per exchange message. Each panel compares
// its store's version on every pulse and only re-renders when its data changed.
public sealed class RenderPulseService(
    IOptions<OrderBookDashboardOptions> options,
    OrderBookStore orderBookStore,
    TradeStore tradeStore,
    FundingStore fundingStore,
    LiquidationStore liquidationStore) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(50, options.Value.UpdateThrottleMilliseconds));
        using var timer = new PeriodicTimer(interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            orderBookStore.RaiseChanged();
            tradeStore.RaiseChanged();
            fundingStore.RaiseChanged();
            liquidationStore.RaiseChanged();
            liquidationStore.RaiseChanged();
        }
    }
}
