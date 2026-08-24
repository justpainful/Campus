namespace Campus.Domain;

/// <summary>
/// One append-only entry in the change journal. Sync ships the range a peer has not seen
/// rather than diffing the whole workspace.
/// </summary>
public sealed class JournalEntry
{
    public long Sequence { get; set; }
    public ChangeOperation Operation { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public CampusId EntityId { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public string DeviceId { get; set; } = string.Empty;
    /// <summary>Serialised state after the change, for replay onto a peer.</summary>
    public string? Snapshot { get; set; }
    /// <summary>Vault hash when the change brought file bytes with it.</summary>
    public string? ContentHash { get; set; }
}

public enum ChangeOperation
{
    Create = 0,
    Update = 1,
    Delete = 2,
    Trash = 3,
    Restore = 4,
    Import = 5,
}

/// <summary>A device paired with this workspace.</summary>
public sealed class PairedDevice
{
    public string DeviceId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DevicePlatform Platform { get; set; }
    /// <summary>Peer's long-term public key, base64.</summary>
    public string PublicKey { get; set; } = string.Empty;
    public DateTimeOffset PairedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeenAt { get; set; }
    public long LastAcknowledgedSequence { get; set; }
    public bool Trusted { get; set; } = true;
}

public enum DevicePlatform { Windows = 0, IOS = 1, Android = 2, MacOS = 3 }

/// <summary>Two divergent edits of one entity, surfaced to the user instead of silently overwritten.</summary>
public sealed class SyncConflict
{
    public CampusId Id { get; init; } = CampusId.New();
    public CampusId EntityId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string LocalSnapshot { get; set; } = string.Empty;
    public string RemoteSnapshot { get; set; } = string.Empty;
    public DateTimeOffset LocalUpdatedAt { get; set; }
    public DateTimeOffset RemoteUpdatedAt { get; set; }
    public string RemoteDeviceId { get; set; } = string.Empty;
    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.UtcNow;
    public ConflictResolution? Resolution { get; set; }
}

public enum ConflictResolution { KeepLocal = 0, KeepRemote = 1, KeepBoth = 2 }

/// <summary>An item captured on the phone and waiting to reach the PC.</summary>
public sealed class OutboxItem
{
    public CampusId Id { get; init; } = CampusId.New();
    public ObjectKind Kind { get; set; }
    public string Payload { get; set; } = string.Empty;
    public string? ContentHash { get; set; }
    public long SizeBytes { get; set; }
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool Synced { get; set; }
    public DateTimeOffset? SyncedAt { get; set; }
}
