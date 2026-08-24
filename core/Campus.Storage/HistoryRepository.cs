using Campus.Domain;
using Microsoft.Data.Sqlite;

namespace Campus.Storage;

/// <summary>
/// What happened to an object, and what it used to contain.
///
/// Two related things live here because they answer the same question from different distances:
/// history says "this was edited on Tuesday", versions can put Tuesday's text back. History is
/// cheap and kept for everything; versions cost a vault object each and are kept for content that
/// can be lost by overwriting.
/// </summary>
public sealed class HistoryRepository(CampusDatabase database, string deviceId)
{
    private readonly CampusDatabase _db = database;
    private readonly string _deviceId = deviceId;

    // ---------------------------------------------------------------------- history

    public async Task RecordAsync(
        CampusId objectId, string action, string? detail = null, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            INSERT INTO history (id, object_id, action, detail, at, device_id)
            VALUES (@id, @object, @action, @detail, @at, @device);
            """);

        command.Parameters.AddWithValue("@id", CampusId.New().Value);
        command.Parameters.AddWithValue("@object", objectId.Value);
        command.Parameters.AddWithValue("@action", action);
        command.Parameters.AddWithValue("@detail", (object?)detail ?? DBNull.Value);
        command.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@device", _deviceId);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<HistoryEntry>> ForObjectAsync(
        CampusId objectId, int limit = 100, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            SELECT * FROM history WHERE object_id = @id ORDER BY at DESC LIMIT @limit;
            """);
        command.Parameters.AddWithValue("@id", objectId.Value);
        command.Parameters.AddWithValue("@limit", limit);

        var entries = new List<HistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            entries.Add(new HistoryEntry
            {
                Id = CampusId.Parse(reader.GetString(reader.GetOrdinal("id"))),
                ObjectId = objectId,
                Action = reader.GetString(reader.GetOrdinal("action")),
                Detail = Nullable(reader, "detail"),
                At = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("at"))),
                DeviceId = Nullable(reader, "device_id"),
            });
        }

        return entries;
    }

    // --------------------------------------------------------------------- versions

    /// <summary>
    /// Records a retained snapshot. The bytes are already in the vault under
    /// <paramref name="contentHash"/>; this only records that they were once the current text.
    /// </summary>
    public async Task<int> AddVersionAsync(
        CampusId objectId, string contentHash, long sizeBytes,
        string? label = null, CancellationToken ct = default)
    {
        return await _db.InTransactionAsync(async () =>
        {
            var next = 1;
            await using (var query = _db.CreateCommand(
                "SELECT COALESCE(MAX(version_number), 0) + 1 FROM versions WHERE object_id = @id;"))
            {
                query.Parameters.AddWithValue("@id", objectId.Value);
                var value = await query.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (value is not (null or DBNull)) next = Convert.ToInt32(value);
            }

            await using var insert = _db.CreateCommand("""
                INSERT INTO versions
                    (id, object_id, version_number, content_hash, size_bytes, created_at, label)
                VALUES (@id, @object, @number, @hash, @size, @created, @label);
                """);

            insert.Parameters.AddWithValue("@id", CampusId.New().Value);
            insert.Parameters.AddWithValue("@object", objectId.Value);
            insert.Parameters.AddWithValue("@number", next);
            insert.Parameters.AddWithValue("@hash", contentHash);
            insert.Parameters.AddWithValue("@size", sizeBytes);
            insert.Parameters.AddWithValue("@created", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            insert.Parameters.AddWithValue("@label", (object?)label ?? DBNull.Value);

            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return next;
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ObjectVersion>> VersionsAsync(
        CampusId objectId, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            SELECT * FROM versions WHERE object_id = @id ORDER BY version_number DESC;
            """);
        command.Parameters.AddWithValue("@id", objectId.Value);

        var versions = new List<ObjectVersion>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            versions.Add(new ObjectVersion
            {
                Id = CampusId.Parse(reader.GetString(reader.GetOrdinal("id"))),
                ObjectId = objectId,
                VersionNumber = reader.GetInt32(reader.GetOrdinal("version_number")),
                ContentHash = reader.GetString(reader.GetOrdinal("content_hash")),
                SizeBytes = reader.GetInt64(reader.GetOrdinal("size_bytes")),
                CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(
                    reader.GetInt64(reader.GetOrdinal("created_at"))),
                Label = Nullable(reader, "label"),
            });
        }

        return versions;
    }

    /// <summary>
    /// Drops the oldest versions past a limit and reports the hashes that nothing references any
    /// more, so the vault can reclaim them. Keeping every keystroke forever would turn a term of
    /// note-taking into gigabytes.
    /// </summary>
    public async Task<IReadOnlyList<string>> PruneAsync(
        CampusId objectId, int keep = 20, CancellationToken ct = default)
    {
        var dropped = new List<string>();

        await _db.InTransactionAsync(async () =>
        {
            await using (var query = _db.CreateCommand("""
                SELECT content_hash FROM versions
                WHERE object_id = @id
                  AND version_number <= (
                      SELECT MAX(version_number) - @keep FROM versions WHERE object_id = @id);
                """))
            {
                query.Parameters.AddWithValue("@id", objectId.Value);
                query.Parameters.AddWithValue("@keep", keep);

                await using var reader = await query.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false)) dropped.Add(reader.GetString(0));
            }

            if (dropped.Count == 0) return;

            await using var delete = _db.CreateCommand("""
                DELETE FROM versions
                WHERE object_id = @id
                  AND version_number <= (
                      SELECT MAX(version_number) - @keep FROM versions WHERE object_id = @id);
                """);
            delete.Parameters.AddWithValue("@id", objectId.Value);
            delete.Parameters.AddWithValue("@keep", keep);
            await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        // A hash still used by a live version or by an object's payload must not be collected.
        var orphaned = new List<string>();
        foreach (var hash in dropped.Distinct(StringComparer.Ordinal))
        {
            await using var check = _db.CreateCommand("""
                SELECT
                    (SELECT COUNT(*) FROM versions WHERE content_hash = @hash)
                  + (SELECT COUNT(*) FROM objects
                     WHERE json_extract(payload, '$.contentHash') = @hash);
                """);
            check.Parameters.AddWithValue("@hash", hash);
            var value = await check.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (value is not (null or DBNull) && Convert.ToInt32(value) == 0) orphaned.Add(hash);
        }

        return orphaned;
    }

    private static string? Nullable(SqliteDataReader reader, string column)
    {
        var index = reader.GetOrdinal(column);
        return reader.IsDBNull(index) ? null : reader.GetString(index);
    }
}
