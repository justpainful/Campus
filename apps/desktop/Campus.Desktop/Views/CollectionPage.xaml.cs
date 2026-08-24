using System.Collections.ObjectModel;
using Campus.Desktop.Services;
using Campus.Desktop.ViewModels;
using Campus.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Campus.Desktop.Views;

/// <summary>
/// The page behind every list destination. Each one is the same shape — a title, segments, a
/// filter and a list — because they are all the same thing underneath: a query over one table.
/// </summary>
public sealed partial class CollectionPage : Page
{
    private readonly WorkspaceService _workspace = App.GetService<WorkspaceService>();
    private readonly NavigationState _navigation = App.GetService<NavigationState>();
    private readonly ObservableCollection<ObjectItem> _items = [];
    private readonly Dictionary<string, (string Name, string Accent)> _subjects = new(StringComparer.Ordinal);

    private CollectionDefinition _definition = CollectionCatalog.Tasks();
    private string _filter = string.Empty;
    private CancellationTokenSource? _pendingLoad;

    public CollectionPage()
    {
        InitializeComponent();
        Items.ItemsSource = _items;
        _navigation.FilterChanged += OnFilterStateChanged;
        Unloaded += (_, _) => _navigation.FilterChanged -= OnFilterStateChanged;
    }

    private async void OnFilterStateChanged(object? sender, EventArgs e) => await ReloadAsync();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is CollectionDefinition definition) _definition = definition;

        TitleText.Text = _definition.Title;
        NewLabel.Text = _definition.NewLabel;
        EmptyGlyph.Symbol = _definition.Symbol;
        EmptyTitle.Text = _definition.EmptyTitle;
        EmptyMessage.Text = _definition.EmptyMessage;

        Segments.Segments = _definition.Segments.Select(s => s.Name).ToList();
        Segments.SelectedIndex = 0;

        await LoadSubjectsAsync();
        await ReloadAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _pendingLoad?.Cancel();
    }

    /// <summary>
    /// Subject names are resolved once per visit rather than per row; a list of forty tasks
    /// should not be forty extra queries.
    /// </summary>
    private async Task LoadSubjectsAsync()
    {
        _subjects.Clear();
        if (!_workspace.IsUnlocked) return;

        var subjects = await _workspace.Objects.QueryAsync(
            new CampusQuery { Kinds = { ObjectKind.Subject }, Sort = SortField.Manual, Descending = false });

        foreach (var subject in subjects)
        {
            var accent = subject.PayloadAs<SubjectPayload>()?.AccentName ?? "Graphite";
            _subjects[subject.Id.Value] = (subject.Title, accent);
        }
    }

    private async Task ReloadAsync()
    {
        _pendingLoad?.Cancel();
        var cts = new CancellationTokenSource();
        _pendingLoad = cts;

        if (!_workspace.IsUnlocked)
        {
            _items.Clear();
            UpdateEmptyState();
            return;
        }

        var segment = _definition.Segments[Math.Clamp(Segments.SelectedIndex, 0, _definition.Segments.Count - 1)];
        var query = segment.BuildQuery();
        _navigation.Apply(query);
        if (_filter.Length > 0) query.Text = _filter;

        try
        {
            var results = await _workspace.Objects.QueryAsync(query, cts.Token);
            if (cts.IsCancellationRequested) return;

            _items.Clear();
            foreach (var model in results)
            {
                var item = new ObjectItem(model);
                if (model.SubjectId is { } subjectId
                    && _subjects.TryGetValue(subjectId.Value, out var subject))
                {
                    item.SubjectName = subject.Name;
                    item.SubjectAccent = Design.ThemeTokens.Subject.FromName(subject.Accent);
                }
                _items.Add(item);
            }

            CountText.Text = _items.Count.ToString();
            UpdateEmptyState();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer load; the newer one will render.
        }
    }

    private void UpdateEmptyState()
    {
        var empty = _items.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Items.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        CountText.Text = _items.Count.ToString();

        // When a filter is what emptied the list, say so rather than claiming the collection is
        // empty — those are different situations and only one of them needs the invitation.
        if (empty && _filter.Length > 0)
        {
            EmptyTitle.Text = "No matches";
            EmptyMessage.Text = $"Nothing here matches “{_filter}”.";
        }
        else if (empty)
        {
            EmptyTitle.Text = _definition.EmptyTitle;
            EmptyMessage.Text = _definition.EmptyMessage;
        }
    }

    private async void OnSegmentChanged(object? sender, int index) => await ReloadAsync();

    private async void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        _filter = FilterBox.Text.Trim();
        await ReloadAsync();
    }

    private async void OnNewClick(object sender, RoutedEventArgs e)
    {
        if (!_workspace.IsUnlocked) return;

        var created = new CampusObject
        {
            Kind = _definition.CreateKind,
            Title = string.Empty,
            Status = ObjectStatus.NotStarted,
            Payload = NewPayload(_definition.CreateKind),
        };

        var title = await AskForTitleAsync();
        if (title is null) return;

        created.Title = title;
        await _workspace.Objects.SaveAsync(created);
        await ReloadAsync();
    }

    private static IObjectPayload? NewPayload(ObjectKind kind) => kind switch
    {
        ObjectKind.Task => new TaskPayload(),
        ObjectKind.Note => new NotePayload(),
        ObjectKind.Assignment => new AssignmentPayload(),
        ObjectKind.Requirement => new RequirementPayload(),
        ObjectKind.Link => new LinkPayload(),
        ObjectKind.Book => new BookPayload(),
        ObjectKind.PrintJob => new PrintJobPayload(),
        ObjectKind.InboxItem => new InboxPayload(),
        _ => null,
    };

    /// <summary>
    /// A single-field prompt. Creating something should cost one sentence, not a form; the rest
    /// of the fields are filled in later from the inspector.
    /// </summary>
    private async Task<string?> AskForTitleAsync()
    {
        var input = new TextBox
        {
            PlaceholderText = _definition.NewLabel,
            Style = (Style)Application.Current.Resources["Input.Text"],
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = _definition.NewLabel,
            Content = input,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        input.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter) dialog.Hide();
        };

        var result = await dialog.ShowAsync();
        var text = input.Text.Trim();
        return result == ContentDialogResult.Primary && text.Length > 0 ? text : null;
    }

    private async void OnCompletionToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: ObjectItem item } box) return;
        if (!_workspace.IsUnlocked) return;

        var done = box.IsChecked == true;
        item.Model.Status = done ? ObjectStatus.Completed : ObjectStatus.NotStarted;
        item.Model.CompletedAt = done ? DateTimeOffset.UtcNow : null;

        if (item.Model.Payload is RequirementPayload requirement) requirement.Ready = done;
        if (item.Model.Payload is AssignmentPayload assignment)
        {
            assignment.Submitted = done;
            assignment.SubmittedAt = done ? DateTimeOffset.UtcNow : null;
        }

        await _workspace.Objects.SaveAsync(item.Model);
        item.Refresh();

        // A completed item usually leaves the segment it was in, so the list is re-read rather
        // than left showing a row that no longer belongs.
        await ReloadAsync();
    }

    private async void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ObjectItem item) return;
        if (!_workspace.IsUnlocked) return;

        await _workspace.Objects.MarkOpenedAsync(item.Id);
    }

    // -------------------------------------------------- template helper functions

    /// <summary>Kinds that can be finished get a checkbox; everything else gets its icon.</summary>
    public static Visibility CompletableVisibility(ObjectKind kind)
        => IsCompletable(kind) ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IconVisibility(ObjectKind kind)
        => IsCompletable(kind) ? Visibility.Collapsed : Visibility.Visible;

    private static bool IsCompletable(ObjectKind kind)
        => kind is ObjectKind.Task or ObjectKind.Assignment or ObjectKind.Requirement;

    public static Visibility TextVisibility(string? text)
        => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility FlagVisibility(bool value)
        => value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Resolves a semantic role name to its brush, so the template never names a colour.</summary>
    public static Brush RoleBrush(string token)
        => (Brush)Application.Current.Resources[token];
}
