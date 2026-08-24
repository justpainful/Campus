using Campus.Desktop.Design;
using Campus.Desktop.Design.Icons;
using Campus.Desktop.Services;
using Campus.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Campus.Desktop.Shell;

/// <summary>One narrowing choice in the sidebar: a subject or a tag.</summary>
public sealed class SidebarEntry
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public string Symbol { get; init; } = CampusSymbols.Tag;
    /// <summary>Subject rows carry a colour dot; tag rows carry an icon instead.</summary>
    public string? AccentToken { get; init; }
    public int Count { get; init; }
    public string CountText => Count > 0 ? Count.ToString() : string.Empty;
}

/// <summary>
/// The sidebar narrows what the workspace is showing. It never navigates: choosing English does
/// not take you to a "subject page", it filters whatever list you are already looking at, which
/// is the difference between a filter and a folder.
/// </summary>
public sealed partial class WorkspaceSidebar : UserControl
{
    private readonly WorkspaceService _workspace = App.GetService<WorkspaceService>();
    private readonly NavigationState _navigation = App.GetService<NavigationState>();
    private readonly Dictionary<string, string> _subjectNames = new(StringComparer.Ordinal);

    public WorkspaceSidebar()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
        _navigation.FilterChanged += (_, _) => UpdateFilterNotice();
    }

    /// <summary>Re-reads the subjects and tags. Called whenever the workspace changes underneath.</summary>
    public async Task RefreshAsync()
    {
        if (!_workspace.IsUnlocked)
        {
            SubjectList.ItemsSource = null;
            TagList.ItemsSource = null;
            return;
        }

        var repository = _workspace.Objects;

        var subjects = await repository.QueryAsync(new CampusQuery
        {
            Kinds = { ObjectKind.Subject },
            Sort = SortField.Manual,
            Descending = false,
        });

        _subjectNames.Clear();
        var subjectEntries = new List<SidebarEntry>();

        foreach (var subject in subjects)
        {
            _subjectNames[subject.Id.Value] = subject.Title;

            // The count is what is still outstanding for that subject, not everything ever
            // filed under it — a subject with forty finished assignments is not "40" to do.
            var outstanding = await repository.CountAsync(new CampusQuery
            {
                SubjectIds = { subject.Id },
                Kinds = { ObjectKind.Task, ObjectKind.Assignment, ObjectKind.Requirement },
                Statuses = { ObjectStatus.None, ObjectStatus.NotStarted, ObjectStatus.InProgress },
            });

            subjectEntries.Add(new SidebarEntry
            {
                Key = $"subject:{subject.Id.Value}",
                Label = subject.Title,
                AccentToken = ThemeTokens.Subject.FromName(
                    subject.PayloadAs<SubjectPayload>()?.AccentName),
                Count = outstanding,
            });
        }

        SubjectList.ItemsSource = subjectEntries;
        SubjectSection.Visibility = subjectEntries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        await LoadCollectionsAsync();
        await LoadTagsAsync();
        UpdateFilterNotice();
    }

    private async Task LoadTagsAsync()
    {
        var tags = new List<SidebarEntry>();

        await using var command = _workspace.Database.CreateCommand("""
            SELECT t.tag, COUNT(*) AS uses
            FROM object_tags t
            JOIN objects o ON o.id = t.object_id
            WHERE o.deleted_at IS NULL
            GROUP BY t.tag
            ORDER BY uses DESC, t.tag
            LIMIT 12;
            """);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var name = reader.GetString(0);
            tags.Add(new SidebarEntry
            {
                Key = $"tag:{name}",
                Label = "#" + name,
                Symbol = CampusSymbols.Tag,
                Count = reader.GetInt32(1),
            });
        }

        TagList.ItemsSource = tags;
        TagSection.Visibility = tags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// The searches somebody kept. A saved search is stored as the question, so its row shows how
    /// many things match it right now rather than how many matched on the day it was saved.
    /// </summary>
    private async Task LoadCollectionsAsync()
    {
        var saved = await _workspace.SavedQueries.AllAsync();
        var entries = new List<SidebarEntry>();

        foreach (var collection in saved)
        {
            entries.Add(new SidebarEntry
            {
                Key = $"saved:{collection.Id.Value}",
                Label = collection.Name,
                Symbol = collection.IconName ?? CampusSymbols.Collection,
                Count = await _workspace.Objects.CountAsync(collection.Query),
            });
        }

        CollectionList.ItemsSource = entries;
        CollectionSection.Visibility = entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _saved = saved;
    }

    private IReadOnlyList<Campus.Storage.SavedQuery> _saved = [];

    private void UpdateFilterNotice()
    {
        if (!_navigation.HasFilter)
        {
            FilterNotice.Visibility = Visibility.Collapsed;
            return;
        }

        var parts = new List<string>(2);
        if (_navigation.SubjectId is { } id && _subjectNames.TryGetValue(id.Value, out var name))
            parts.Add(name);
        if (_navigation.Tag is { } tag) parts.Add("#" + tag);

        FilterLabel.Text = $"Showing {string.Join(" · ", parts)}";
        FilterNotice.Visibility = Visibility.Visible;
    }

    private void OnEntryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;

        if (key.StartsWith("subject:", StringComparison.Ordinal))
        {
            var id = CampusId.Parse(key["subject:".Length..]);
            // Clicking the subject you are already filtered to clears the filter, so the row is
            // a toggle rather than a one-way door.
            _navigation.SubjectId = Nullable.Equals(_navigation.SubjectId, id) ? null : id;
        }
        else if (key.StartsWith("saved:", StringComparison.Ordinal))
        {
            var id = key["saved:".Length..];
            var collection = _saved.FirstOrDefault(c => c.Id.Value == id);

            // A saved search opens as a list of its own rather than narrowing the current one:
            // it is a question already, not a filter on somebody else's question.
            if (collection is not null)
            {
                App.GetService<ShellRouter>().GoTo(
                    ShellDestinations.Search, collection.Query.Text ?? collection.Name);
            }
        }
        else if (key.StartsWith("tag:", StringComparison.Ordinal))
        {
            var tag = key["tag:".Length..];
            _navigation.Tag = string.Equals(_navigation.Tag, tag, StringComparison.Ordinal) ? null : tag;
        }
    }

    private void OnClearFilterClick(object sender, RoutedEventArgs e) => _navigation.Clear();

    // ------------------------------------------------- template helper functions

    public static Brush RoleBrush(string? token)
        => (Brush)Application.Current.Resources[token ?? ThemeTokens.Label.Tertiary];

    public static Visibility DotVisibility(string? accentToken)
        => accentToken is null ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility IconVisibility(string? accentToken)
        => accentToken is null ? Visibility.Visible : Visibility.Collapsed;
}
