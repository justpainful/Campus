using Campus.Desktop.Design.Icons;
using Campus.Desktop.Shell;
using Campus.Domain;

namespace Campus.Desktop.ViewModels;

/// <summary>One tab within a collection — Today, Upcoming, Overdue, Done.</summary>
public sealed record CollectionSegment(string Name, Func<CampusQuery> BuildQuery);

/// <summary>
/// An action that belongs to a whole list rather than to one row — emptying the trash, clearing
/// an archive. Defined beside the list it belongs to so the page stays free of special cases.
/// </summary>
public sealed record CollectionCommand(
    string Label,
    string Symbol,
    bool IsDestructive,
    string? ConfirmTitle,
    string? ConfirmMessage);

/// <summary>
/// Everything a list destination needs: what it is called, how its segments are queried, what to
/// say when it is empty, and what creating something here means.
/// </summary>
public sealed record CollectionDefinition(
    string Title,
    string Symbol,
    ObjectKind CreateKind,
    IReadOnlyList<CollectionSegment> Segments,
    string EmptyTitle,
    string EmptyMessage,
    string NewLabel)
{
    /// <summary>Actions that apply to the list as a whole. Usually none.</summary>
    public IReadOnlyList<CollectionCommand> Commands { get; init; } = [];

    /// <summary>False when this list is not somewhere new things are made.</summary>
    public bool CanCreate => CreateKind != ObjectKind.Unknown && NewLabel.Length > 0;
}

/// <summary>
/// The list destinations, defined in one place. Each is a query rather than a folder, which is
/// what lets one book appear under its subject, in the library and in an exam collection without
/// three copies existing.
/// </summary>
public static class CollectionCatalog
{
    public static CollectionDefinition? For(string destinationId) => destinationId switch
    {
        ShellDestinations.Inbox => Inbox(),
        ShellDestinations.Tasks => Tasks(),
        ShellDestinations.Assignments => Assignments(),
        ShellDestinations.Requirements => Requirements(),
        ShellDestinations.Notes => Notes(),
        ShellDestinations.Library => Library(),
        ShellDestinations.Links => Links(),
        ShellDestinations.PrintCenter => PrintCenter(),
        ShellDestinations.Archive => Archive(),
        ShellDestinations.Trash => Trash(),
        _ => null,
    };

    private static CampusQuery Of(ObjectKind kind, Action<CampusQuery>? refine = null)
    {
        var query = new CampusQuery { Kinds = { kind } };
        refine?.Invoke(query);
        return query;
    }

    public static CollectionDefinition Inbox() => new(
        "Inbox", CampusSymbols.Inbox, ObjectKind.InboxItem,
        [
            new("Waiting", () => Of(ObjectKind.InboxItem, q =>
            {
                q.Statuses.Add(ObjectStatus.None);
                q.Sort = SortField.CreatedAt;
            })),
            new("All", () => Of(ObjectKind.InboxItem, q => q.Sort = SortField.CreatedAt)),
        ],
        "Inbox is clear",
        "Anything you capture in a hurry lands here until you decide what it actually is.",
        "Capture");

    public static CollectionDefinition Tasks() => new(
        "Tasks", CampusSymbols.Tasks, ObjectKind.Task,
        [
            new("Today", () => Of(ObjectKind.Task, q =>
            {
                q.Due = DateRange.Of(RelativeWindow.Today);
                q.Sort = SortField.Priority;
            })),
            new("Upcoming", () => Of(ObjectKind.Task, q =>
            {
                q.Due = DateRange.Of(RelativeWindow.Next30Days);
                q.Sort = SortField.DueAt;
                q.Descending = false;
            })),
            new("Overdue", () => Of(ObjectKind.Task, q =>
            {
                q.Due = DateRange.Of(RelativeWindow.Overdue);
                q.Statuses.Add(ObjectStatus.NotStarted);
                q.Statuses.Add(ObjectStatus.InProgress);
                q.Sort = SortField.DueAt;
                q.Descending = false;
            })),
            new("Someday", () => Of(ObjectKind.Task, q => q.Sort = SortField.CreatedAt)),
            new("Done", () => Of(ObjectKind.Task, q =>
            {
                q.Statuses.Add(ObjectStatus.Completed);
                q.Sort = SortField.UpdatedAt;
            })),
        ],
        "Nothing to do",
        "Tasks are the small things — revise a chapter, ask about a mark, pack the ruler.",
        "New task");

    public static CollectionDefinition Assignments() => new(
        "Assignments", CampusSymbols.Assignments, ObjectKind.Assignment,
        [
            new("Due", () => Of(ObjectKind.Assignment, q =>
            {
                q.Statuses.Add(ObjectStatus.NotStarted);
                q.Statuses.Add(ObjectStatus.InProgress);
                q.Sort = SortField.DueAt;
                q.Descending = false;
            })),
            new("This week", () => Of(ObjectKind.Assignment, q =>
            {
                q.Due = DateRange.Of(RelativeWindow.Next7Days);
                q.Sort = SortField.DueAt;
                q.Descending = false;
            })),
            new("Handed in", () => Of(ObjectKind.Assignment, q =>
            {
                q.Statuses.Add(ObjectStatus.Completed);
                q.Sort = SortField.UpdatedAt;
            })),
            new("All", () => Of(ObjectKind.Assignment, q =>
            {
                q.Sort = SortField.DueAt;
                q.Descending = false;
            })),
        ],
        "No assignments",
        "What was set, when it is due, who set it, and whether it has been handed in.",
        "New assignment");

    public static CollectionDefinition Requirements() => new(
        "Requirements", CampusSymbols.Requirements, ObjectKind.Requirement,
        [
            new("To prepare", () => Of(ObjectKind.Requirement, q =>
            {
                q.Statuses.Add(ObjectStatus.NotStarted);
                q.Statuses.Add(ObjectStatus.InProgress);
                q.Sort = SortField.DueAt;
                q.Descending = false;
            })),
            new("Ready", () => Of(ObjectKind.Requirement, q =>
            {
                q.Statuses.Add(ObjectStatus.Completed);
                q.Sort = SortField.DueAt;
                q.Descending = false;
            })),
            new("All", () => Of(ObjectKind.Requirement, q =>
            {
                q.Sort = SortField.DueAt;
                q.Descending = false;
            })),
        ],
        "Nothing to bring",
        "The things you have to bring or prepare, tracked before the day you need them.",
        "New requirement");

    public static CollectionDefinition Notes() => new(
        "Notes", CampusSymbols.Notes, ObjectKind.Note,
        [
            new("Recent", () => Of(ObjectKind.Note, q => q.Sort = SortField.UpdatedAt)),
            new("Pinned", () => Of(ObjectKind.Note, q =>
            {
                q.IsPinned = true;
                q.Sort = SortField.UpdatedAt;
            })),
            new("Favourites", () => Of(ObjectKind.Note, q =>
            {
                q.IsFavorite = true;
                q.Sort = SortField.UpdatedAt;
            })),
        ],
        "No notes yet",
        "Quick notes, lesson notes, daily notes and the scratchpad.",
        "New note");

    public static CollectionDefinition Library() => new(
        "Library", CampusSymbols.Library, ObjectKind.Book,
        [
            new("Books", () => Of(ObjectKind.Book, q => q.Sort = SortField.Title)),
            new("Reading", () => Of(ObjectKind.Book, q =>
            {
                q.Statuses.Add(ObjectStatus.InProgress);
                q.Sort = SortField.OpenedAt;
            })),
            new("Files", () => Of(ObjectKind.File, q => q.Sort = SortField.UpdatedAt)),
        ],
        "The library is empty",
        "Textbooks, solved books, references and explanations — searchable inside, not just by name.",
        "Add book");

    public static CollectionDefinition Links() => new(
        "Links", CampusSymbols.Links, ObjectKind.Link,
        [
            new("All", () => Of(ObjectKind.Link, q => q.Sort = SortField.UpdatedAt)),
            new("Pinned", () => Of(ObjectKind.Link, q =>
            {
                q.IsPinned = true;
                q.Sort = SortField.UpdatedAt;
            })),
        ],
        "No links saved",
        "YouTube explanations, Telegram groups and school portals, kept with their titles.",
        "Add link");

    public static CollectionDefinition PrintCenter() => new(
        "Print Center", CampusSymbols.PrintCenter, ObjectKind.PrintJob,
        [
            new("To print", () => Of(ObjectKind.PrintJob, q =>
            {
                q.PrintState = PrintState.ToPrint;
                q.Sort = SortField.CreatedAt;
            })),
            new("Printed", () => Of(ObjectKind.PrintJob, q =>
            {
                q.PrintState = PrintState.Printed;
                q.Sort = SortField.UpdatedAt;
            })),
            new("Archive", () => Of(ObjectKind.PrintJob, q =>
            {
                q.PrintState = PrintState.Archived;
                q.Sort = SortField.UpdatedAt;
            })),
        ],
        "Nothing waiting to print",
        "Drop files here and Campus keeps the queue, the page counts and what you already printed.",
        "Add to queue");

    /// <summary>
    /// Things put away on purpose. Archived is not deleted and not hidden — it is finished, and
    /// still findable, which is what makes it safe to archive a whole term.
    /// </summary>
    public static CollectionDefinition Archive() => new(
        "Archive", CampusSymbols.Archive, ObjectKind.Unknown,
        [
            new("Everything", () => new CampusQuery
            {
                IsArchived = true,
                Sort = SortField.UpdatedAt,
            }),
            new("Files", () => new CampusQuery
            {
                Kinds = { ObjectKind.File },
                IsArchived = true,
                Sort = SortField.UpdatedAt,
            }),
            new("Notes", () => new CampusQuery
            {
                Kinds = { ObjectKind.Note, ObjectKind.Lesson },
                IsArchived = true,
                Sort = SortField.UpdatedAt,
            }),
            new("Finished work", () => new CampusQuery
            {
                Kinds = { ObjectKind.Assignment, ObjectKind.Task, ObjectKind.Exam },
                IsArchived = true,
                Sort = SortField.UpdatedAt,
            }),
        ],
        "The archive is empty",
        "Last term's work belongs here: out of the way, still searchable, not deleted.",
        "");

    /// <summary>
    /// The trash. Deliberately its own view rather than a filter on everything else, because
    /// something in the trash should never turn up in an ordinary list by accident.
    /// </summary>
    public static CollectionDefinition Trash() => new(
        "Trash", CampusSymbols.Trash, ObjectKind.Unknown,
        [
            new("Everything", () => new CampusQuery
            {
                OnlyTrashed = true,
                Sort = SortField.UpdatedAt,
            }),
        ],
        "The trash is empty",
        "Anything you delete waits here until you empty it, so a wrong click is not the end of it.",
        "")
    {
        Commands =
        [
            new CollectionCommand(
                "Empty trash", CampusSymbols.Delete, IsDestructive: true,
                "Empty the trash?",
                "Everything in the trash will be gone, and the files behind it will be removed "
                + "from the vault. This cannot be undone."),
        ],
    };
}
