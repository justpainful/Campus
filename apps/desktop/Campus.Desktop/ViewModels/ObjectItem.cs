using Campus.Desktop.Design;
using Campus.Desktop.Design.Icons;
using Campus.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Campus.Desktop.ViewModels;

/// <summary>
/// A <see cref="CampusObject"/> dressed for a list: the icon it should show, the line underneath
/// its title, and how its due date reads today. Kept out of the domain so the storage layer never
/// has to know about presentation.
/// </summary>
public sealed partial class ObjectItem(CampusObject model) : ObservableObject
{
    public CampusObject Model { get; } = model;

    public CampusId Id => Model.Id;
    public string Title => Model.Title.Length > 0 ? Model.Title : "Untitled";

    [ObservableProperty]
    public partial string? SubjectName { get; set; }

    [ObservableProperty]
    public partial string SubjectAccent { get; set; } = ThemeTokens.Subject.Graphite;

    public bool IsDone => Model.Status == ObjectStatus.Completed;
    public bool IsPinned => Model.IsPinned;
    public bool IsFavorite => Model.IsFavorite;

    /// <summary>The symbol for this object, chosen by kind and, for files, by media type.</summary>
    public string Symbol => Model.Kind switch
    {
        ObjectKind.Note => CampusSymbols.Notes,
        ObjectKind.Task => CampusSymbols.Tasks,
        ObjectKind.Assignment => CampusSymbols.Assignments,
        ObjectKind.Requirement => CampusSymbols.Requirements,
        ObjectKind.Link => CampusSymbols.Link,
        ObjectKind.Book => CampusSymbols.Book,
        ObjectKind.Lesson => CampusSymbols.Lesson,
        ObjectKind.Exam => CampusSymbols.Exam,
        ObjectKind.Project => CampusSymbols.Project,
        ObjectKind.Board => CampusSymbols.Boards,
        ObjectKind.Thread => CampusSymbols.Thread,
        ObjectKind.PrintJob => CampusSymbols.PrintCenter,
        ObjectKind.Goal => CampusSymbols.Goals,
        ObjectKind.Collection => CampusSymbols.Collection,
        ObjectKind.Subject => CampusSymbols.Subjects,
        ObjectKind.Event => CampusSymbols.Event,
        ObjectKind.Person => CampusSymbols.Person,
        ObjectKind.InboxItem => CampusSymbols.Inbox,
        ObjectKind.File => SymbolForMedia(Model.PayloadAs<FilePayload>()?.Media ?? MediaKind.Unknown),
        _ => CampusSymbols.Unknown,
    };

    private static string SymbolForMedia(MediaKind media) => media switch
    {
        MediaKind.Pdf => CampusSymbols.Pdf,
        MediaKind.Image => CampusSymbols.Image,
        MediaKind.Video => CampusSymbols.Video,
        MediaKind.Audio => CampusSymbols.Audio,
        MediaKind.Spreadsheet => CampusSymbols.Spreadsheet,
        MediaKind.Presentation => CampusSymbols.Presentation,
        MediaKind.Markdown => CampusSymbols.Markdown,
        MediaKind.Text => CampusSymbols.Text,
        MediaKind.Document => CampusSymbols.Document,
        MediaKind.Web => CampusSymbols.Link,
        _ => CampusSymbols.Unknown,
    };

    /// <summary>
    /// The quiet line under the title: subject, kind-specific detail and size, joined by
    /// separators only where both sides actually have something to say.
    /// </summary>
    public string Subtitle
    {
        get
        {
            var parts = new List<string>(3);
            if (!string.IsNullOrEmpty(SubjectName)) parts.Add(SubjectName!);

            switch (Model.Payload)
            {
                case FilePayload file:
                    if (file.PageCount is { } pages) parts.Add($"{pages} page{(pages == 1 ? "" : "s")}");
                    else if (file.Duration is { } duration) parts.Add(FormatDuration(duration));
                    if (file.SizeBytes > 0) parts.Add(FormatSize(file.SizeBytes));
                    break;
                case AssignmentPayload assignment:
                    if (!string.IsNullOrEmpty(assignment.Teacher)) parts.Add(assignment.Teacher!);
                    if (assignment.Points is { } points) parts.Add($"{points:0.##} points");
                    break;
                case BookPayload book:
                    if (!string.IsNullOrEmpty(book.Author)) parts.Add(book.Author!);
                    if (book.CurrentPage is { } page && book.TotalPages is { } total)
                        parts.Add($"page {page} of {total}");
                    break;
                case LinkPayload link:
                    if (!string.IsNullOrEmpty(link.Domain)) parts.Add(link.Domain!);
                    break;
                case TaskPayload task when task.Checklist.Count > 0:
                    parts.Add($"{task.Checklist.Count(c => c.Done)} of {task.Checklist.Count} done");
                    break;
                case PrintJobPayload print:
                    parts.Add($"{print.Pages} page{(print.Pages == 1 ? "" : "s")}");
                    if (print.Copies > 1) parts.Add($"{print.Copies} copies");
                    break;
                case RequirementPayload requirement when !string.IsNullOrEmpty(requirement.Teacher):
                    parts.Add(requirement.Teacher!);
                    break;
                case NotePayload note when note.Body.Length > 0:
                    parts.Add(FirstLine(note.Body));
                    break;
            }

            if (parts.Count == 0 && Model.Summary is { Length: > 0 } summary) parts.Add(summary);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>How the due date reads relative to today. Empty when there is no date.</summary>
    public string DueText => Model.DueAt is { } due ? FormatRelativeDay(due) : string.Empty;

    /// <summary>
    /// The role the due date should be painted in: red once it is past, amber on the day, and
    /// quiet otherwise. A completed item is never urgent, whatever its date says.
    /// </summary>
    public string DueRole
    {
        get
        {
            if (Model.DueAt is not { } due || IsDone) return ThemeTokens.Label.Tertiary;

            var now = DateTimeOffset.Now;
            if (due < now) return ThemeTokens.Destructive.Primary;
            if (due.Date == now.Date) return ThemeTokens.Warning.Primary;
            return ThemeTokens.Label.Secondary;
        }
    }

    public bool HasDue => Model.DueAt is not null;

    public static string FormatRelativeDay(DateTimeOffset value)
    {
        var today = DateTimeOffset.Now.Date;
        var days = (value.Date - today).Days;

        return days switch
        {
            0 => "Today",
            1 => "Tomorrow",
            -1 => "Yesterday",
            > 1 and < 7 => value.ToString("dddd"),
            < -1 and > -7 => $"{-days} days ago",
            _ => value.Year == today.Year ? value.ToString("d MMM") : value.ToString("d MMM yyyy"),
        };
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024L * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    public static string FormatDuration(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
        : $"{value.Minutes}:{value.Seconds:00}";

    private static string FirstLine(string text)
    {
        var line = text.AsSpan();
        var end = line.IndexOfAny('\r', '\n');
        if (end >= 0) line = line[..end];
        return line.Length > 90 ? string.Concat(line[..90], "…") : line.ToString();
    }

    /// <summary>Re-raises the derived properties after the underlying object changes.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(DueText));
        OnPropertyChanged(nameof(DueRole));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(IsFavorite));
        OnPropertyChanged(nameof(Symbol));
    }
}
