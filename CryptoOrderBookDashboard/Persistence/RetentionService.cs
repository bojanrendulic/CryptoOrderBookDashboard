using Dapper;
using Microsoft.Extensions.Options;

namespace CryptoOrderBookDashboard.Persistence;

// Deletes rows older than Persistence:RetentionDays once a day, in small chunks so the single
// writer is never blocked for long, then returns the freed pages to the OS.
public sealed class RetentionService(
    SqliteDatabase database,
    IOptions<PersistenceOptions> options,
    ILogger<RetentionService> logger) : BackgroundService
{
    private const int ChunkSize = 5000;

    // Every time-stamped table and its time column. Add new tables here.
    private static readonly (string Table, string TimeColumn)[] Tables =
    [
        ("liquidations", "time_ms"),
        ("liquidation_minutes", "minute_ms"),
        ("feed_sessions", "end_ms"),
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.RetentionDays <= 0)
        {
            return; // keep forever
        }

        try
        {
            // First pass shortly after startup, then daily.
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await PurgeAsync(DateTimeOffset.UtcNow, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Retention purge failed");
                }

                await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal async Task<long> PurgeAsync(DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now.AddDays(-options.Value.RetentionDays).ToUnixTimeMilliseconds();
        long total = 0;

        await using var connection = await database.OpenAsync(ct);
        foreach (var (table, column) in Tables)
        {
            int deleted;
            do
            {
                ct.ThrowIfCancellationRequested();

                // Bounded chunks: each DELETE is its own short transaction.
                deleted = await connection.ExecuteAsync(
                    $"DELETE FROM {table} WHERE {column} < @cutoff AND {column} <= " +
                    $"(SELECT MAX({column}) FROM (SELECT {column} FROM {table} WHERE {column} < @cutoff ORDER BY {column} LIMIT {ChunkSize}))",
                    new { cutoff });
                total += deleted;
            }
            while (deleted > 0);
        }

        if (total > 0)
        {
            await connection.ExecuteAsync("PRAGMA incremental_vacuum;");
            logger.LogInformation("Retention removed {Count} row(s) older than {Days} days", total, options.Value.RetentionDays);
        }

        return total;
    }
}
