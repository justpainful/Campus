using Campus.Domain;
using Campus.Storage;
using Campus.Vault;

namespace Campus.Sync;

/// <summary>
/// Producing and applying bundles.
///
/// The whole design rests on one decision: sync moves the change log, not the workspace. Two
/// devices that have been apart for a month exchange the entries neither has seen and nothing
/// else, so the cost of syncing is the size of what changed rather than the size of what exists.
///
/// Conflicts are surfaced rather than resolved. When both sides edited the same thing since they
/// last agreed, the incoming version is stored as a conflict and the local text is left alone —
/// the machine does not get to decide which sentence somebody meant.
/// </summary>
public sealed class SyncEngine(
    CampusDatabase database,
    CampusVault vault,
    string deviceId,
    string deviceName)
{
    private readonly JournalRepository _journal = new(database, deviceId);
    private readonly DeviceRepository _devices = new(database);
    private readonly ObjectRepository _objects = new(database, deviceId);
    private readonly CampusVault _vault = vault;

    public string DeviceId { get; } = deviceId;
    public string DeviceName { get; } = deviceName;

    /// <summary>Raised as work progresses, for a UI that should not look frozen.</summary>
    public event EventHandler<string>? Progress;

    // ------------------------------------------------------------------------ sending

    /// <summary>
    /// Writes everything <paramref name="peer"/> has not acknowledged into a bundle.
    /// </summary>
    public async Task<BundleManifest> WriteForAsync(
        PairedDevice peer,
        string pairingCode,
        string destinationPath,
        CancellationToken ct = default)
    {
        Progress?.Invoke(this, "Collecting changes");

        var entries = await _journal
            .ReadSinceAsync(peer.LastAcknowledgedSequence, excludeDeviceId: peer.DeviceId, ct: ct)
            .ConfigureAwait(false);

        Progress?.Invoke(this, $"Packing {entries.Count} changes");

        var salt = Pairing.NewSalt();
        using var key = Pairing.DeriveTransferKey(pairingCode, salt);

        var manifest = await SyncBundle.WriteAsync(
            destinationPath, entries, _vault.Objects, key, salt,
            DeviceId, DeviceName, peer.LastAcknowledgedSequence, ct).ConfigureAwait(false);

        Progress?.Invoke(this, "Ready");
        return manifest;
    }

    /// <summary>
    /// Everything, for a device that has never synced before. The same format — a first sync is
    /// not a special case, it is a bundle that starts at zero.
    /// </summary>
    public Task<BundleManifest> WriteEverythingAsync(
        string pairingCode, string destinationPath, CancellationToken ct = default)
        => WriteForAsync(
            new PairedDevice { DeviceId = "", DisplayName = "", LastAcknowledgedSequence = 0 },
            pairingCode, destinationPath, ct);

    // ----------------------------------------------------------------------- receiving

    /// <summary>
    /// Applies a bundle. Returns null when the pairing code does not open it, in which case
    /// nothing has been changed.
    /// </summary>
    public async Task<SyncResult?> ApplyAsync(
        string bundlePath, string pairingCode, CancellationToken ct = default)
    {
        var manifest = await SyncBundle.ReadManifestAsync(bundlePath, ct).ConfigureAwait(false);
        if (manifest is null) return null;

        byte[] salt;
        try { salt = Convert.FromBase64String(manifest.Salt); }
        catch (FormatException) { return null; }

        using var key = Pairing.DeriveTransferKey(pairingCode, salt);

        using var contents = await SyncBundle
            .OpenAsync(bundlePath, key, manifest.FromDeviceId, ct).ConfigureAwait(false);
        if (contents is null) return null;

        var entries = await contents.ReadJournalAsync(ct).ConfigureAwait(false);

        // What this device and the peer last agreed on. Anything edited here after that point,
        // and also edited there, is a genuine conflict rather than a stale copy.
        var peer = await _devices.GetAsync(manifest.FromDeviceId, ct).ConfigureAwait(false);
        var lastAgreed = peer?.LastSeenAt ?? DateTimeOffset.MinValue;

        var applied = 0;
        var ignored = 0;
        var conflicted = 0;
        var files = 0;

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            if (entry.EntityType != "object") continue;

            var incoming = SnapshotSerializer.Deserialize(entry.Snapshot);
            if (incoming is null) continue;

            Progress?.Invoke(this, incoming.Title);

            // Bytes come before the record that refers to them, so a file's row is never
            // visible in the workspace before the file itself can be opened.
            if (entry.ContentHash is { Length: > 0 } hash && !_vault.Objects.Exists(hash))
            {
                if (contents.OpenFile(hash) is { } stream)
                {
                    await using (stream)
                    {
                        var stored = await _vault.Objects
                            .PutStreamAsync(stream, ct).ConfigureAwait(false);

                        // The hash is the address; if it does not match, the bundle is not
                        // carrying what it claimed and the file is dropped rather than trusted.
                        if (stored.ContentHash == hash) files++;
                        else _vault.Objects.Delete(stored.ContentHash);
                    }
                }
            }

            var outcome = await _objects.ApplyRemoteAsync(
                incoming, entry.DeviceId, lastAgreed, entry.Operation, ct).ConfigureAwait(false);

            switch (outcome)
            {
                case ObjectRepository.ApplyOutcome.Applied:
                    applied++;
                    break;

                case ObjectRepository.ApplyOutcome.Ignored:
                    ignored++;
                    break;

                case ObjectRepository.ApplyOutcome.Conflicted:
                    conflicted++;
                    await RecordConflictAsync(incoming, entry, ct).ConfigureAwait(false);
                    break;
            }
        }

        // Recording the peer keeps the position it has reached, so the next bundle starts where
        // this one stopped rather than replaying a term of changes.
        await _devices.SaveAsync(new PairedDevice
        {
            DeviceId = manifest.FromDeviceId,
            DisplayName = manifest.FromDeviceName,
            Platform = peer?.Platform ?? DevicePlatform.Windows,
            PublicKey = peer?.PublicKey ?? string.Empty,
            PairedAt = peer?.PairedAt ?? DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
            LastAcknowledgedSequence = Math.Max(
                peer?.LastAcknowledgedSequence ?? 0, manifest.ToSequence),
            Trusted = peer?.Trusted ?? true,
        }, ct).ConfigureAwait(false);

        return new SyncResult(applied, ignored, conflicted, files, manifest.ToSequence);
    }

    private async Task RecordConflictAsync(
        CampusObject incoming, JournalEntry entry, CancellationToken ct)
    {
        var local = await _objects.GetAsync(incoming.Id, ct).ConfigureAwait(false);
        if (local is null) return;

        await _devices.RecordConflictAsync(new SyncConflict
        {
            EntityId = incoming.Id,
            EntityType = "object",
            LocalSnapshot = PayloadSerializer.SerializeSnapshot(local),
            RemoteSnapshot = entry.Snapshot ?? string.Empty,
            LocalUpdatedAt = local.UpdatedAt,
            RemoteUpdatedAt = incoming.UpdatedAt,
            RemoteDeviceId = entry.DeviceId,
        }, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------- conflicts

    /// <summary>
    /// Settles a conflict the way the person chose. Keeping both makes a copy rather than
    /// merging, because merging two versions of a paragraph is a guess dressed up as a feature.
    /// </summary>
    public async Task ResolveAsync(
        SyncConflict conflict, ConflictResolution resolution, CancellationToken ct = default)
    {
        switch (resolution)
        {
            case ConflictResolution.KeepRemote:
                if (SnapshotSerializer.Deserialize(conflict.RemoteSnapshot) is { } remote)
                {
                    remote.UpdatedAt = DateTimeOffset.UtcNow;
                    await _objects.SaveAsync(remote, ct).ConfigureAwait(false);
                }
                break;

            case ConflictResolution.KeepBoth:
                if (SnapshotSerializer.Deserialize(conflict.RemoteSnapshot) is { } copy)
                {
                    var duplicate = new CampusObject
                    {
                        Title = copy.Title + $" (from {conflict.RemoteDeviceId})",
                        Kind = copy.Kind,
                        Summary = copy.Summary,
                        SubjectId = copy.SubjectId,
                        ParentId = copy.ParentId,
                        Status = copy.Status,
                        Priority = copy.Priority,
                        DueAt = copy.DueAt,
                        Payload = copy.Payload,
                        SourceDeviceId = conflict.RemoteDeviceId,
                    };
                    duplicate.Tags.AddRange(copy.Tags);
                    await _objects.SaveAsync(duplicate, ct).ConfigureAwait(false);
                }
                break;

            case ConflictResolution.KeepLocal:
                // Nothing to do: the local copy was never replaced.
                break;
        }

        await _devices.ResolveConflictAsync(conflict.Id, resolution, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<SyncConflict>> ConflictsAsync(CancellationToken ct = default)
        => _devices.UnresolvedConflictsAsync(ct);

    public Task<IReadOnlyList<PairedDevice>> DevicesAsync(CancellationToken ct = default)
        => _devices.AllAsync(ct);

    public Task<long> PositionAsync(CancellationToken ct = default)
        => _journal.MaxSequenceAsync(ct);

    public Task<int> PendingForAsync(PairedDevice peer, CancellationToken ct = default)
        => _journal.PendingForAsync(peer.DeviceId, peer.LastAcknowledgedSequence, ct);

    public Task PairAsync(PairedDevice device, CancellationToken ct = default)
        => _devices.SaveAsync(device, ct);

    public Task ForgetAsync(string peerDeviceId, CancellationToken ct = default)
        => _devices.ForgetAsync(peerDeviceId, ct);
}
