using Campus.Desktop.Design;
using Campus.Desktop.Design.Icons;
using Campus.Desktop.Services;
using Campus.Desktop.ViewModels;
using Campus.Domain;
using Campus.Storage;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Campus.Desktop.Views;

/// <summary>
/// Search over the whole workspace, including the text pulled out of documents at import.
///
/// Typing re-runs the search after a short pause rather than on every keystroke: the index is
/// fast, but a query per character on a large workspace is work nobody asked for, and results
/// that reshuffle mid-word are hard to read.
/// </summary>
public sealed partial class SearchPage : Page
{
    private readonly WorkspaceService _workspace = App.GetService<WorkspaceService>();
    private readonly ShellRouter _router = App.GetService<ShellRouter>();
    private readonly DispatcherQueueTimer _debounce;

    private string _query = "";

    /// <summary>The filters, as the kinds each one allows.</summary>
    private static readonly (string Name, ObjectKind[] Kinds)[] Filters =
    [
        ("Everything", []),
        ("Files", [ObjectKind.File, ObjectKind.Book]),
        ("Notes", [ObjectKind.Note, ObjectKind.Lesson]),
        ("Work", [ObjectKind.Assignment, ObjectKind.Task, ObjectKind.Requirement, ObjectKind.Exam]),
        ("Threads", [ObjectKind.Thread, ObjectKind.Board]),
    ];

    public SearchPage()
    {
        InitializeComponent();

        Segments.Segments = Filters.Select(f => f.Name).ToList();
        Segments.SelectedIndex = 0;

        _debounce = DispatcherQueue.CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(180);
        _debounce.IsRepeating = false;
        _debounce.Tick += async (_, _) => await RunAsync();

        Loaded += (_, _) => QueryBox.Focus(FocusState.Programmatic);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // The shell can arrive here with a query already in hand — from the palette, or from a
        // saved search in the sidebar.
        if (e.Parameter is string text && text.Length > 0)
        {
            QueryBox.Text = text;
            _query = text;
            await RunAsync();
        }
    }

    private async Task RunAsync()
    {
        Results.Children.Clear();

        if (!_workspace.IsUnlocked || _query.Length == 0)
        {
            ShowIdle();
            return;
        }

        var filter = Filters[Math.Clamp(Segments.SelectedIndex, 0, Filters.Length - 1)];

        var hits = await _workspace.Search.SearchAsync(
            _query,
            filter.Kinds.Length > 0 ? filter.Kinds : null,
            App.GetService<NavigationState>().SubjectId);

        SaveButton.Visibility = Visibility.Visible;

        if (hits.Count == 0)
        {
            EmptyState.Visibility = Visibility.Visible;
            EmptyGlyph.Symbol = CampusSymbols.Search;
            EmptyTitle.Text = "No matches";
            EmptyMessage.Text = $"Nothing in the workspace mentions “{_query}”.";
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;

        Results.Children.Add(new TextBlock
        {
            Text = $"{hits.Count} result{(hits.Count == 1 ? "" : "s")}",
            Style = (Style)Application.Current.Resources["Text.Caption"],
            Margin = new Thickness(4, 0, 0, 8),
        });

        foreach (var hit in hits) Results.Children.Add(BuildRow(hit));
    }

    private void ShowIdle()
    {
        SaveButton.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
        EmptyGlyph.Symbol = CampusSymbols.Search;
        EmptyTitle.Text = "Search everything";
        EmptyMessage.Text = "Titles, notes, tags, and the text inside every PDF, document and "
            + "slide deck you have imported.";
    }

    private FrameworkElement BuildRow(SearchHit hit)
    {
        var item = new ObjectItem(hit.Object);

        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        row.Children.Add(new CampusIcon
        {
            Symbol = item.Symbol,
            IconSize = 20,
            Foreground = Brush(ThemeTokens.Label.Secondary),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
        });

        var text = new StackPanel { Spacing = 3 };

        text.Children.Add(new TextBlock
        {
            Text = hit.Object.Title,
            FontFamily = Font("Theme.Font.Text"),
            FontSize = 14.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(ThemeTokens.Label.Primary),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        });

        if (hit.Snippet.Length > 0)
        {
            text.Children.Add(Highlighted(hit.Snippet));
        }

        var meta = new List<string> { hit.Object.Kind.ToString() };
        if (item.Subtitle is { Length: > 0 } subtitle) meta.Add(subtitle);
        meta.Add(BoardPage.Ago(hit.Object.UpdatedAt));

        text.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", meta),
            Style = (Style)Application.Current.Resources["Text.Caption"],
        });

        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var button = new Button
        {
            Style = (Style)Application.Current.Resources["Button.Plain"],
            Padding = new Thickness(14, 12, 14, 12),
            CornerRadius = (CornerRadius)Application.Current.Resources["Theme.Radius.M"],
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = row,
        };

        AutomationProperties.SetName(button, hit.Object.Title);
        button.Click += (_, _) => _router.Open(hit.Object.Id);
        button.RightTapped += (_, e) =>
        {
            ObjectCommands.Build(hit.Object, XamlRoot, RunAsync).ShowAt(button, e.GetPosition(button));
            e.Handled = true;
        };

        return button;
    }

    /// <summary>
    /// Draws the matched sentence with the search terms picked out. FTS5 would happily wrap them
    /// in markers of its own, but those would have to be parsed back out of the text — finding
    /// the words here keeps the snippet plain and the highlighting honest.
    /// </summary>
    private TextBlock Highlighted(string snippet)
    {
        var block = new TextBlock
        {
            FontFamily = Font("Theme.Font.Reading"),
            FontSize = 13,
            LineHeight = 19,
            Foreground = Brush(ThemeTokens.Label.Secondary),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 3,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var terms = _query
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 2)
            .ToList();

        var index = 0;
        while (index < snippet.Length)
        {
            var best = -1;
            var bestLength = 0;

            foreach (var term in terms)
            {
                var found = snippet.IndexOf(term, index, StringComparison.OrdinalIgnoreCase);
                if (found >= 0 && (best < 0 || found < best))
                {
                    best = found;
                    bestLength = term.Length;
                }
            }

            if (best < 0) break;

            if (best > index) block.Inlines.Add(new Run { Text = snippet[index..best] });
            block.Inlines.Add(new Run
            {
                Text = snippet.Substring(best, bestLength),
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush(ThemeTokens.Label.Primary),
            });

            index = best + bestLength;
        }

        if (index < snippet.Length) block.Inlines.Add(new Run { Text = snippet[index..] });
        return block;
    }

    // ----------------------------------------------------------------------- actions

    private void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        _query = QueryBox.Text.Trim();
        _debounce.Start();
    }

    private async void OnSegmentChanged(object? sender, int index) => await RunAsync();

    /// <summary>
    /// Keeps this search. A saved search is stored as the question, so it keeps finding new
    /// things that match rather than freezing today's answer.
    /// </summary>
    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_query.Length == 0 || !_workspace.IsUnlocked) return;

        var name = await ObjectCommands.AskAsync(XamlRoot, "Save this search", _query);
        if (name is null) return;

        var filter = Filters[Math.Clamp(Segments.SelectedIndex, 0, Filters.Length - 1)];
        var query = new CampusQuery { Text = _query, Sort = SortField.Relevance };
        foreach (var kind in filter.Kinds) query.Kinds.Add(kind);

        await _workspace.SavedQueries.SaveAsync(new SavedQuery
        {
            Name = name,
            IconName = CampusSymbols.Search,
            Query = query,
        });

        Notifications.Show($"Saved “{name}”. It will keep finding new matches.", NoticeKind.Success);
    }

    private static Brush Brush(string token) => (Brush)Application.Current.Resources[token];
    private static FontFamily Font(string key) => (FontFamily)Application.Current.Resources[key];
}
