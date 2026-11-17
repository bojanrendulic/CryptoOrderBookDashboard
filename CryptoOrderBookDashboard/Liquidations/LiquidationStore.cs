namespace CryptoOrderBookDashboard.Liquidations;

// In-memory view of the last 24 hours of liquidations, shared by all viewers.
//
// - Events feed the tape (newest first). After a restart only the large ones come back from the
//   database, flagged Restored.
// - Per-minute buckets per symbol and side hold every liquidation and drive the 1h/24h totals, so
//   those survive restarts in full (they are persisted as absolute values).
// - Feed sessions record when the stream was actually connected, so the UI can say how much of a
//   window was covered and where the gaps are.
//
// Everything here is a sample: Binance sends at most one liquidation per symbol per second.
public sealed class LiquidationStore
{
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    // A hole between sessions shorter than this is heartbeat jitter, not a gap worth showing.
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(90);

    private const int MaxEvents = 200_000;
    private const int TopSymbolCount = 15;
    private const int PressureCount = 8;

    private readonly object _gate = new();
    private readonly LinkedList<LiquidationEvent> _events = new(); // oldest first
    private readonly Dictionary<(string Symbol, long MinuteMs, LiquidatedSide Side), (decimal Usd, int Count)> _minutes = new();
    private readonly Dictionary<string, HashSet<long>> _activeSeconds = new(StringComparer.Ordinal);
    private readonly List<FeedSession> _sessions = []; // closed sessions, oldest first
    private DateTime? _openSessionStart;

    private long _version;
    private SummaryCache? _summaryCache;

    public event Action? Changed;

    public long Version => Interlocked.Read(ref _version);

    public void RaiseChanged() => Changed?.Invoke();

    // Loads what the database kept from before this process started.
    public void Restore(
        IEnumerable<LiquidationEvent> largeEvents,
        IEnumerable<LiquidationMinute> minutes,
        IEnumerable<FeedSession> sessions)
    {
        lock (_gate)
        {
            foreach (var evt in largeEvents.OrderBy(e => e.TimeUtc))
            {
                _events.AddLast(evt with { Restored = true });
            }

            foreach (var m in minutes)
            {
                _minutes[(m.Symbol, ToMs(m.MinuteUtc), m.Side)] = (m.Usd, m.Count);
            }

            _sessions.AddRange(sessions.OrderBy(s => s.StartUtc));
            Bump();
        }
    }

    // Records one liquidation and returns its minute bucket's new absolute totals (to persist).
    public LiquidationMinute Add(LiquidationEvent evt)
    {
        lock (_gate)
        {
            _events.AddLast(evt);
            while (_events.Count > MaxEvents)
            {
                _events.RemoveFirst();
            }

            var minuteMs = ToMs(evt.TimeUtc) / 60_000 * 60_000;
            var key = (evt.Symbol, minuteMs, evt.Side);
            _minutes.TryGetValue(key, out var bucket);
            bucket = (bucket.Usd + evt.UsdValue, bucket.Count + 1);
            _minutes[key] = bucket;

            if (!_activeSeconds.TryGetValue(evt.Symbol, out var seconds))
            {
                _activeSeconds[evt.Symbol] = seconds = [];
            }

            seconds.Add(ToMs(evt.TimeUtc) / 1000);

            Bump();
            return new LiquidationMinute(evt.Symbol, FromMs(minuteMs), evt.Side, bucket.Usd, bucket.Count);
        }
    }

    public void BeginSession(DateTime nowUtc)
    {
        lock (_gate)
        {
            _openSessionStart ??= nowUtc;
            Bump();
        }
    }

    // The open session's current extent (to persist as a heartbeat), or null if disconnected.
    public FeedSession? CurrentSession(DateTime nowUtc)
    {
        lock (_gate)
        {
            return _openSessionStart is { } start ? new FeedSession(start, nowUtc) : null;
        }
    }

    // Closes the open session at nowUtc and returns it (to persist), or null if none was open.
    public FeedSession? EndSession(DateTime nowUtc)
    {
        lock (_gate)
        {
            if (_openSessionStart is not { } start)
            {
                return null;
            }

            var session = new FeedSession(start, nowUtc);
            _sessions.Add(session);
            _openSessionStart = null;
            Bump();
            return session;
        }
    }

    // Newest first, at most `max` events matching the filter.
    public IReadOnlyList<LiquidationEvent> GetTape(Func<LiquidationEvent, bool> filter, int max)
    {
        lock (_gate)
        {
            var result = new List<LiquidationEvent>(Math.Min(max, 256));
            for (var node = _events.Last; node is not null && result.Count < max; node = node.Previous)
            {
                if (filter(node.Value))
                {
                    result.Add(node.Value);
                }
            }

            return result;
        }
    }

    // Recomputed at most once per second per version and shared by all viewers.
    public LiquidationSummary GetSummary(DateTime nowUtc)
    {
        var second = ToMs(nowUtc) / 1000;
        var version = Version;
        if (Volatile.Read(ref _summaryCache) is { } cached && cached.Version == version && cached.Second == second)
        {
            return cached.Summary;
        }

        lock (_gate)
        {
            Prune(nowUtc);
            var summary = BuildSummary(nowUtc);
            Volatile.Write(ref _summaryCache, new SummaryCache(version, second, summary));
            return summary;
        }
    }

    private LiquidationSummary BuildSummary(DateTime nowUtc)
    {
        var nowMs = ToMs(nowUtc);
        var hourStart = nowMs - (long)TimeSpan.FromHours(1).TotalMilliseconds;
        var dayStart = nowMs - (long)Retention.TotalMilliseconds;

        var hour = new Accumulator();
        var day = new Accumulator();
        var perSymbol = new Dictionary<string, (decimal Long, decimal Short, int Count)>(StringComparer.Ordinal);

        foreach (var ((symbol, minuteMs, side), (usd, count)) in _minutes)
        {
            // A minute counts towards a window if it ends inside it.
            if (minuteMs + 60_000 <= dayStart)
            {
                continue;
            }

            day.Add(side, usd, count);
            if (minuteMs + 60_000 > hourStart)
            {
                hour.Add(side, usd, count);
            }

            perSymbol.TryGetValue(symbol, out var s);
            perSymbol[symbol] = side == LiquidatedSide.Long
                ? (s.Long + usd, s.Short, s.Count + count)
                : (s.Long, s.Short + usd, s.Count + count);
        }

        LiquidationEvent? largestHour = null;
        LiquidationEvent? largestDay = null;
        foreach (var evt in _events)
        {
            var ms = ToMs(evt.TimeUtc);
            if (ms < dayStart)
            {
                continue;
            }

            if (largestDay is null || evt.UsdValue > largestDay.UsdValue)
            {
                largestDay = evt;
            }

            if (ms >= hourStart && (largestHour is null || evt.UsdValue > largestHour.UsdValue))
            {
                largestHour = evt;
            }
        }

        var sessions = AllSessions(nowUtc);
        var top = perSymbol
            .Select(kv => new SymbolLiquidations(kv.Key, kv.Value.Long, kv.Value.Short, kv.Value.Count))
            .OrderByDescending(s => s.TotalUsd)
            .Take(TopSymbolCount)
            .ToList();

        return new LiquidationSummary(
            new LiquidationWindow(TimeSpan.FromHours(1), hour.Totals, largestHour, Covered(sessions, FromMs(hourStart), nowUtc)),
            new LiquidationWindow(Retention, day.Totals, largestDay, Covered(sessions, FromMs(dayStart), nowUtc)),
            top,
            Pressure(hourStart / 1000),
            sessions.Where(s => s.EndUtc > FromMs(dayStart)).Select(s => (DateTime?)s.StartUtc).FirstOrDefault(),
            Gaps(sessions, FromMs(dayStart)),
            _openSessionStart is not null);
    }

    private List<FeedSession> AllSessions(DateTime nowUtc)
    {
        var sessions = new List<FeedSession>(_sessions);
        if (_openSessionStart is { } start)
        {
            sessions.Add(new FeedSession(start, nowUtc));
        }

        return sessions;
    }

    private static TimeSpan Covered(List<FeedSession> sessions, DateTime from, DateTime to)
    {
        // Sessions don't overlap (one connection at a time), so the overlaps simply add up.
        var total = TimeSpan.Zero;
        foreach (var s in sessions)
        {
            var start = s.StartUtc > from ? s.StartUtc : from;
            var end = s.EndUtc < to ? s.EndUtc : to;
            if (end > start)
            {
                total += end - start;
            }
        }

        return total;
    }

    private static List<FeedGap> Gaps(List<FeedSession> sessions, DateTime from)
    {
        var gaps = new List<FeedGap>();
        for (var i = 1; i < sessions.Count; i++)
        {
            var start = sessions[i - 1].EndUtc;
            var end = sessions[i].StartUtc;
            if (end > from && end - start >= MinGap)
            {
                gaps.Add(new FeedGap(start < from ? from : start, end));
            }
        }

        return gaps;
    }

    private List<SamplingPressure> Pressure(long fromSecond)
    {
        var result = new List<SamplingPressure>();
        foreach (var (symbol, seconds) in _activeSeconds)
        {
            var active = 0;
            var saturated = 0;
            foreach (var second in seconds)
            {
                if (second < fromSecond)
                {
                    continue;
                }

                active++;
                if (seconds.Contains(second - 1))
                {
                    saturated++;
                }
            }

            if (saturated > 0)
            {
                result.Add(new SamplingPressure(symbol, active, saturated));
            }
        }

        return result.OrderByDescending(p => p.SaturatedSeconds).Take(PressureCount).ToList();
    }

    private void Prune(DateTime nowUtc)
    {
        var dayStart = nowUtc - Retention;
        while (_events.First is { } first && first.Value.TimeUtc < dayStart)
        {
            _events.RemoveFirst();
        }

        var dayStartMs = ToMs(dayStart);
        foreach (var key in _minutes.Keys.Where(k => k.MinuteMs + 60_000 <= dayStartMs).ToList())
        {
            _minutes.Remove(key);
        }

        var hourStartSecond = ToMs(nowUtc - TimeSpan.FromHours(1)) / 1000;
        foreach (var (symbol, seconds) in _activeSeconds.ToList())
        {
            seconds.RemoveWhere(s => s < hourStartSecond - 1);
            if (seconds.Count == 0)
            {
                _activeSeconds.Remove(symbol);
            }
        }

        _sessions.RemoveAll(s => s.EndUtc < dayStart);
    }

    private void Bump() => Interlocked.Increment(ref _version);

    private static long ToMs(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static DateTime FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    private sealed record SummaryCache(long Version, long Second, LiquidationSummary Summary);

    private sealed class Accumulator
    {
        private decimal _long;
        private decimal _short;
        private int _longCount;
        private int _shortCount;

        public SideTotals Totals => new(_long, _short, _longCount, _shortCount);

        public void Add(LiquidatedSide side, decimal usd, int count)
        {
            if (side == LiquidatedSide.Long)
            {
                _long += usd;
                _longCount += count;
            }
            else
            {
                _short += usd;
                _shortCount += count;
            }
        }
    }
}
