using System.Collections.Concurrent;
using CryptoOrderBookDashboard.OrderBooks;
using CryptoOrderBookDashboard.OrderBooks.Configuration;
using Microsoft.Extensions.Options;

namespace CryptoOrderBookDashboard.Funding;

public sealed class FundingStore
{
    public const int HistoryDays = 30;

    private sealed record RowsCache(long Version, IReadOnlyList<FundingRow> Rows);

    private readonly ConcurrentDictionary<string, FundingTicker> _tickers = new();
    private readonly ConcurrentDictionary<string, int> _intervals = new();
    private readonly ConcurrentDictionary<string, FundingHistorySummary> _history = new();
    private readonly ConcurrentDictionary<string, PositioningSnapshot> _positioning = new();

    // Raw settled rates per symbol; only touched by the funding hosted service, under the gate.
    private readonly Dictionary<string, SortedList<DateTime, decimal>> _settlements = new();
    private readonly object _settlementGate = new();
    private DateTime? _latestSettlementUtc;

    private readonly object _rowsGate = new();
    private RowsCache? _rowsCache;

    private readonly Dictionary<string, TrackedSymbolOptions> _tracked;

    private long _version;
    private volatile bool _historyLoaded;

    public FundingStore(IOptions<OrderBookDashboardOptions> options)
    {
        _tracked = options.Value.Symbols
            .Where(s => s.GetMarkets().Contains(MarketType.UsdFutures))
            .ToDictionary(s => s.Symbol, StringComparer.OrdinalIgnoreCase);
    }

    public event Action? Changed;

    public bool HistoryLoaded => _historyLoaded;

    public IReadOnlyCollection<string> TrackedSymbols => _tracked.Keys;

    public long Version => Interlocked.Read(ref _version);

    public DateTime? LatestSettlementUtc
    {
        get
        {
            lock (_settlementGate)
            {
                return _latestSettlementUtc;
            }
        }
    }

    public void RaiseChanged() => Changed?.Invoke();

    public void UpdateTickers(IEnumerable<FundingTicker> tickers)
    {
        foreach (var ticker in tickers)
        {
            _tickers[ticker.Symbol] = ticker;
        }

        Interlocked.Increment(ref _version);
    }

    public void SetIntervals(IReadOnlyDictionary<string, int> intervalHoursBySymbol)
    {
        // fundingInfo only lists symbols with non-default settings - everything else is 8h.
        foreach (var symbol in _intervals.Keys.Except(intervalHoursBySymbol.Keys))
        {
            _intervals.TryRemove(symbol, out _);
        }

        foreach (var (symbol, hours) in intervalHoursBySymbol)
        {
            _intervals[symbol] = hours;
        }

        Interlocked.Increment(ref _version);
    }

    public void AddSettlements(IEnumerable<FundingSettlementRecord> records)
    {
        lock (_settlementGate)
        {
            foreach (var record in records)
            {
                if (!FundingMath.IsUsdtPerpetual(record.Symbol))
                {
                    continue;
                }

                if (!_settlements.TryGetValue(record.Symbol, out var list))
                {
                    list = new SortedList<DateTime, decimal>();
                    _settlements[record.Symbol] = list;
                }

                list[record.TimeUtc] = record.Rate;

                if (_latestSettlementUtc is null || record.TimeUtc > _latestSettlementUtc)
                {
                    _latestSettlementUtc = record.TimeUtc;
                }
            }
        }
    }

    // Trims to the history window and recomputes every symbol's summary. Called after a batch
    // of AddSettlements rather than per page, since it walks the whole history.
    public void PublishHistory(bool loaded)
    {
        var now = DateTime.UtcNow;
        var cutoff = now.AddDays(-HistoryDays);
        var firstDay = now.Date.AddDays(-(HistoryDays - 1));

        lock (_settlementGate)
        {
            foreach (var (symbol, list) in _settlements)
            {
                while (list.Count > 0 && list.Keys[0] < cutoff)
                {
                    list.RemoveAt(0);
                }

                var daily = new decimal[HistoryDays];
                var cumulative = 0m;

                foreach (var (time, rate) in list)
                {
                    cumulative += rate;
                    var day = (time.Date - firstDay).Days;
                    if (day is >= 0 and < HistoryDays)
                    {
                        daily[day] += rate;
                    }
                }

                _history[symbol] = list.Count == 0
                    ? new FundingHistorySummary(0m, daily, null, null)
                    : new FundingHistorySummary(cumulative, daily, list.Keys[^1], list.Values[^1]);
            }
        }

        if (loaded)
        {
            _historyLoaded = true;
        }

        Interlocked.Increment(ref _version);
    }

    public void SetPositioning(string symbol, PositioningSnapshot snapshot)
    {
        _positioning[symbol] = snapshot;
        Interlocked.Increment(ref _version);
    }

    public PositioningSnapshot? GetPositioning(string symbol) => _positioning.GetValueOrDefault(symbol);

    // Shared by all viewers and rebuilt only when something changed (the mark price stream
    // bumps the version about once a second).
    public IReadOnlyList<FundingRow> GetRows()
    {
        var version = Version;
        var cache = Volatile.Read(ref _rowsCache);
        if (cache?.Version == version)
        {
            return cache.Rows;
        }

        lock (_rowsGate)
        {
            cache = _rowsCache;
            if (cache?.Version == version)
            {
                return cache.Rows;
            }

            var rows = _tickers.Values.Select(BuildRow).ToList();
            Volatile.Write(ref _rowsCache, new RowsCache(version, rows));
            return rows;
        }
    }

    private FundingRow BuildRow(FundingTicker ticker)
    {
        var interval = _intervals.GetValueOrDefault(ticker.Symbol, FundingMath.DefaultIntervalHours);
        var rate8h = FundingMath.To8h(ticker.EstimatedRate, interval);
        var premium = ticker.IndexPrice > 0 ? (ticker.MarkPrice - ticker.IndexPrice) / ticker.IndexPrice * 100m : 0m;
        var tracked = _tracked.GetValueOrDefault(ticker.Symbol);

        return new FundingRow(
            ticker.Symbol,
            tracked?.GetDisplayName() ?? ticker.Symbol[..^"USDT".Length],
            tracked is not null,
            ticker.MarkPrice,
            premium,
            ticker.EstimatedRate,
            interval,
            rate8h,
            FundingMath.AnnualizedPct(ticker.EstimatedRate, interval),
            ticker.NextFundingTimeUtc,
            FundingMath.Classify(rate8h),
            _history.GetValueOrDefault(ticker.Symbol),
            _positioning.GetValueOrDefault(ticker.Symbol));
    }
}
