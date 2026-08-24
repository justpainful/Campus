using Campus.Desktop.Design.Icons;
using Campus.Desktop.Shell;
using Campus.Domain;

namespace Campus.Desktop.Services;

/// <summary>One thing Campus can do, named the way a person would look for it.</summary>
public sealed record CampusCommand(
    string Id,
    string Title,
    string Category,
    string Symbol,
    Func<Task> Execute,
    string? Shortcut = null,
    Func<bool>? CanExecute = null)
{
    public bool IsAvailable => CanExecute?.Invoke() ?? true;
}

/// <summary>
/// Every command in one list.
///
/// The rule this enforces is that nothing is reachable only by clicking: if an action exists, it
/// is registered here, so the palette, the keyboard and any future automation all see the same
/// set. That is also what makes the app usable without a mouse.
/// </summary>
public sealed class CommandRegistry
{
    private readonly List<CampusCommand> _commands = [];

    public IReadOnlyList<CampusCommand> All => _commands;

    public void Register(CampusCommand command) => _commands.Add(command);

    public CampusCommand? Find(string id)
        => _commands.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// Ranks commands against what has been typed. A prefix match on the title beats a match in
    /// the middle, which beats a match on the category, so typing "set" reaches Settings before
    /// "Reset zoom".
    /// </summary>
    public IReadOnlyList<CampusCommand> Search(string query, int limit = 40)
    {
        var available = _commands.Where(c => c.IsAvailable);
        if (string.IsNullOrWhiteSpace(query))
            return available.OrderBy(c => c.Category).ThenBy(c => c.Title).Take(limit).ToList();

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return available
            .Select(command => (Command: command, Score: Score(command, terms)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Command.Title)
            .Take(limit)
            .Select(x => x.Command)
            .ToList();
    }

    private static int Score(CampusCommand command, string[] terms)
    {
        var total = 0;

        foreach (var term in terms)
        {
            var title = command.Title.AsSpan();
            var titleIndex = title.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            var categoryIndex = command.Category.AsSpan()
                .IndexOf(term, StringComparison.OrdinalIgnoreCase);

            var best = titleIndex switch
            {
                0 => 100,
                > 0 when title[titleIndex - 1] == ' ' => 70,   // start of a word
                > 0 => 40,
                _ => categoryIndex >= 0 ? 20 : 0,
            };

            if (best == 0) return 0;   // every term has to match something
            total += best;
        }

        return total;
    }

    /// <summary>Builds the standard command set for a window.</summary>
    public static CommandRegistry CreateDefault(MainWindow window, WorkspaceService workspace)
    {
        var registry = new CommandRegistry();

        foreach (var destination in ShellDestinations.CreateDefault())
        {
            var id = destination.Id;
            registry.Register(new CampusCommand(
                $"go.{id}", $"Go to {destination.Title}", "Navigate",
                destination.Symbol,
                () => { window.NavigateTo(id); return Task.CompletedTask; },
                destination.Shortcut));
        }

        registry.Register(new CampusCommand(
            "capture.quick", "Quick capture", "Create", CampusSymbols.Add,
            () => window.QuickCaptureAsync(), "Ctrl+Alt+N",
            () => workspace.IsUnlocked));

        foreach (var (kind, label, symbol) in new[]
        {
            (ObjectKind.Task, "task", CampusSymbols.Tasks),
            (ObjectKind.Note, "note", CampusSymbols.Notes),
            (ObjectKind.Assignment, "assignment", CampusSymbols.Assignments),
            (ObjectKind.Requirement, "requirement", CampusSymbols.Requirements),
            (ObjectKind.Link, "link", CampusSymbols.Link),
        })
        {
            var captured = kind;
            registry.Register(new CampusCommand(
                $"new.{captured}", $"New {label}", "Create", symbol,
                () => window.QuickCaptureAsync(captured),
                CanExecute: () => workspace.IsUnlocked));
        }

        registry.Register(new CampusCommand(
            "workspace.lock", "Lock Campus", "Workspace", CampusSymbols.Lock,
            () => { workspace.Lock(); return Task.CompletedTask; }, "Ctrl+Shift+L",
            () => workspace.IsUnlocked));

        registry.Register(new CampusCommand(
            "view.inspector", "Toggle inspector", "View", CampusSymbols.SidebarRight,
            () => { window.ToggleInspector(); return Task.CompletedTask; }));

        registry.Register(new CampusCommand(
            "view.sidebar", "Toggle sidebar", "View", CampusSymbols.SidebarLeft,
            () => { window.ToggleSidebar(); return Task.CompletedTask; }, "Ctrl+B"));

        registry.Register(new CampusCommand(
            "view.focus", "Focus mode", "View", CampusSymbols.FocusMode,
            () => { window.ToggleFocusMode(); return Task.CompletedTask; }, "Ctrl+Shift+Enter"));

        registry.Register(new CampusCommand(
            "theme.gallery", "Open theme gallery", "View", CampusSymbols.Palette,
            () => { window.NavigateTo("gallery"); return Task.CompletedTask; }));

        registry.Register(new CampusCommand(
            "theme.light", "Appearance: Light", "View", CampusSymbols.Sun,
            () => { window.SetAppearance(AppearanceMode.Light); return Task.CompletedTask; }));

        registry.Register(new CampusCommand(
            "theme.dark", "Appearance: Dark", "View", CampusSymbols.Moon,
            () => { window.SetAppearance(AppearanceMode.Dark); return Task.CompletedTask; }));

        registry.Register(new CampusCommand(
            "view.split", "Split the workspace", "View", CampusSymbols.SplitRight,
            () => { window.ToggleSplit(); return Task.CompletedTask; }, "Ctrl+\\"));

        registry.Register(new CampusCommand(
            "view.study", "Study mode", "View", CampusSymbols.Fullscreen,
            () => { window.ToggleStudyMode(); return Task.CompletedTask; }, "Ctrl+Shift+D"));

        // Getting the workspace out, in both senses: a readable copy, and one that can be read
        // back in. Both are deliberate acts and both say what they produce.
        registry.Register(new CampusCommand(
            "workspace.export", "Export everything", "Workspace", CampusSymbols.Export,
            () => window.ExportEverythingAsync(),
            CanExecute: () => workspace.IsUnlocked));

        registry.Register(new CampusCommand(
            "workspace.backup", "Back up now", "Workspace", CampusSymbols.Backup,
            () => window.BackUpNowAsync(),
            CanExecute: () => workspace.IsUnlocked));

        return registry;
    }
}
