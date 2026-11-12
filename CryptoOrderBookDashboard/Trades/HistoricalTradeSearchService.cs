using System.Diagnostics;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using Binance.Net.Interfaces.Clients;
using CryptoOrderBookDashboard.OrderBooks;

namespace CryptoOrderBookDashboard.Trades;

public enum HistoricalTradeSearchStatus
{
    Completed,
    TimedOut,
    MatchLimitReached,
    Cancelled,
    ApiError,

    // Every candidate hour was visited, but at least one was too busy to page through
    // completely within MaxPagesPerWindow, so matches late in that hour may be missing.
    Incomplete,
}

public sealed record HistoricalTradeMatch(long Id, decimal Price, decimal Quantity, bool BuyerIsMaker, DateTime TradeTimeUtc);

public sealed record HistoricalTradeSearchProgress(int MatchesSoFar, DateTime SearchedBackToUtc);

public sealed record HistoricalTradeSearchResult(
    HistoricalTradeSearchStatus Status,
    IReadOnlyList<HistoricalTradeMatch> Matches,
    DateTime SearchedBackToUtc);

public sealed class HistoricalTradeSearchService(IBinanceRestClient restClient)
{
    // Search runs off the UI thread now (see TradeTapePanel), so this is a safety cap
    // against runaway API usage rather than something users have to sit through.
    private static readonly TimeSpan MaxSearchDuration = TimeSpan.FromMinutes(12);
    private const int MaxMatches = 200;
    private const int AggTradePageLimit = 1000;
    private const int MaxPagesPerWindow = 10;
    private const int MaxRetriesPerCall = 3;
    private static readonly TimeSpan InterPageDelay = TimeSpan.FromMilliseconds(150);

    // Hour-windows are independent, so scanning several concurrently turns wall-clock
    // time from "sum of every window's latency" into "max latency per batch". Kept modest
    // (rather than higher) because a busy hour can already need up to MaxPagesPerWindow
    // sequential calls on its own - too much concurrency on top of that risks tripping
    // Binance's rate limit / IP ban, which previously caused silent under-reporting.
    private const int MaxConcurrentWindows = 4;

    private sealed record WindowScanResult(List<HistoricalTradeMatch> Matches, bool Failed, bool Truncated);

    public async Task<HistoricalTradeSearchResult> FindTradesInRangeAsync(
        MarketType market,
        string symbol,
        decimal? minNotionalUsd,
        decimal? maxNotionalUsd,
        int lookbackDays,
        IProgress<HistoricalTradeSearchProgress>? progress,
        CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-lookbackDays);
        var now = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var searchedBackTo = now;
        var matches = new List<HistoricalTradeMatch>();
        var anyTruncated = false;

        try
        {
            // Hourly candles for the lookback window (up to 30 days = 720 rows) stay well within
            // the 1000-row limit, so the whole window's volume profile comes back in one call.
            var klines = await GetKlinesAsync(market, symbol, cutoff, now, ct);
            if (klines is null)
            {
                // Without the volume profile nothing was searched - reporting "Completed" with
                // zero matches here would be a false negative.
                return new HistoricalTradeSearchResult(HistoricalTradeSearchStatus.ApiError, matches, searchedBackTo);
            }

            // An hour's total volume is a hard lower bound for any single trade within it, so
            // this can never skip an hour that actually contains a qualifying trade - hours that
            // don't meet it are provably safe to skip without spending any verification calls.
            var candidates = klines
                .Where(c => minNotionalUsd is null || c.QuoteVolume >= minNotionalUsd.Value)
                .OrderByDescending(c => c.OpenTime)
                .ToList();

            for (var i = 0; i < candidates.Count; i += MaxConcurrentWindows)
            {
                ct.ThrowIfCancellationRequested();

                if (stopwatch.Elapsed >= MaxSearchDuration)
                {
                    return new HistoricalTradeSearchResult(HistoricalTradeSearchStatus.TimedOut, Finalize(matches), searchedBackTo);
                }

                var batch = candidates.Skip(i).Take(MaxConcurrentWindows).ToList();
                var batchResults = await Task.WhenAll(batch.Select(candle =>
                    FindMatchesInWindowAsync(market, symbol, candle.OpenTime, candle.CloseTime, minNotionalUsd, maxNotionalUsd, ct)));

                foreach (var windowResult in batchResults)
                {
                    matches.AddRange(windowResult.Matches);
                    anyTruncated |= windowResult.Truncated;
                }

                searchedBackTo = batch.Min(c => c.OpenTime);

                // A window that failed even after retries (most likely a rate limit / IP ban)
                // means we can no longer honestly claim full coverage - stop rather than let
                // the loop run to the end and falsely report "Completed".
                if (batchResults.Any(r => r.Failed))
                {
                    return new HistoricalTradeSearchResult(HistoricalTradeSearchStatus.ApiError, Finalize(matches), searchedBackTo);
                }

                progress?.Report(new HistoricalTradeSearchProgress(matches.Count, searchedBackTo));

                if (matches.Count >= MaxMatches)
                {
                    return new HistoricalTradeSearchResult(
                        HistoricalTradeSearchStatus.MatchLimitReached,
                        Finalize(matches).Take(MaxMatches).ToList(),
                        searchedBackTo);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return new HistoricalTradeSearchResult(HistoricalTradeSearchStatus.Cancelled, Finalize(matches), searchedBackTo);
        }

        var status = anyTruncated ? HistoricalTradeSearchStatus.Incomplete : HistoricalTradeSearchStatus.Completed;
        return new HistoricalTradeSearchResult(status, Finalize(matches), cutoff);
    }

    private static List<HistoricalTradeMatch> Finalize(List<HistoricalTradeMatch> matches) =>
        matches.OrderByDescending(m => m.TradeTimeUtc).ToList();

    // Returns null when the kline request fails, so callers can tell "no candidate hours"
    // apart from "couldn't find out".
    private async Task<List<IBinanceKline>?> GetKlinesAsync(
        MarketType market,
        string symbol,
        DateTime start,
        DateTime end,
        CancellationToken ct)
    {
        var result = market switch
        {
            MarketType.Spot => await restClient.SpotApi.ExchangeData.GetKlinesAsync(
                symbol, KlineInterval.OneHour, start, end, 1000, ct),
            MarketType.UsdFutures => await restClient.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                symbol, KlineInterval.OneHour, start, end, 1000, ct),
            _ => throw new NotSupportedException($"Unsupported market type '{market}'")
        };

        return result.Success ? result.Data.ToList() : null;
    }

    // Scans one hour-long window for qualifying trades. Binance caps aggTrades time-range
    // queries at just under an hour (which is why the kline interval above is hourly), and
    // caps each page at 1000 trades - busy hours need extra fromId-based pages to fully cover.
    // MaxPagesPerWindow bounds how many pages a single very liquid hour can consume, so one
    // "hot" hour can't starve the other concurrently-running windows in its batch.
    private async Task<WindowScanResult> FindMatchesInWindowAsync(
        MarketType market,
        string symbol,
        DateTime start,
        DateTime end,
        decimal? min,
        decimal? max,
        CancellationToken ct)
    {
        var matches = new List<HistoricalTradeMatch>();
        long? fromId = null;
        var pages = 0;

        while (true)
        {
            if (pages >= MaxPagesPerWindow)
            {
                // Page budget spent before reaching the end of the hour - the rest of it was
                // never looked at, so this window must not count as fully covered.
                return new WindowScanResult(matches, Failed: false, Truncated: true);
            }

            if (pages > 0)
            {
                // Spread out pagination within a busy hour instead of firing pages back to
                // back, so a single hot hour doesn't burn through the rate-limit budget alone.
                await Task.Delay(InterPageDelay, ct);
            }

            pages++;
            ct.ThrowIfCancellationRequested();

            var result = fromId is null
                ? await GetAggTradesWithRetryAsync(market, symbol, start, end, null, ct)
                : await GetAggTradesWithRetryAsync(market, symbol, null, null, fromId, ct);

            if (!result.Success)
            {
                // Non-transient failure (or retries exhausted) - this hour could not be fully
                // verified. Report it as failed rather than silently treating it as "no matches".
                return new WindowScanResult(matches, Failed: true, Truncated: false);
            }

            var page = result.Data.OrderBy(t => t.TradeTime).ToList();
            if (page.Count == 0)
            {
                break;
            }

            foreach (var trade in page)
            {
                if (trade.TradeTime >= start
                    && trade.TradeTime < end
                    && MatchesRange(trade.Price * trade.Quantity, min, max))
                {
                    matches.Add(new HistoricalTradeMatch(trade.Id, trade.Price, trade.Quantity, trade.BuyerIsMaker, trade.TradeTime));
                }
            }

            var last = page[^1];
            if (page.Count < AggTradePageLimit || last.TradeTime >= end)
            {
                // Either this was the last page, or we've paged past the window end.
                break;
            }

            fromId = last.Id + 1;
        }

        return new WindowScanResult(matches, Failed: false, Truncated: false);
    }

    // Retries transient failures (rate limits, timeouts) with a short backoff, so a busy
    // search hitting Binance's rate limit doesn't silently under-report an hour's trades.
    private async Task<CryptoExchange.Net.Objects.HttpResult<Binance.Net.Objects.Models.Spot.BinanceAggregatedTrade[]>> GetAggTradesWithRetryAsync(
        MarketType market,
        string symbol,
        DateTime? startTime,
        DateTime? endTime,
        long? fromId,
        CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await GetAggTradesAsync(market, symbol, startTime, endTime, fromId, ct);
            if (result.Success || result.Error?.IsTransient != true || attempt >= MaxRetriesPerCall)
            {
                return result;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(1000 * (attempt + 1)), ct);
        }
    }

    private async Task<CryptoExchange.Net.Objects.HttpResult<Binance.Net.Objects.Models.Spot.BinanceAggregatedTrade[]>> GetAggTradesAsync(
        MarketType market,
        string symbol,
        DateTime? startTime,
        DateTime? endTime,
        long? fromId,
        CancellationToken ct) => market switch
    {
        MarketType.Spot => await restClient.SpotApi.ExchangeData.GetAggregatedTradeHistoryAsync(
            symbol, fromId: fromId, startTime: startTime, endTime: endTime, limit: AggTradePageLimit, ct: ct),
        MarketType.UsdFutures => await restClient.UsdFuturesApi.ExchangeData.GetAggregatedTradeHistoryAsync(
            symbol, fromId: fromId, startTime: startTime, endTime: endTime, limit: AggTradePageLimit, ct: ct),
        _ => throw new NotSupportedException($"Unsupported market type '{market}'")
    };

    private static bool MatchesRange(decimal notional, decimal? min, decimal? max) =>
        (min is null || notional >= min.Value) && (max is null || notional < max.Value);
}
