using Campus.Desktop.Design.Icons;
using Campus.Desktop.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Campus.Desktop.Shell;

/// <summary>
/// One destination on the activity bar. The rail is the app's spine, so the list is defined
/// once here rather than being scattered across the XAML.
/// </summary>
public sealed partial class ShellDestination : ObservableObject
{
    public required string Id { get; init; }
    public required string Symbol { get; init; }

    /// <summary>
    /// The catalogue key for this destination's name.
    ///
    /// A key rather than a word, because the rail is built once at startup and lives for the
    /// session: if it held the English word, switching to Arabic would leave sixteen English
    /// tooltips down the side of an Arabic window.
    /// </summary>
    public required string TitleKey { get; init; }

    public string Title => Localization.Language.Get(TitleKey);

    /// <summary>Keyboard accelerator hint shown in the tooltip, for example "Ctrl+1".</summary>
    public string? Shortcut { get; init; }

    /// <summary>Where the destination sits: the main group, or pinned to the bottom of the rail.</summary>
    public DestinationPlacement Placement { get; init; } = DestinationPlacement.Main;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Unread or pending count. Zero hides the badge entirely.</summary>
    [ObservableProperty]
    public partial int Badge { get; set; }

    public string AccessibleName => Badge > 0 ? $"{Title}, {Badge} pending" : Title;

    public string TooltipText => Shortcut is null ? Title : $"{Title}  ({Shortcut})";
}

public enum DestinationPlacement { Main = 0, Bottom = 1 }

/// <summary>The rail as shipped. Order is deliberate: capture, then organise, then reference.</summary>
public static class ShellDestinations
{
    public const string Home = "home";
    public const string Inbox = "inbox";
    public const string Subjects = "subjects";
    public const string Library = "library";
    public const string Files = "files";
    public const string Notes = "notes";
    public const string Assignments = "assignments";
    public const string Tasks = "tasks";
    public const string Requirements = "requirements";
    public const string Goals = "goals";
    public const string Planner = "planner";
    public const string PrintCenter = "print";
    public const string Links = "links";
    public const string Boards = "boards";
    public const string Conversations = "conversations";
    public const string Search = "search";
    public const string Sync = "sync";
    public const string Extensions = "extensions";
    public const string Archive = "archive";
    public const string Trash = "trash";
    public const string Profile = "profile";
    public const string Settings = "settings";

    public static IReadOnlyList<ShellDestination> CreateDefault() =>
    [
        new() { Id = Home, TitleKey = "rail.home", Symbol = CampusSymbols.Home, Shortcut = "Ctrl+1" },
        new() { Id = Inbox, TitleKey = "rail.inbox", Symbol = CampusSymbols.Inbox, Shortcut = "Ctrl+2" },
        new() { Id = Subjects, TitleKey = "rail.subjects", Symbol = CampusSymbols.Subjects, Shortcut = "Ctrl+3" },
        new() { Id = Library, TitleKey = "rail.library", Symbol = CampusSymbols.Library, Shortcut = "Ctrl+4" },
        new() { Id = Files, TitleKey = "rail.files", Symbol = CampusSymbols.Files },
        new() { Id = Notes, TitleKey = "rail.notes", Symbol = CampusSymbols.Notes, Shortcut = "Ctrl+5" },
        new() { Id = Assignments, TitleKey = "rail.assignments", Symbol = CampusSymbols.Assignments, Shortcut = "Ctrl+6" },
        new() { Id = Tasks, TitleKey = "rail.tasks", Symbol = CampusSymbols.Tasks, Shortcut = "Ctrl+7" },
        new() { Id = Requirements, TitleKey = "rail.requirements", Symbol = CampusSymbols.Requirements, Shortcut = "Ctrl+8" },
        new() { Id = Goals, TitleKey = "rail.goals", Symbol = CampusSymbols.Goals },
        new() { Id = Planner, TitleKey = "rail.planner", Symbol = CampusSymbols.Planner, Shortcut = "Ctrl+9" },
        new() { Id = PrintCenter, TitleKey = "rail.print.center", Symbol = CampusSymbols.PrintCenter },
        new() { Id = Links, TitleKey = "rail.links", Symbol = CampusSymbols.Links },
        new() { Id = Boards, TitleKey = "rail.boards", Symbol = CampusSymbols.Boards },
        new() { Id = Conversations, TitleKey = "rail.conversations", Symbol = CampusSymbols.Conversations },
        new() { Id = Search, TitleKey = "rail.search", Symbol = CampusSymbols.Search, Shortcut = "Ctrl+Shift+F" },

        new() { Id = Archive, TitleKey = "rail.archive", Symbol = CampusSymbols.Archive, Placement = DestinationPlacement.Bottom },
        new() { Id = Trash, TitleKey = "rail.trash", Symbol = CampusSymbols.Trash, Placement = DestinationPlacement.Bottom },
        new() { Id = Sync, TitleKey = "rail.sync", Symbol = CampusSymbols.Sync, Placement = DestinationPlacement.Bottom },
        new() { Id = Extensions, TitleKey = "rail.extensions", Symbol = CampusSymbols.Extensions, Placement = DestinationPlacement.Bottom },
        new() { Id = Profile, TitleKey = "rail.profile", Symbol = CampusSymbols.Profile, Placement = DestinationPlacement.Bottom },
        new() { Id = Settings, TitleKey = "rail.settings", Symbol = CampusSymbols.Settings, Shortcut = "Ctrl+,", Placement = DestinationPlacement.Bottom },
    ];
}
