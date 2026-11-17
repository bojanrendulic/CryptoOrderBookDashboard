using CryptoOrderBookDashboard.Liquidations;
using CryptoOrderBookDashboard.Persistence;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CryptoOrderBookDashboard.Tests;

public sealed class PersistenceTests
{
    private static readonly DateTime T0 = new(2026, 11, 17, 10, 0, 0, DateTimeKind.Utc);

    private static LiquidationEvent Liq(string symbol, decimal usd, DateTime time, LiquidatedSide side = LiquidatedSide.Long) =>
        new(symbol, side, 100m, usd / 100m, usd, time);

    [Fact]
    public async Task Initialize_creates_schema_in_wal_mode_and_is_idempotent()
    {
        await using var db = await TestDatabase.CreateAsync();
        await db.Database.InitializeAsync(); // second run must be a no-op

        await using var connection = await db.Database.OpenAsync();
        Assert.Equal(Migrations.LatestVersion, await connection.ExecuteScalarAsync<int>("PRAGMA user_version;"));
        Assert.Equal("wal", await connection.ExecuteScalarAsync<string>("PRAGMA journal_mode;"));

        var tables = (await connection.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type = 'table'")).ToHashSet();
        Assert.Contains("liquidations", tables);
        Assert.Contains("liquidation_minutes", tables);
        Assert.Contains("feed_sessions", tables);
    }

    [Fact]
    public async Task Writer_commits_queued_writes_and_ignores_duplicates()
    {
        await using var db = await TestDatabase.CreateAsync();
        var writer = db.CreateWriter(batchSize: 7);

        for (var i = 0; i < 20; i++)
        {
            Assert.True(writer.TryEnqueue(LiquidationRepository.InsertEvent(Liq("BTCUSDT", 250_000m + i, T0.AddSeconds(i)))));
        }

        // The same event again: the natural key makes it a no-op, not a duplicate row.
        writer.TryEnqueue(LiquidationRepository.InsertEvent(Liq("BTCUSDT", 250_000m, T0)));
        await writer.DrainAsync();

        await using var connection = await db.Database.OpenAsync();
        Assert.Equal(20, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM liquidations"));
        Assert.Equal(0, writer.Failed);
    }

    [Fact]
    public async Task One_rejected_write_does_not_lose_the_rest_of_its_batch()
    {
        await using var db = await TestDatabase.CreateAsync();
        var writer = db.CreateWriter();

        writer.TryEnqueue(LiquidationRepository.InsertEvent(Liq("BTCUSDT", 300_000m, T0)));
        // Price 0 violates CHECK (price > 0): the batch rolls back and is retried row by row.
        writer.TryEnqueue(LiquidationRepository.InsertEvent(new LiquidationEvent("BADUSDT", LiquidatedSide.Long, 0m, 1m, 0m, T0)));
        writer.TryEnqueue(LiquidationRepository.InsertEvent(Liq("ETHUSDT", 400_000m, T0)));
        await writer.DrainAsync();

        await using var connection = await db.Database.OpenAsync();
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM liquidations"));
        Assert.Equal(1, writer.Failed);
    }

    [Fact]
    public async Task Full_queue_drops_and_counts_instead_of_blocking()
    {
        await using var db = await TestDatabase.CreateAsync();
        var writer = db.CreateWriter(queueCapacity: 3);

        var accepted = Enumerable.Range(0, 5)
            .Count(i => writer.TryEnqueue(LiquidationRepository.InsertEvent(Liq("BTCUSDT", 300_000m, T0.AddSeconds(i)))));

        Assert.Equal(3, accepted);
        Assert.Equal(2, writer.Dropped);
    }

    [Fact]
    public async Task Minute_and_session_upserts_are_idempotent()
    {
        await using var db = await TestDatabase.CreateAsync();
        var writer = db.CreateWriter();

        // Absolute values: the last write wins, replays don't double count.
        writer.TryEnqueue(LiquidationRepository.UpsertMinute(new LiquidationMinute("BTCUSDT", T0, LiquidatedSide.Long, 1000m, 1)));
        writer.TryEnqueue(LiquidationRepository.UpsertMinute(new LiquidationMinute("BTCUSDT", T0, LiquidatedSide.Long, 3500m, 2)));
        writer.TryEnqueue(LiquidationRepository.UpsertMinute(new LiquidationMinute("BTCUSDT", T0, LiquidatedSide.Long, 3500m, 2)));

        // A session's end only moves forward, even if an older heartbeat arrives late.
        writer.TryEnqueue(LiquidationRepository.UpsertSession(new FeedSession(T0, T0.AddMinutes(5))));
        writer.TryEnqueue(LiquidationRepository.UpsertSession(new FeedSession(T0, T0.AddMinutes(2))));
        await writer.DrainAsync();

        var (_, minutes, sessions) = await new LiquidationRepository(db.Database).LoadSinceAsync(T0.AddMinutes(-1));
        var minute = Assert.Single(minutes);
        Assert.Equal(3500m, minute.Usd);
        Assert.Equal(2, minute.Count);
        Assert.Equal(T0.AddMinutes(5), Assert.Single(sessions).EndUtc);
    }

    [Fact]
    public async Task Repository_round_trips_events()
    {
        await using var db = await TestDatabase.CreateAsync();
        var writer = db.CreateWriter();
        var evt = new LiquidationEvent("SOLUSDT", LiquidatedSide.Short, 71.55m, 3000m, 214_650m, T0.AddSeconds(5));
        writer.TryEnqueue(LiquidationRepository.InsertEvent(evt));
        writer.TryEnqueue(LiquidationRepository.InsertEvent(Liq("OLDUSDT", 500_000m, T0.AddDays(-2))));
        await writer.DrainAsync();

        var (events, _, _) = await new LiquidationRepository(db.Database).LoadSinceAsync(T0);
        var loaded = Assert.Single(events);
        Assert.Equal(evt, loaded);
    }

    [Fact]
    public async Task Retention_deletes_only_rows_older_than_the_limit()
    {
        await using var db = await TestDatabase.CreateAsync();
        var writer = db.CreateWriter();
        for (var day = 0; day < 10; day++)
        {
            writer.TryEnqueue(LiquidationRepository.InsertEvent(Liq("BTCUSDT", 300_000m, T0.AddDays(-day))));
            writer.TryEnqueue(LiquidationRepository.UpsertMinute(new LiquidationMinute("BTCUSDT", T0.AddDays(-day), LiquidatedSide.Long, 1m, 1)));
        }

        await writer.DrainAsync();

        var retention = new RetentionService(
            db.Database,
            Options.Create(new PersistenceOptions { RetentionDays = 5 }),
            NullLogger<RetentionService>.Instance);
        var deleted = await retention.PurgeAsync(new DateTimeOffset(T0), CancellationToken.None);

        await using var connection = await db.Database.OpenAsync();
        // Days 0..4 are inside the limit (day 5 is exactly at the cutoff and is kept).
        Assert.Equal(6, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM liquidations"));
        Assert.Equal(6, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM liquidation_minutes"));
        Assert.Equal(8, deleted);
    }
}
