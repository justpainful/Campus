namespace Campus.Domain;

/// <summary>A file stored in the vault. The bytes live under <see cref="ContentHash"/>, never under a readable name.</summary>
public sealed class FilePayload : IObjectPayload
{
    public ObjectKind Kind => ObjectKind.File;

    /// <summary>SHA-256 of the plaintext, lowercase hex. Also the vault object key.</summary>
    public string ContentHash { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public string MimeType { get; set; } = "application/octet-stream";
    public MediaKind Media { get; set; } = MediaKind.Unknown;
    public long SizeBytes { get; set; }

    public int? PageCount { get; set; }
    public int? PixelWidth { get; set; }
    public int? PixelHeight { get; set; }
    public TimeSpan? Duration { get; set; }

    public string? ThumbnailHash { get; set; }
    public bool TextExtracted { get; set; }
    public DateTimeOffset? ImportedAt { get; set; }
}

public sealed class NotePayload : IObjectPayload
{
    public ObjectKind Kind => ObjectKind.Note;
    public NoteKind NoteKind { get; set; } = NoteKind.Quick;
    /// <summary>Markdown body. Stored encrypted like everything else.</summary>
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset? ForDate { get; set; }
}

public sealed class TaskPayload : IObjectPayload
{
    public ObjectKind Kind => ObjectKind.Task;
    public List<ChecklistItem> Checklist { get; init; } = [];
    public DateTimeOffset? RemindAt { get; set; }
    public string? Notes { get; set; }
    /// <summary>Set when the task is deferred out of the active lists.</summary>
    public bool Someday { get; set; }
    public RecurrenceRule? Recurrence { get; set; }
}

public sealed class ChecklistItem
{
    public CampusId Id { get; init; } = CampusId.New();
    public string Text { get; set; } = string.Empty;
    public bool Done { get; set; }
    public double SortOrder { get; set; }
}

public sealed class RecurrenceRule
{
    /// <summary>Days between occurrences; 7 = weekly, 1 = daily.</summary>
    public int IntervalDays { get; set; } = 1;
    public DateTimeOffset? Until { get; set; }
    public int? Count { get; set; }
}

public sealed class AssignmentPayload : IObjectPayload
{
    public ObjectKind Kind => ObjectKind.Assignment;
    public DateTimeOffset? AssignedAt { get; set; }
    public string? Teacher { get; set; }
    public double? Points { get; set; }
    public double? EarnedPoints { get; set; }
    public string? Instructions { get; set; }
    public bool Submitted { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public string? SubmissionNote { get; set; }
}

public sealed class RequirementPayload : IObjectPayload
{
    public ObjectKind Kind => ObjectKind.Requirement;
    /// <summary>What has to physically happen: bring a notebook, print a sheet, buy a ruler.</summary>
    public string? Action { get; set; }
    public bool Ready { get; set; }
    public string? Teacher { get; set; }
    public bool RequiresPrinting { get; set; }
}

public sealed class LinkPayload : IObjectPayload
{
    public ObjectKind Kind => ObjectKind.Link;
    public string Url { get; set; } = string.Empty;
    public LinkProvider Provider { get; set; } = LinkProvider.Generic;
    public string? Domain { get; set; }
    public string? Description { get; set; }
    public string? ThumbnailHash { get; set; }
    public DateTimeOffset? FetchedAt { get; set; }
}

public sealed class BookPayload : IObjectPayload
{
    public ObjectKind Kind => ObjectKind.Book;
    public string? Author { get; set; }
    public string? Edition { get; set; }
    public int? TotalPages { get; set; }
    public int? CurrentPage { get; set; }
    /// <summary>True when this book is the worked-solutions companion of another book.</summary>
    public bool IsSolutionBook { get; set; }
}

public sealed class LessonPayload : IObjectPayload
{
    public ObjectKind Kind => ObjectKind.Lesson;
    public string? Unit { get; set; }
    public int? LessonNumber { get; set; }
    public DateTimeOffset? TaughtOn { get; set; }
    public string? Body { get; set; }
}

public sealed class ExamPayload : IObjectPayload
{
    public ObjectKind Kind => ObjectKind.Exam;
    public DateTimeOffset? ScheduledAt { get; set; }
    public string? Scope { get; set; }
    public double? MaxScore { get; set; }
    public double? Score { get; set; }
    public string? Location { get; set; }
}

public sealed class PrintJobPayload : IObjectPayload
{
    public ObjectKind Kind => ObjectKind.PrintJob;
    public PrintState State { get; set; } = PrintState.ToPrint;
    public int Pages { get; set; }
    public int Copies { get; set; } = 1;
    public PrintColorMode ColorMode { get; set; } = PrintColorMode.BlackAndWhite;
    public bool DoubleSided { get; set; }
    public DateTimeOffset? PrintedAt { get; set; }
    public string? Notes { get; set; }
    /// <summary>Objects queued in this job, in order.</summary>
    public List<CampusId> Items { get; init; } = [];
}

public sealed class GoalPayload : IObjectPayload
{
    public ObjectKind Kind => ObjectKind.Goal;
    public string? Detail { get; set; }
    public double Progress { get; set; }
    public DateTimeOffset? TargetDate { get; set; }
    public List<ChecklistItem> Steps { get; init; } = [];
}

public sealed class BoardPayload : IObjectPayload
{
    public ObjectKind Kind => ObjectKind.Board;
    public string? Description { get; set; }
    public List<string> AvailableTags { get; init; } = [];
    public BoardLayout Layout { get; set; } = BoardLayout.Cards;
}

public enum BoardLayout { Cards = 0, List = 1, Compact = 2 }

public sealed class ThreadPayload : IObjectPayload
{
    public ObjectKind Kind => ObjectKind.Thread;
    public string? Body { get; set; }
    public bool Locked { get; set; }
    public bool Resolved { get; set; }
    public int MessageCount { get; set; }
    public DateTimeOffset? LastActivityAt { get; set; }
}

public sealed class CollectionPayload : IObjectPayload
{
    public ObjectKind Kind => ObjectKind.Collection;
    public string? Description { get; set; }
    /// <summary>When set the collection is smart: membership comes from the query, not a stored list.</summary>
    public string? Query { get; set; }
    public List<CampusId> Members { get; init; } = [];
    public string? IconName { get; set; }
}

public sealed class EventPayload : IObjectPayload
{
    public ObjectKind Kind => ObjectKind.Event;
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    public string? Location { get; set; }
    public bool AllDay { get; set; }
    /// <summary>Day-of-week bitmask for a repeating class slot; null for one-off events.</summary>
    public int? WeeklyMask { get; set; }
}

public sealed class PersonPayload : IObjectPayload
{
    public ObjectKind Kind => ObjectKind.Person;
    public string? Role { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Note { get; set; }
}

public sealed class InboxPayload : IObjectPayload
{
    public ObjectKind Kind => ObjectKind.InboxItem;
    public string? RawText { get; set; }
    public string? SuggestedKindHint { get; set; }
    public bool Triaged { get; set; }
}

public sealed class SubjectPayload : IObjectPayload
{
    public ObjectKind Kind => ObjectKind.Subject;
    public string? Teacher { get; set; }
    public string? Room { get; set; }
    public string? Code { get; set; }
    /// <summary>Named accent from the theme's subject palette — never a raw hex value.</summary>
    public string? AccentName { get; set; }
    public string? IconName { get; set; }
    public double SortOrder { get; set; }
}
