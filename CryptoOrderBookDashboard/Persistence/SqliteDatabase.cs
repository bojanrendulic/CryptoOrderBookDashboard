using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace CryptoOrderBookDashboard.Persistence;

// Opens connections to the dashboard's SQLite file and owns its schema. All writes go through
// PersistenceWriter (one writer, batched transactions); anything may read through OpenAsync,
// which WAL mode lets run alongside the writer.
public sealed class SqliteDatabase
{
    private readonly string _connectionString;

    public SqliteDatabase(IOptions<PersistenceOptions> options, IHostEnvironment environment)
        : this(ResolvePath(options.Value.DatabasePath, environment.ContentRootPath))
    {
    }

    public SqliteDatabase(string databasePath)
    {
        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
    }

    public string DatabasePath { get; }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        // Per-connection settings. NORMAL is durable across application crashes in WAL mode; an
        // OS crash or power loss can lose the last few commits but never corrupts the file.
        await connection.ExecuteAsync("PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 5000;");
        return connection;
    }

    // Creates the file and brings the schema to the latest version. Safe to call repeatedly.
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(DatabasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = await OpenAsync(ct);

        // auto_vacuum only takes effect on an empty database, so set it before any table exists.
        // WAL is persistent in the file: readers never block the writer and vice versa.
        await connection.ExecuteAsync("PRAGMA auto_vacuum = INCREMENTAL; PRAGMA journal_mode = WAL;");
        await Migrations.ApplyAsync(connection, ct);
    }

    private static string ResolvePath(string path, string contentRoot) =>
        Path.IsPathRooted(path) ? path : Path.Combine(contentRoot, path);
}
