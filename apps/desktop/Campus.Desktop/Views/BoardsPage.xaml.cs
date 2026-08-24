using Campus.Desktop.Design;
using Campus.Desktop.Design.Icons;
using Campus.Desktop.Services;
using Campus.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Campus.Desktop.Views;

/// <summary>
/// The boards. Each is a place threads live; opening one shows its threads.
/// </summary>
public sealed partial class BoardsPage : Page
{
    private readonly WorkspaceService _workspace = App.GetService<WorkspaceService>();

    public BoardsPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        List.Children.Clear();
        if (!_workspace.IsUnlocked) return;

        var boards = await _workspace.Objects.QueryAsync(new CampusQuery
        {
            Kinds = { ObjectKind.Board },
            Sort = SortField.Title,
            Descending = false,
        });

        EmptyState.Visibility = boards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Subtitle.Text = boards.Count == 0
            ? "Nothing set up yet"
            : $"{boards.Count} board{(boards.Count == 1 ? "" : "s")}";

        foreach (var board in boards)
        {
            var threads = await _workspace.Objects.CountAsync(new CampusQuery
            {
                Kinds = { ObjectKind.Thread },
                ParentId = board.Id,
            });

            var open = await _workspace.Objects.CountAsync(new CampusQuery
            {
                Kinds = { ObjectKind.Thread },
                ParentId = board.Id,
                Statuses = { ObjectStatus.None, ObjectStatus.NotStarted, ObjectStatus.InProgress },
            });

            List.Children.Add(BuildRow(board, threads, open));
        }
    }

    private FrameworkElement BuildRow(CampusObject board, int threads, int open)
    {
        var payload = board.PayloadAs<BoardPayload>();

        var row = new Grid { ColumnSpacing = 14 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = (CornerRadius)Application.Current.Resources["Theme.Radius.M"],
            Background = Brush(ThemeTokens.Fill.Quaternary),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new CampusIcon
            {
                Symbol = CampusSymbols.Boards,
                IconSize = 20,
                Foreground = Brush(ThemeTokens.Label.Secondary),
            },
        });

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = board.Title,
            Style = (Style)Application.Current.Resources["Text.Headline"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = payload?.Description is { Length: > 0 } description
                ? description
                : "No description",
            Style = (Style)Application.Current.Resources["Text.Footnote"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var counts = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        counts.Children.Add(new TextBlock
        {
            Text = threads == 0 ? "—" : threads.ToString(),
            FontFamily = Font("Theme.Font.Text"),
            FontSize = 17,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = Brush(ThemeTokens.Label.Secondary),
        });
        counts.Children.Add(new TextBlock
        {
            // "Open" is the number that matters; the total is context for it.
            Text = open > 0 ? $"{open} open" : "threads",
            Style = (Style)Application.Current.Resources["Text.Caption"],
            HorizontalAlignment = HorizontalAlignment.Right,
        });
        Grid.SetColumn(counts, 2);
        row.Children.Add(counts);

        var button = new Button
        {
            Style = (Style)Application.Current.Resources["Button.Plain"],
            Background = Brush(ThemeTokens.Surface.Primary),
            CornerRadius = (CornerRadius)Application.Current.Resources["Theme.Radius.Card"],
            Padding = new Thickness(16, 12, 18, 12),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = row,
        };

        AutomationProperties.SetName(button, board.Title);
        button.Click += (_, _) => Frame?.Navigate(typeof(BoardPage), board.Id);
        button.RightTapped += (_, e) =>
        {
            ObjectCommands.Build(board, XamlRoot, ReloadAsync).ShowAt(button, e.GetPosition(button));
            e.Handled = true;
        };

        return button;
    }

    private async void OnNewClick(object sender, RoutedEventArgs e)
    {
        if (!_workspace.IsUnlocked) return;

        var title = await ObjectCommands.AskAsync(XamlRoot, "New board", "", "Physics questions");
        if (title is null) return;

        await _workspace.Objects.SaveAsync(new CampusObject
        {
            Kind = ObjectKind.Board,
            Title = title,
            SourceDeviceId = _workspace.DeviceId,
            Payload = new BoardPayload(),
        });

        await ReloadAsync();
    }

    private static Brush Brush(string token) => (Brush)Application.Current.Resources[token];
    private static FontFamily Font(string key) => (FontFamily)Application.Current.Resources[key];
}
