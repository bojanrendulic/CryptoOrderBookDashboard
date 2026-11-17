using CryptoOrderBookDashboard.Persistence;
using Dapper;

namespace CryptoOrderBookDashboard.Liquidations;

// SQL for liquidations. Writes are built here and committed by PersistenceWriter; reads go
// straight to the database. All writes are idempotent: events by their natural key, minute
// buckets and feed sessions as absolute values.
//
// Duplicates use ON CONFLICT DO NOTHING, never INSERT OR IGNORE: OR IGNORE also silently skips
// rows that violate CHECK / NOT NULL, which would hide bad data instead of reporting it.
public sealed class LiquidationRepository(SqliteDatabase database)
{
    public const string FeedName = "liquidations";

    private const string InsertEventSql =
        """
        INSERT INTO liquidations (symbol, side, time_ms, price, quantity, usd)
        VALUES (@Symbol, @Side, @TimeMs, @Price, @Quantity, @Usd)
        ON CONFLICT DO NOTHING
        """;

    private const string UpsertMinuteSql =
        """
        INSERT INTO liquidation_minutes (symbol, minute_ms, side, usd, count)
        VALUES (@Symbol, @MinuteMs, @Side, @Usd, @Count)
        ON CONFLICT (symbol, minute_ms, side) DO UPDATE SET usd = excluded.usd, count = excluded.count
        """;

    private const string UpsertSessionSql =
        """
        INSERT INTO feed_sessions (feed, start_ms, end_ms)
        VALUES (@Feed, @StartMs, @EndMs)
        ON CONFLICT (feed, start_ms) DO UPDATE SET end_ms = MAX(end_ms, excluded.end_ms)
        """;

    // Doubles, not decimals: Microsoft.Data.Sqlite would store decimal as TEXT.
    public static PersistWrite InsertEvent(LiquidationEvent evt) => new(InsertEventSql, new
    {
        evt.Symbol,
        Side = evt.Side.ToString(),
        TimeMs = ToMs(evt.TimeUtc),
        Price = (double)evt.Price,
        Quantity = (double)evt.Quantity,
        Usd = (double)evt.UsdValue,
    });

    public static PersistWrite UpsertMinute(LiquidationMinute minute) => new(UpsertMinuteSql, new
    {
        minute.Symbol,
        MinuteMs = ToMs(minute.MinuteUtc),
        Side = minute.Side.ToString(),
        Usd = (double)minute.Usd,
        minute.Count,
    });

    public static PersistWrite UpsertSession(FeedSession session) => new(UpsertSessionSql, new
    {
        Feed = FeedName,
        StartMs = ToMs(session.StartUtc),
        EndMs = ToMs(session.EndUtc),
    });

    public async Task<(List<LiquidationEvent> Events, List<LiquidationMinute> Minutes, List<FeedSession> Sessions)> LoadSinceAsync(
        DateTime sinceUtc,
        CancellationToken ct = default)
    {
        var sinceMs = ToMs(sinceUtc);
        await using var connection = await database.OpenAsync(ct);

        var events = (await connection.QueryAsync<EventRow>(
                "SELECT symbol, side, time_ms AS TimeMs, price, quantity, usd FROM liquidations WHERE time_ms >= @sinceMs ORDER BY time_ms",
                new { sinceMs }))
            .Select(r => new LiquidationEvent(r.Symbol, Enum.Parse<LiquidatedSide>(r.Side), (decimal)r.Price, (decimal)r.Quantity, (decimal)r.Usd, FromMs(r.TimeMs)))
            .ToList();

        var minutes = (await connection.QueryAsync<MinuteRow>(
                "SELECT symbol, minute_ms AS MinuteMs, side, usd, count FROM liquidation_minutes WHERE minute_ms >= @sinceMs",
                new { sinceMs = sinceMs - 60_000 }))
            .Select(r => new LiquidationMinute(r.Symbol, FromMs(r.MinuteMs), Enum.Parse<LiquidatedSide>(r.Side), (decimal)r.Usd, (int)r.Count))
            .ToList();

        var sessions = (await connection.QueryAsync<SessionRow>(
                "SELECT start_ms AS StartMs, end_ms AS EndMs FROM feed_sessions WHERE feed = @FeedName AND end_ms >= @sinceMs ORDER BY start_ms",
                new { FeedName, sinceMs }))
            .Select(r => new FeedSession(FromMs(r.StartMs), FromMs(r.EndMs)))
            .ToList();

        return (events, minutes, sessions);
    }

    private static long ToMs(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static DateTime FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    private sealed record EventRow(string Symbol, string Side, long TimeMs, double Price, double Quantity, double Usd);

    private sealed record MinuteRow(string Symbol, long MinuteMs, string Side, double Usd, long Count);

    private sealed record SessionRow(long StartMs, long EndMs);
}
