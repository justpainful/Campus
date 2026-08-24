using Campus.Domain;

namespace Campus.Storage;

/// <summary>
/// The devices this workspace is paired with, and how far each of them has caught up.
///
/// Pairing is deliberately explicit and per-device. There is no account, no server and nothing
/// that discovers you — two devices know about each other because somebody put them together
/// once and confirmed it.
/// </summary>
public sealed class DeviceRepository(CampusDatabase database)
{
    private readonly CampusDatabase _db = database;

    public async Task<IReadOnlyList<PairedDevice>> AllAsync(CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand(
            "SELECT * FROM devices ORDER BY paired_at;");

        var devices = new List<PairedDevice>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var lastSeen = reader.GetOrdinal("last_seen_at");

            devices.Add(new PairedDevice
            {
                DeviceId = reader.GetString(reader.GetOrdinal("device_id")),
                DisplayName = reader.GetString(reader.GetOrdinal("display_name")),
                Platform = (DevicePlatform)reader.GetInt32(reader.GetOrdinal("platform")),
                PublicKey = reader.GetString(reader.GetOrdinal("public_key")),
                PairedAt = DateTimeOffset.FromUnixTimeMilliseconds(
                    reader.GetInt64(reader.GetOrdinal("paired_at"))),
                LastSeenAt = reader.IsDBNull(lastSeen)
                    ? null
                    : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(lastSeen)),
                LastAcknowledgedSequence = reader.GetInt64(reader.GetOrdinal("last_ack_seq")),
                Trusted = reader.GetInt32(reader.GetOrdinal("trusted")) != 0,
            });
        }

        return devices;
    }

    public async Task<PairedDevice?> GetAsync(string deviceId, CancellationToken ct = default)
        => (await AllAsync(ct).ConfigureAwait(false))
            .FirstOrDefault(d => d.DeviceId == deviceId);

    public async Task SaveAsync(PairedDevice device, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            INSERT INTO devices
                (device_id, display_name, platform, public_key, paired_at,
                 last_seen_at, last_ack_seq, trusted)
            VALUES (@id, @name, @platform, @key, @paired, @seen, @ack, @trusted)
            ON CONFLICT(device_id) DO UPDATE SET
                display_name = excluded.display_name,
                platform = excluded.platform,
                public_key = excluded.public_key,
                last_seen_at = excluded.last_seen_at,
                last_ack_seq = MAX(devices.last_ack_seq, excluded.last_ack_seq),
                trusted = excluded.trusted;
            """);

        command.Parameters.AddWithValue("@id", device.DeviceId);
        command.Parameters.AddWithValue("@name", device.DisplayName);
        command.Parameters.AddWithValue("@platform", (int)device.Platform);
        command.Parameters.AddWithValue("@key", device.PublicKey);
        command.Parameters.AddWithValue("@paired", device.PairedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@seen",
            device.LastSeenAt is { } seen ? seen.ToUnixTimeMilliseconds() : DBNull.Value);
        command.Parameters.AddWithValue("@ack", device.LastAcknowledgedSequence);
        command.Parameters.AddWithValue("@trusted", device.Trusted ? 1 : 0);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Records that a peer has received everything up to a point.</summary>
    public async Task AcknowledgeAsync(
        string deviceId, long sequence, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            UPDATE devices
            SET last_ack_seq = MAX(last_ack_seq, @seq), last_seen_at = @now
            WHERE device_id = @id;
            """);

        command.Parameters.AddWithValue("@seq", sequence);
        command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@id", deviceId);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task ForgetAsync(string deviceId, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("DELETE FROM devices WHERE device_id = @id;");
        command.Parameters.AddWithValue("@id", deviceId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------- conflicts

    /// <summary>
    /// Records two versions of the same thing that were edited apart. Campus never silently
    /// picks a winner: a conflict is surfaced, because the machine cannot know which sentence
    /// the person meant to keep.
    /// </summary>
    public async Task RecordConflictAsync(SyncConflict conflict, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            INSERT INTO conflicts
                (id, entity_id, entity_type, local_snapshot, remote_snapshot,
                 local_updated, remote_updated, remote_device, detected_at, resolution)
            VALUES (@id, @entity, @type, @local, @remote, @localAt, @remoteAt, @device, @at, NULL);
            """);

        command.Parameters.AddWithValue("@id", conflict.Id.Value);
        command.Parameters.AddWithValue("@entity", conflict.EntityId.Value);
        command.Parameters.AddWithValue("@type", conflict.EntityType);
        command.Parameters.AddWithValue("@local", conflict.LocalSnapshot);
        command.Parameters.AddWithValue("@remote", conflict.RemoteSnapshot);
        command.Parameters.AddWithValue("@localAt", conflict.LocalUpdatedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@remoteAt", conflict.RemoteUpdatedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@device", conflict.RemoteDeviceId);
        command.Parameters.AddWithValue("@at", conflict.DetectedAt.ToUnixTimeMilliseconds());

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SyncConflict>> UnresolvedConflictsAsync(
        CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand(
            "SELECT * FROM conflicts WHERE resolution IS NULL ORDER BY detected_at DESC;");

        var conflicts = new List<SyncConflict>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            conflicts.Add(new SyncConflict
            {
                Id = CampusId.Parse(reader.GetString(reader.GetOrdinal("id"))),
                EntityId = CampusId.Parse(reader.GetString(reader.GetOrdinal("entity_id"))),
                EntityType = reader.GetString(reader.GetOrdinal("entity_type")),
                LocalSnapshot = reader.GetString(reader.GetOrdinal("local_snapshot")),
                RemoteSnapshot = reader.GetString(reader.GetOrdinal("remote_snapshot")),
                LocalUpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(
                    reader.GetInt64(reader.GetOrdinal("local_updated"))),
                RemoteUpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(
                    reader.GetInt64(reader.GetOrdinal("remote_updated"))),
                RemoteDeviceId = reader.GetString(reader.GetOrdinal("remote_device")),
                DetectedAt = DateTimeOffset.FromUnixTimeMilliseconds(
                    reader.GetInt64(reader.GetOrdinal("detected_at"))),
            });
        }

        return conflicts;
    }

    public async Task ResolveConflictAsync(
        CampusId id, ConflictResolution resolution, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand(
            "UPDATE conflicts SET resolution = @resolution WHERE id = @id;");
        command.Parameters.AddWithValue("@resolution", (int)resolution);
        command.Parameters.AddWithValue("@id", id.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
