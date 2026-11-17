using System.Threading.Channels;
using Dapper;
using Microsoft.Extensions.Options;

namespace CryptoOrderBookDashboard.Persistence;

// One statement to run with one parameter object. Writes with the same Sql in a batch are sent
// together in a single Dapper call.
public sealed record PersistWrite(string Sql, object Parameters);

// The single writer for the database. Feeds call TryEnqueue from their socket handlers and never
// wait on disk; this service commits queued writes in batched transactions (all-or-nothing per
// batch), because SQLite pays one disk sync per commit, not per row.
//
// Durability boundary: a write is durable once its batch commits. Up to one flush interval of
// queued writes lives only in memory and is lost if the process dies; on a normal shutdown the
// queue is drained first. Writes are expected to be idempotent (ON CONFLICT DO NOTHING,
// absolute-value upserts), so replaying one is always harmless.
public sealed class PersistenceWriter : BackgroundService
{
    private readonly SqliteDatabase _database;
    private readonly PersistenceOptions _options;
    private readonly ILogger<PersistenceWriter> _logger;
    private readonly Channel<PersistWrite> _queue;

    private long _written;
    private long _dropped;
    private long _failed;

    public PersistenceWriter(SqliteDatabase database, IOptions<PersistenceOptions> options, ILogger<PersistenceWriter> logger)
    {
        _database = database;
        _options = options.Value;
        _logger = logger;
        _queue = Channel.CreateBounded<PersistWrite>(new BoundedChannelOptions(Math.Max(1, _options.QueueCapacity))
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait, // with TryWrite: returns false instead of blocking
        });
    }

    public long Written => Interlocked.Read(ref _written);

    // Writes refused because the queue was full (the database could not keep up).
    public long Dropped => Interlocked.Read(ref _dropped);

    // Writes the database rejected (e.g. a CHECK constraint) even when retried on their own.
    public long Failed => Interlocked.Read(ref _failed);

    public int Pending => _queue.Reader.Count;

    // Never blocks. Returns false (and counts the write as dropped) if the queue is full.
    public bool TryEnqueue(PersistWrite write)
    {
        if (_queue.Writer.TryWrite(write))
        {
            return true;
        }

        if (Interlocked.Increment(ref _dropped) % 1000 == 1)
        {
            _logger.LogWarning("Persistence queue full; {Dropped} write(s) dropped so far", Dropped);
        }

        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = _queue.Reader;
        var batch = new List<PersistWrite>(_options.BatchSize);

        try
        {
            while (await reader.WaitToReadAsync(stoppingToken))
            {
                await FillBatchAsync(reader, batch, stoppingToken);
                await WriteBatchAsync(batch, CancellationToken.None);
                batch.Clear();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down - fall through and drain.
        }

        await DrainAsync(batch);
        _logger.LogInformation(
            "Persistence writer stopped: {Written} written, {Dropped} dropped, {Failed} failed",
            Written, Dropped, Failed);
    }

    // Feeds stop before the writer (hosted services stop in reverse registration order), so
    // whatever is queued now is final. Commit it so a normal shutdown loses nothing.
    internal async Task DrainAsync(List<PersistWrite>? batch = null)
    {
        batch ??= [];
        while (_queue.Reader.TryRead(out var write))
        {
            batch.Add(write);
            if (batch.Count >= _options.BatchSize)
            {
                await WriteBatchAsync(batch, CancellationToken.None);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await WriteBatchAsync(batch, CancellationToken.None);
            batch.Clear();
        }
    }

    // Takes writes until the batch is full or the flush interval since the first one has passed.
    private async Task FillBatchAsync(ChannelReader<PersistWrite> reader, List<PersistWrite> batch, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + _options.FlushIntervalMilliseconds;
        while (batch.Count < _options.BatchSize)
        {
            if (reader.TryRead(out var write))
            {
                batch.Add(write);
                continue;
            }

            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
            {
                return;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(remaining));
            try
            {
                if (!await reader.WaitToReadAsync(timeout.Token))
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return; // flush interval elapsed
            }
        }
    }

    internal async Task WriteBatchAsync(IReadOnlyList<PersistWrite> batch, CancellationToken ct)
    {
        if (batch.Count == 0)
        {
            return;
        }

        try
        {
            await using var connection = await _database.OpenAsync(ct);
            await using var tx = await connection.BeginTransactionAsync(ct);
            foreach (var group in batch.GroupBy(w => w.Sql))
            {
                await connection.ExecuteAsync(group.Key, group.Select(w => w.Parameters), tx);
            }

            await tx.CommitAsync(ct);
            Interlocked.Add(ref _written, batch.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The whole batch rolled back. Retry each write on its own so one bad row does not
            // cost the other few hundred.
            _logger.LogWarning(ex, "Batch of {Count} write(s) failed; retrying individually", batch.Count);
            await WriteIndividuallyAsync(batch, ct);
        }
    }

    private async Task WriteIndividuallyAsync(IReadOnlyList<PersistWrite> batch, CancellationToken ct)
    {
        foreach (var write in batch)
        {
            try
            {
                await using var connection = await _database.OpenAsync(ct);
                await connection.ExecuteAsync(write.Sql, write.Parameters);
                Interlocked.Increment(ref _written);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Increment(ref _failed);
                _logger.LogError(ex, "Write rejected by the database: {Sql}", write.Sql);
            }
        }
    }
}
