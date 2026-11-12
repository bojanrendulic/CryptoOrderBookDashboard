using CryptoOrderBookDashboard.OrderBooks.Configuration;
using Microsoft.Extensions.Options;

namespace CryptoOrderBookDashboard.Trades;

public sealed class TradeRenderPulseService(
    IOptions<OrderBookDashboardOptions> options,
    TradeStore store) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(50, options.Value.UpdateThrottleMilliseconds));
        using var timer = new PeriodicTimer(interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            store.RaiseChanged();
        }
    }
}
