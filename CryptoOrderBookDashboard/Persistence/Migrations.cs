using Dapper;
using Microsoft.Data.Sqlite;

namespace CryptoOrderBookDashboard.Persistence;

// Numbered schema scripts tracked by PRAGMA user_version. Append new versions at the end;
// never edit one that has shipped, because existing databases have already run it.
//
// Conventions: times are INTEGER unix milliseconds (UTC); prices, quantities and USD values are
// REAL (double, ~15 significant digits - plenty for analytics, and SUM/ORDER BY stay fast).
public static class Migrations
{
    private static readonly string[] Scripts =
    [
        // 1: liquidations
        """
        -- Individual liquidations at or above Liquidations:MinPersistUsd. The stream has no id,
        -- so the natural key makes re-inserting the same event harmless.
        CREATE TABLE liquidations (
            id         INTEGER PRIMARY KEY,
            symbol     TEXT    NOT NULL,
            side       TEXT    NOT NULL CHECK (side IN ('Long', 'Short')),
            time_ms    INTEGER NOT NULL,
            price      REAL    NOT NULL CHECK (price > 0),
            quantity   REAL    NOT NULL CHECK (quantity > 0),
            usd        REAL    NOT NULL CHECK (usd >= 0),
            UNIQUE (symbol, time_ms, side, price, quantity)
        );
        CREATE INDEX ix_liquidations_time ON liquidations (time_ms);

        -- Per-minute totals of every received liquidation (not just the large ones), so the
        -- 1h/24h figures survive a restart. Written as absolute values (last write wins), which
        -- keeps them idempotent.
        CREATE TABLE liquidation_minutes (
            symbol     TEXT    NOT NULL,
            minute_ms  INTEGER NOT NULL,
            side       TEXT    NOT NULL CHECK (side IN ('Long', 'Short')),
            usd        REAL    NOT NULL CHECK (usd >= 0),
            count      INTEGER NOT NULL CHECK (count >= 0),
            PRIMARY KEY (symbol, minute_ms, side)
        ) WITHOUT ROWID;
        CREATE INDEX ix_liquidation_minutes_time ON liquidation_minutes (minute_ms);

        -- When a feed was actually receiving data: one row per connected session, its end moved
        -- forward by a heartbeat. The holes between sessions are gaps (disconnects, restarts).
        CREATE TABLE feed_sessions (
            feed       TEXT    NOT NULL,
            start_ms   INTEGER NOT NULL,
            end_ms     INTEGER NOT NULL CHECK (end_ms >= start_ms),
            PRIMARY KEY (feed, start_ms)
        ) WITHOUT ROWID;
        """,
    ];

    public static int LatestVersion => Scripts.Length;

    public static async Task ApplyAsync(SqliteConnection connection, CancellationToken ct = default)
    {
        var version = await connection.ExecuteScalarAsync<int>("PRAGMA user_version;");
        for (var next = version + 1; next <= Scripts.Length; next++)
        {
            ct.ThrowIfCancellationRequested();

            // Script and version bump commit together, so a failed migration leaves the
            // database exactly as it was.
            await using var tx = await connection.BeginTransactionAsync(ct);
            await connection.ExecuteAsync(Scripts[next - 1], transaction: tx);
            await connection.ExecuteAsync($"PRAGMA user_version = {next};", transaction: tx);
            await tx.CommitAsync(ct);
        }
    }
}
