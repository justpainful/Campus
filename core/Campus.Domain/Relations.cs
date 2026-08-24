namespace Campus.Domain;

/// <summary>
/// A directed edge between two objects. Backlinks are derived by reading edges in reverse,
/// so a link is only ever stored once.
/// </summary>
public sealed class Relation
{
    public CampusId Id { get; init; } = CampusId.New();
    public CampusId FromId { get; set; }
    public CampusId ToId { get; set; }
    public RelationKind Kind { get; set; } = RelationKind.Related;
    public string? Label { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>True when the edge was inferred from a [[wiki-link]] rather than created by hand.</summary>
    public bool IsDerived { get; set; }
}

/// <summary>An annotation anchored into a document: a highlight, a drawing, a comment, a timestamp.</summary>
public sealed class Annotation
{
    public CampusId Id { get; init; } = CampusId.New();
    public CampusId ObjectId { get; set; }
    public AnnotationKind Kind { get; set; }

    /// <summary>Page number for paged media; null otherwise.</summary>
    public int? Page { get; set; }
    /// <summary>Playback position for time-based media; null otherwise.</summary>
    public TimeSpan? Position { get; set; }

    /// <summary>Normalised rect (0..1 of page size) so annotations survive zoom and re-render.</summary>
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }

    /// <summary>Ink path or quad points, serialised. Empty for plain comments.</summary>
    public string? Geometry { get; set; }

    public string? Text { get; set; }
    /// <summary>Named highlight colour from the theme's annotation palette, never raw hex.</summary>
    public string? ColorName { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum AnnotationKind
{
    Highlight = 0,
    Underline = 1,
    Strikethrough = 2,
    Ink = 3,
    Comment = 4,
    TextBox = 5,
    Bookmark = 6,
    TimestampNote = 7,
    Shape = 8,
}

/// <summary>One entry in an object's audit trail.</summary>
public sealed class HistoryEntry
{
    public CampusId Id { get; init; } = CampusId.New();
    public CampusId ObjectId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public string? DeviceId { get; set; }
}

/// <summary>A retained prior state of an object's content.</summary>
public sealed class ObjectVersion
{
    public CampusId Id { get; init; } = CampusId.New();
    public CampusId ObjectId { get; set; }
    public int VersionNumber { get; set; }
    /// <summary>Vault hash of the snapshot payload.</summary>
    public string ContentHash { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Label { get; set; }
}

public sealed class Tag
{
    public string Name { get; set; } = string.Empty;
    public string? ColorName { get; set; }
    public string? Description { get; set; }
    public int UseCount { get; set; }
    public bool IsPinned { get; set; }
}
