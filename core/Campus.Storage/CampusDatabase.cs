using Campus.Vault;
using Microsoft.Data.Sqlite;

namespace Campus.Storage;

/// <summary>
/// The workspace database. Encrypted with SQLCipher using a key derived from the vault's master
/// key, so locking the vault makes the database as unreadable as the files beside it.
/// </summary>
public sealed class CampusDatabase : IDisposable, IAsyncDisposable
{
    private readonly string _path;
    private SqliteConnection? _connection;

    static CampusDatabase()
    {
        // Selects the SQLCipher-enabled native provider. Must run before any connection opens.
        SQLitePCL.Batteries_V2.Init();
    }

    public CampusDatabase(string path) => _path = path;

    public bool IsOpen => _connection is { State: System.Data.ConnectionState.Open };

    /// <summary>The live connection. Throws when the database is closed, which is what a locked vault means.</summary>
    public SqliteConnection Connection => _connection
        ?? throw new InvalidOperationException("The workspace database is closed.");

    /// <summary>
    /// Opens the database with the vault's database key and brings the schema up to date.
    /// </summary>
    public async Task OpenAsync(VaultKeyRing keys, CancellationToken ct = default)
    {
        if (IsOpen) return;

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };

        _connection = new SqliteConnection(builder.ToString());
        await _connection.OpenAsync(ct).ConfigureAwait(false);

        // SQLCipher takes the key as a raw blob literal, which avoids a KDF pass over a key that
        // is already a uniformly random 256-bit value.
        var keyLiteral = Convert.ToHexStringLower(keys.DatabaseKey.Span);
        await ExecuteAsync($"PRAGMA key = \"x'{keyLiteral}'\";", ct).ConfigureAwait(false);

        // Everything from here needs to read a real page, so a wrong key surfaces as one clear
        // failure rather than as whichever pragma happened to touch the file first.
        try
        {
            await ExecuteAsync("PRAGMA cipher_memory_security = ON;", ct).ConfigureAwait(false);
            await ExecuteAsync("PRAGMA journal_mode = WAL;", ct).ConfigureAwait(false);
            await ExecuteAsync("PRAGMA synchronous = NORMAL;", ct).ConfigureAwait(false);
            await ExecuteAsync("PRAGMA foreign_keys = ON;", ct).ConfigureAwait(false);
            await ExecuteAsync("PRAGMA busy_timeout = 5000;", ct).ConfigureAwait(false);
            await ExecuteAsync("SELECT count(*) FROM sqlite_master;", ct).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            await CloseAsync().ConfigureAwait(false);
            throw new InvalidOperationException("The workspace database could not be decrypted.", ex);
        }

        await Migrations.ApplyAsync(this, ct).ConfigureAwait(false);
    }

    /// <summary>Closes the connection. Called whenever the vault locks.</summary>
    public async Task CloseAsync()
    {
        if (_connection is null) return;
        await _connection.CloseAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
        _connection = null;
        SqliteConnection.ClearAllPools();
    }

    public SqliteCommand CreateCommand(string sql)
    {
        var command = Connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    public async Task<int> ExecuteAsync(string sql, CancellationToken ct = default)
    {
        await using var command = CreateCommand(sql);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<T?> ScalarAsync<T>(string sql, CancellationToken ct = default)
    {
        await using var command = CreateCommand(sql);
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, typeof(T));
    }

    /// <summary>Runs work inside a transaction, rolling back if it throws.</summary>
    public async Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken ct = default)
    {
        await using var transaction = (SqliteTransaction)await Connection
            .BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await work().ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    public Task InTransactionAsync(Func<Task> work, CancellationToken ct = default)
        => InTransactionAsync<object?>(async () => { await work().ConfigureAwait(false); return null; }, ct);

    /// <summary>Reclaims space and rewrites the file. Offered from Settings, never automatic.</summary>
    public Task CompactAsync(CancellationToken ct = default) => ExecuteAsync("VACUUM;", ct);

    public void Dispose() => CloseAsync().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);
}
