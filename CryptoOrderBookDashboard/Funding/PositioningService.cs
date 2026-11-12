using System.Collections.Concurrent;
using Binance.Net.Enums;
using Binance.Net.Interfaces.Clients;

namespace CryptoOrderBookDashboard.Funding;

// Open interest and long/short ratios. Binance only serves these per symbol (from the
// rate-limited /futures/data endpoints), so they can't be polled for all ~900 perpetuals:
// tracked symbols are refreshed every few minutes, anything else when a user selects it.
public sealed class PositioningService(
    FundingStore store,
    IBinanceRestClient restClient,
    ILogger<PositioningService> logger) : BackgroundService
{
    // Matches the 5-minute granularity of the ratio endpoints - fetching more often is pointless.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _gate = new(2, 2);
    private readonly ConcurrentDictionary<string, Task> _inFlight = new();

    // Fire-and-forget safe: never throws, and concurrent requests for one symbol share a fetch.
    // Results land in FundingStore and show up on the next render pulse.
    public Task EnsureFreshAsync(string symbol)
    {
        var existing = store.GetPositioning(symbol);
        if (existing is not null && DateTime.UtcNow - existing.FetchedUtc < CacheTtl)
        {
            return Task.CompletedTask;
        }

        return _inFlight.GetOrAdd(symbol, s => Task.Run(() => FetchAsync(s)));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CacheTtl);

        do
        {
            foreach (var symbol in store.TrackedSymbols)
            {
                await EnsureFreshAsync(symbol);
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task FetchAsync(string symbol)
    {
        await _gate.WaitAsync();

        try
        {
            var api = restClient.UsdFuturesApi.ExchangeData;
            var openInterestTask = api.GetOpenInterestAsync(symbol);
            var historyTask = api.GetOpenInterestHistoryAsync(symbol, PeriodInterval.OneHour, 25);
            var globalTask = api.GetGlobalLongShortAccountRatioAsync(symbol, PeriodInterval.FiveMinutes, 1);
            var topTask = api.GetTopLongShortPositionRatioAsync(symbol, PeriodInterval.FiveMinutes, 1);

            await Task.WhenAll(openInterestTask, historyTask, globalTask, topTask);

            var openInterest = openInterestTask.Result;
            if (!openInterest.Success)
            {
                logger.LogWarning("Failed to load open interest for {Symbol}: {Error}", symbol, openInterest.Error);
                return;
            }

            // 25 hourly points: the oldest one is ~24h ago.
            decimal? change24h = null;
            var oldest = historyTask.Result.Success
                ? historyTask.Result.Data.OrderBy(h => h.Timestamp).FirstOrDefault()
                : null;
            if (oldest is { SumOpenInterest: > 0 })
            {
                change24h = (openInterest.Data.OpenInterest - oldest.SumOpenInterest) / oldest.SumOpenInterest * 100m;
            }

            var global = globalTask.Result.Success ? globalTask.Result.Data.LastOrDefault() : null;
            var top = topTask.Result.Success ? topTask.Result.Data.LastOrDefault() : null;

            store.SetPositioning(symbol, new PositioningSnapshot(
                openInterest.Data.OpenInterest,
                change24h,
                global?.LongAccount * 100m,
                global?.LongShortRatio,
                top?.LongShortRatio,
                DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load positioning data for {Symbol}", symbol);
        }
        finally
        {
            _gate.Release();
            _inFlight.TryRemove(symbol, out _);
        }
    }
}
