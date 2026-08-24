namespace Campus.Domain;

/// <summary>
/// The single shape every Campus entity shares. Kind-specific data lives in the strongly
/// typed <see cref="Payload"/> so one table, one index and one search path serve everything.
/// </summary>
public sealed class CampusObject
{
    public CampusId Id { get; init; } = CampusId.New();
    public ObjectKind Kind { get; set; } = ObjectKind.Unknown;

    public string Title { get; set; } = string.Empty;
    public string? Summary { get; set; }

    public CampusId? SubjectId { get; set; }
    public CampusId? ParentId { get; set; }

    public ObjectStatus Status { get; set; } = ObjectStatus.None;
    public Priority Priority { get; set; } = Priority.None;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DueAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset? OpenedAt { get; set; }

    public bool IsFavorite { get; set; }
    public bool IsPinned { get; set; }
    public bool IsArchived { get; set; }
    public bool IsTrashed => DeletedAt is not null;

    public int? AcademicYear { get; set; }
    public TermKind? Term { get; set; }

    public CaptureSource Source { get; set; } = CaptureSource.Desktop;
    public string? SourceDeviceId { get; set; }

    public List<string> Tags { get; init; } = [];
    public Dictionary<string, string> Metadata { get; init; } = [];

    /// <summary>Kind-specific data. Null for objects that need nothing beyond the shared shape.</summary>
    public IObjectPayload? Payload { get; set; }

    /// <summary>Sort key used when the user hand-orders a collection.</summary>
    public double SortOrder { get; set; }

    public T? PayloadAs<T>() where T : class, IObjectPayload => Payload as T;

    public void Touch() => UpdatedAt = DateTimeOffset.UtcNow;
}

/// <summary>Marker for kind-specific payloads. Each implementation names the kind it belongs to.</summary>
public interface IObjectPayload
{
    ObjectKind Kind { get; }
}
