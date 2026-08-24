using Campus.Domain;

namespace Campus.Storage;

/// <summary>
/// The change log.
///
/// Sync ships the entries a peer has not seen rather than comparing two whole workspaces, which
/// is what makes syncing a term's work over a cable finish in seconds instead of minutes. The log
/// is append-only and ordered by a single counter, so "what has this device missed" is one number.
/// </summary>
public sealed class JournalRepository(CampusDatabase database, string deviceId)
{
    private readonly CampusDatabase _db = database;
    private readonly string _deviceId = deviceId;

    public async Task<long> MaxSequenceAsync(CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("SELECT COALESCE(MAX(seq), 0) FROM journal;");
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }

    /// <summary>
    /// Everything after <paramref name="afterSequence"/>, oldest first.
    ///
    /// Entries this device received from the peer are skipped when sending back to that same
    /// peer: replaying someone's own change at them is at best wasted bytes and at worst a loop.
    /// </summary>
    public async Task<IReadOnlyList<JournalEntry>> ReadSinceAsync(
        long afterSequence, string? excludeDeviceId = null, int limit = 10_000,
        CancellationToken ct = default)
    {
        var filter = excludeDeviceId is null ? "" : " AND device_id <> @exclude";

        await using var command = _db.CreateCommand($"""
            SELECT seq, operation, entity_type, entity_id, at, device_id, snapshot, content_hash
            FROM journal
            WHERE seq > @after{filter}
            ORDER BY seq
            LIMIT @limit;
            """);

        command.Parameters.AddWithValue("@after", afterSequence);
        command.Parameters.AddWithValue("@limit", limit);
        if (excludeDeviceId is not null) command.Parameters.AddWithValue("@exclude", excludeDeviceId);

        var entries = new List<JournalEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            entries.Add(new JournalEntry
            {
                Sequence = reader.GetInt64(0),
                Operation = (ChangeOperation)reader.GetInt32(1),
                EntityType = reader.GetString(2),
                EntityId = CampusId.Parse(reader.GetString(3)),
                At = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                DeviceId = reader.GetString(5),
                Snapshot = reader.IsDBNull(6) ? null : reader.GetString(6),
                ContentHash = reader.IsDBNull(7) ? null : reader.GetString(7),
            });
        }

        return entries;
    }

    /// <summary>
    /// Writes an entry that arrived from a peer, keeping the originating device's name on it so
    /// it is never sent back to them.
    /// </summary>
    public async Task AppendAsync(JournalEntry entry, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            INSERT INTO journal (operation, entity_type, entity_id, at, device_id, snapshot, content_hash)
            VALUES (@op, @type, @id, @at, @device, @snapshot, @hash);
            """);

        command.Parameters.AddWithValue("@op", (int)entry.Operation);
        command.Parameters.AddWithValue("@type", entry.EntityType);
        command.Parameters.AddWithValue("@id", entry.EntityId.Value);
        command.Parameters.AddWithValue("@at", entry.At.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@device", entry.DeviceId.Length > 0 ? entry.DeviceId : _deviceId);
        command.Parameters.AddWithValue("@snapshot", (object?)entry.Snapshot ?? DBNull.Value);
        command.Parameters.AddWithValue("@hash", (object?)entry.ContentHash ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>How many entries a peer has not seen yet.</summary>
    public async Task<int> PendingForAsync(
        string peerDeviceId, long acknowledged, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            SELECT COUNT(*) FROM journal WHERE seq > @after AND device_id <> @peer;
            """);
        command.Parameters.AddWithValue("@after", acknowledged);
        command.Parameters.AddWithValue("@peer", peerDeviceId);

        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null or DBNull ? 0 : Convert.ToInt32(value);
    }

    /// <summary>
    /// Drops entries every paired device has already acknowledged. The journal is a transport
    /// log, not the history — history lives in its own table and is not pruned by this.
    /// </summary>
    public async Task<int> PruneAcknowledgedAsync(CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            DELETE FROM journal
            WHERE seq <= COALESCE((SELECT MIN(last_ack_seq) FROM devices WHERE trusted = 1), 0);
            """);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
