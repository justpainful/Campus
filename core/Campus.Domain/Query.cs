namespace Campus.Domain;

/// <summary>
/// The description of a virtual collection. Every list in Campus — a subject's books, the print
/// queue, "due this week" — is one of these rather than a folder on disk.
/// </summary>
public sealed class CampusQuery
{
    public string? Text { get; set; }
    public List<ObjectKind> Kinds { get; init; } = [];
    public List<CampusId> SubjectIds { get; init; } = [];
    public List<string> TagsAll { get; init; } = [];
    public List<string> TagsAny { get; init; } = [];
    public List<ObjectStatus> Statuses { get; init; } = [];
    public List<MediaKind> Media { get; init; } = [];

    public bool? IsFavorite { get; set; }
    public bool? IsPinned { get; set; }
    public bool? IsArchived { get; set; }
    public bool IncludeTrashed { get; set; }

    /// <summary>Restricts the result to trashed objects, which is what the Trash view wants.</summary>
    public bool OnlyTrashed { get; set; }

    public DateRange? Due { get; set; }
    public DateRange? Created { get; set; }
    public DateRange? Updated { get; set; }

    public int? AcademicYear { get; set; }
    public TermKind? Term { get; set; }

    public CampusId? ParentId { get; set; }
    public PrintState? PrintState { get; set; }

    public SortField Sort { get; set; } = SortField.UpdatedAt;
    public bool Descending { get; set; } = true;
    public int? Limit { get; set; }
    public int Offset { get; set; }

    public CampusQuery Clone() => new()
    {
        Text = Text,
        Kinds = [.. Kinds],
        SubjectIds = [.. SubjectIds],
        TagsAll = [.. TagsAll],
        TagsAny = [.. TagsAny],
        Statuses = [.. Statuses],
        Media = [.. Media],
        IsFavorite = IsFavorite,
        IsPinned = IsPinned,
        IsArchived = IsArchived,
        IncludeTrashed = IncludeTrashed,
        OnlyTrashed = OnlyTrashed,
        Due = Due,
        Created = Created,
        Updated = Updated,
        AcademicYear = AcademicYear,
        Term = Term,
        ParentId = ParentId,
        PrintState = PrintState,
        Sort = Sort,
        Descending = Descending,
        Limit = Limit,
        Offset = Offset,
    };
}

public enum SortField
{
    UpdatedAt = 0,
    CreatedAt = 1,
    DueAt = 2,
    Title = 3,
    Priority = 4,
    Manual = 5,
    OpenedAt = 6,
    Relevance = 7,
}

/// <summary>
/// An absolute or relative window in time. Relative windows are resolved at query time so a
/// saved smart collection like "due in the next 7 days" keeps meaning the right thing.
/// </summary>
public readonly record struct DateRange(DateTimeOffset? From, DateTimeOffset? To, RelativeWindow Relative = RelativeWindow.None)
{
    public static DateRange Absolute(DateTimeOffset? from, DateTimeOffset? to) => new(from, to);
    public static DateRange Of(RelativeWindow window) => new(null, null, window);

    public (DateTimeOffset? From, DateTimeOffset? To) Resolve(DateTimeOffset now)
    {
        if (Relative == RelativeWindow.None) return (From, To);
        var today = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
        return Relative switch
        {
            RelativeWindow.Today => (today, today.AddDays(1)),
            RelativeWindow.Tomorrow => (today.AddDays(1), today.AddDays(2)),
            RelativeWindow.Yesterday => (today.AddDays(-1), today),
            RelativeWindow.ThisWeek => (StartOfWeek(today), StartOfWeek(today).AddDays(7)),
            RelativeWindow.NextWeek => (StartOfWeek(today).AddDays(7), StartOfWeek(today).AddDays(14)),
            RelativeWindow.Next7Days => (today, today.AddDays(7)),
            RelativeWindow.Next30Days => (today, today.AddDays(30)),
            RelativeWindow.Overdue => (null, today),
            RelativeWindow.ThisMonth => (new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset),
                                         new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset).AddMonths(1)),
            RelativeWindow.Last7Days => (today.AddDays(-7), today.AddDays(1)),
            RelativeWindow.Last30Days => (today.AddDays(-30), today.AddDays(1)),
            _ => (From, To),
        };
    }

    private static DateTimeOffset StartOfWeek(DateTimeOffset day)
    {
        // Sunday-first, matching the school week in the user's region.
        var delta = (int)day.DayOfWeek;
        return day.AddDays(-delta);
    }
}

public enum RelativeWindow
{
    None = 0,
    Today = 1,
    Tomorrow = 2,
    Yesterday = 3,
    ThisWeek = 4,
    NextWeek = 5,
    Next7Days = 6,
    Next30Days = 7,
    Overdue = 8,
    ThisMonth = 9,
    Last7Days = 10,
    Last30Days = 11,
}
