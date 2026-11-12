using Binance.Net.Interfaces.Clients;
using Binance.Net.Objects.Models.Futures.Socket;

namespace CryptoOrderBookDashboard.Funding;

// Feeds FundingStore for every USDT-M perpetual:
// - live mark price / estimated funding from the single !markPrice@arr stream (1s),
// - settled funding history from /fapi/v1/fundingRate without a symbol, which returns all
//   symbols at once (~120 pages for 30 days instead of ~900 per-symbol calls),
// - funding intervals from /fapi/v1/fundingInfo.
public sealed class FundingRateHostedService(
    FundingStore store,
    IBinanceRestClient restClient,
    IBinanceSocketClient socketClient,
    ILogger<FundingRateHostedService> logger) : BackgroundService
{
    private const int HistoryPageLimit = 1000;

    // Safety cap; a full 30-day backfill currently needs ~120 pages.
    private const int MaxHistoryPages = 400;

    // /fundingRate shares a 500 requests / 5 min / IP budget, so pace the backfill.
    private static readonly TimeSpan HistoryPageDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan IntervalRefreshInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await LoadIntervalsAsync(stoppingToken);
        await LoadSnapshotAsync(stoppingToken);

        var subscription = await socketClient.UsdFuturesApi.ExchangeData.SubscribeToAllMarkPriceUpdatesAsync(
            1000,
            update => store.UpdateTickers(update.Data.Where(IsFundingSymbol).Select(ToTicker)),
            stoppingToken);

        if (subscription.Success)
        {
            // Rates and next-funding times may have moved while disconnected.
            subscription.Data.ConnectionRestored += downtime => _ = LoadSnapshotAsync(stoppingToken);
        }
        else
        {
            logger.LogError("Failed to subscribe to mark price updates: {Error}", subscription.Error);
        }

        await FetchHistoryAsync(DateTime.UtcNow.AddDays(-FundingStore.HistoryDays), stoppingToken);
        store.PublishHistory(loaded: true);

        var lastIntervalRefresh = DateTime.UtcNow;
        using var timer = new PeriodicTimer(RefreshInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var from = store.LatestSettlementUtc?.AddMilliseconds(1) ?? DateTime.UtcNow.AddDays(-FundingStore.HistoryDays);
                await FetchHistoryAsync(from, stoppingToken);
                store.PublishHistory(loaded: true);

                if (DateTime.UtcNow - lastIntervalRefresh >= IntervalRefreshInterval)
                {
                    lastIntervalRefresh = DateTime.UtcNow;
                    await LoadIntervalsAsync(stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
        finally
        {
            if (subscription.Success)
            {
                await subscription.Data.CloseAsync();
            }
        }
    }

    private async Task LoadIntervalsAsync(CancellationToken ct)
    {
        try
        {
            var result = await restClient.UsdFuturesApi.ExchangeData.GetFundingInfoAsync(ct);
            if (!result.Success)
            {
                logger.LogWarning("Failed to load funding intervals: {Error}", result.Error);
                return;
            }

            store.SetIntervals(result.Data
                .Where(i => i.FundingIntervalHours > 0)
                .ToDictionary(i => i.Symbol, i => i.FundingIntervalHours));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to load funding intervals");
        }
    }

    private async Task LoadSnapshotAsync(CancellationToken ct)
    {
        try
        {
            var result = await restClient.UsdFuturesApi.ExchangeData.GetMarkPricesAsync(ct);
            if (!result.Success)
            {
                logger.LogWarning("Failed to load mark price snapshot: {Error}", result.Error);
                return;
            }

            store.UpdateTickers(result.Data
                .Where(m => FundingMath.IsUsdtPerpetual(m.Symbol) && m.NextFundingTime > DateTime.UnixEpoch)
                .Select(m => new FundingTicker(m.Symbol, m.MarkPrice, m.IndexPrice, m.FundingRate ?? 0m, m.NextFundingTime)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to load mark price snapshot");
        }
    }

    // Pages forward in time from `from`. Results are ascending by funding time; the next page
    // starts at the last page's final timestamp (not +1ms) because several symbols settle at
    // the same instant and a page can end in the middle of them - AddSettlements de-duplicates.
    private async Task FetchHistoryAsync(DateTime from, CancellationToken ct)
    {
        var start = from;

        for (var page = 0; page < MaxHistoryPages; page++)
        {
            if (page > 0)
            {
                await Task.Delay(HistoryPageDelay, ct);
            }

            var result = await restClient.UsdFuturesApi.ExchangeData.GetFundingRatesAsync(
                null!, startTime: start, limit: HistoryPageLimit, ct: ct);

            if (!result.Success)
            {
                // The next refresh resumes from the latest stored settlement, so nothing is lost.
                logger.LogWarning("Funding history page from {Start} failed: {Error}", start, result.Error);
                return;
            }

            var rows = result.Data;
            store.AddSettlements(rows.Select(r => new FundingSettlementRecord(r.Symbol, r.FundingTime, r.FundingRate)));

            if (page % 10 == 9)
            {
                // Let the page fill in progressively during the initial ~minute-long backfill.
                store.PublishHistory(loaded: false);
            }

            if (rows.Length < HistoryPageLimit)
            {
                return;
            }

            var first = rows[0].FundingTime;
            var last = rows[^1].FundingTime;
            start = last == first ? last.AddMilliseconds(1) : last;
        }

        logger.LogWarning("Funding history fetch stopped at the {Pages}-page safety cap", MaxHistoryPages);
    }

    private static bool IsFundingSymbol(BinanceFuturesUsdtStreamMarkPrice m) =>
        FundingMath.IsUsdtPerpetual(m.Symbol) && m.NextFundingTime > DateTime.UnixEpoch;

    private static FundingTicker ToTicker(BinanceFuturesUsdtStreamMarkPrice m) =>
        new(m.Symbol, m.MarkPrice, m.IndexPrice, m.FundingRate ?? 0m, m.NextFundingTime);
}
