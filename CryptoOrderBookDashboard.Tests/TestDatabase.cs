using CryptoOrderBookDashboard.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CryptoOrderBookDashboard.Tests;

// A real SQLite file in the temp folder (not :memory:), so WAL, pooling and the writer's own
// connections behave exactly as in the app. Deleted on dispose.
public sealed class TestDatabase : IAsyncDisposable
{
    private TestDatabase(string path)
    {
        Path = path;
        Database = new SqliteDatabase(path);
    }

    public string Path { get; }

    public SqliteDatabase Database { get; }

    public static async Task<TestDatabase> CreateAsync()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cobd-test-{Guid.NewGuid():N}.db");
        var db = new TestDatabase(path);
        await db.Database.InitializeAsync();
        return db;
    }

    public PersistenceWriter CreateWriter(int batchSize = 500, int queueCapacity = 50_000) =>
        new(Database, Options.Create(new PersistenceOptions { BatchSize = batchSize, QueueCapacity = queueCapacity }), NullLogger<PersistenceWriter>.Instance);

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { Path, Path + "-wal", Path + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // Best effort; it's a temp file.
            }
        }

        return ValueTask.CompletedTask;
    }
}
