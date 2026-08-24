using System.Collections.ObjectModel;
using Campus.Desktop.Design.Icons;
using Campus.Desktop.Services;
using Campus.Desktop.ViewModels;
using Campus.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Campus.Desktop.Views;

/// <summary>One row in the palette, whether it came from the command list or the workspace.</summary>
public sealed class PaletteEntry
{
    public required string Title { get; init; }
    public required string Symbol { get; init; }
    public string? Detail { get; init; }
    public string? Shortcut { get; init; }
    public Func<Task>? Run { get; init; }
}

public enum PaletteMode
{
    /// <summary>Ctrl+Shift+P — search what Campus can do.</summary>
    Commands,
    /// <summary>Ctrl+P — search what is in the workspace.</summary>
    Search,
}

/// <summary>
/// The command palette and quick open, which are the same surface in two modes. Everything the
/// app can do is reachable from here, which is what keeps Campus usable without a mouse.
/// </summary>
public sealed partial class CommandPalette : UserControl
{
    private readonly ObservableCollection<PaletteEntry> _entries = [];
    private CommandRegistry? _registry;
    private WorkspaceService? _workspace;
    private PaletteMode _mode = PaletteMode.Commands;
    private CancellationTokenSource? _pendingSearch;

    public CommandPalette()
    {
        InitializeComponent();
        Results.ItemsSource = _entries;
    }

    public bool IsOpen => Root.Visibility == Visibility.Visible;

    public void Initialise(CommandRegistry registry, WorkspaceService workspace)
    {
        _registry = registry;
        _workspace = workspace;
    }

    public void Show(PaletteMode mode)
    {
        _mode = mode;
        ApplyMode();

        Root.Visibility = Visibility.Visible;
        Input.Text = string.Empty;
        Input.Focus(FocusState.Programmatic);
        _ = RefreshAsync();
    }

    public void Hide()
    {
        _pendingSearch?.Cancel();
        Root.Visibility = Visibility.Collapsed;
        _entries.Clear();
    }

    private void ApplyMode()
    {
        var searching = _mode == PaletteMode.Search;
        ModeIcon.Symbol = searching ? CampusSymbols.Search : CampusSymbols.Command;
        Input.PlaceholderText = searching ? "Search everything" : "Type a command";
        HintText.Text = searching
            ? "Enter to open · Tab for commands · Esc to close"
            : "Enter to run · Tab to search the workspace · Esc to close";
    }

    private void OnQueryChanged(object sender, TextChangedEventArgs e) => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        _pendingSearch?.Cancel();
        var cts = new CancellationTokenSource();
        _pendingSearch = cts;

        var query = Input.Text.Trim();

        if (_mode == PaletteMode.Commands)
        {
            ShowCommands(query);
            return;
        }

        if (_workspace is not { IsUnlocked: true } || query.Length == 0)
        {
            _entries.Clear();
            return;
        }

        try
        {
            var results = await _workspace.Objects.QueryAsync(new CampusQuery
            {
                Text = query,
                Sort = SortField.Relevance,
                Limit = 30,
            }, cts.Token);

            if (cts.IsCancellationRequested) return;

            _entries.Clear();
            foreach (var model in results)
            {
                var item = new ObjectItem(model);
                var captured = model;
                _entries.Add(new PaletteEntry
                {
                    Title = item.Title,
                    Symbol = item.Symbol,
                    Detail = item.Subtitle.Length > 0 ? item.Subtitle : model.Kind.ToString(),
                    Run = async () =>
                    {
                        if (_workspace is { IsUnlocked: true })
                            await _workspace.Objects.MarkOpenedAsync(captured.Id);
                    },
                });
            }

            if (_entries.Count > 0) Results.SelectedIndex = 0;
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke already started a newer search.
        }
    }

    private void ShowCommands(string query)
    {
        _entries.Clear();
        if (_registry is null) return;

        foreach (var command in _registry.Search(query))
        {
            _entries.Add(new PaletteEntry
            {
                Title = command.Title,
                Symbol = command.Symbol,
                Detail = command.Category,
                Shortcut = command.Shortcut,
                Run = command.Execute,
            });
        }

        if (_entries.Count > 0) Results.SelectedIndex = 0;
    }

    private void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                Hide();
                e.Handled = true;
                break;

            case VirtualKey.Enter:
                _ = RunSelectedAsync();
                e.Handled = true;
                break;

            case VirtualKey.Tab:
                // One key flips between "what can Campus do" and "what is in my workspace",
                // so a wrong guess about which one you wanted costs nothing.
                _mode = _mode == PaletteMode.Commands ? PaletteMode.Search : PaletteMode.Commands;
                ApplyMode();
                _ = RefreshAsync();
                e.Handled = true;
                break;

            case VirtualKey.Down:
                Move(1);
                e.Handled = true;
                break;

            case VirtualKey.Up:
                Move(-1);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Moves the selection while focus stays in the text box, so typing never stops.</summary>
    private void Move(int delta)
    {
        if (_entries.Count == 0) return;
        var next = Results.SelectedIndex + delta;
        Results.SelectedIndex = Math.Clamp(next, 0, _entries.Count - 1);
        Results.ScrollIntoView(Results.SelectedItem);
    }

    private async void OnResultClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PaletteEntry entry) await RunAsync(entry);
    }

    private Task RunSelectedAsync()
        => Results.SelectedItem is PaletteEntry entry ? RunAsync(entry) : Task.CompletedTask;

    private async Task RunAsync(PaletteEntry entry)
    {
        // Closed before running, so a command that opens a dialog does not appear behind this one.
        Hide();
        if (entry.Run is not null) await entry.Run();
    }

    private void OnScrimPressed(object sender, PointerRoutedEventArgs e) => Hide();

    public static Visibility TextVisibility(string? text)
        => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
}
