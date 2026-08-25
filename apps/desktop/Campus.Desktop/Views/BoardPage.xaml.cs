using Campus.Desktop.Design;
using Campus.Desktop.Design.Icons;
using Campus.Desktop.Services;
using Campus.Domain;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Campus.Desktop.Views;

/// <summary>
/// One board's threads.
///
/// A thread is either open or resolved, and the list says which at a glance — the point of
/// writing a question down is being able to see later that it never got answered.
/// </summary>
public sealed partial class BoardPage : Page
{
    private readonly WorkspaceService _workspace = App.GetService<WorkspaceService>();

    private CampusObject? _board;
    private CampusId _boardId;
    private string _filter = "";

    private static readonly string[] Views = ["Open", "Resolved", "All"];

    public BoardPage()
    {
        InitializeComponent();
        Segments.Segments = Views;
        Segments.SelectedIndex = 0;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not CampusId id || !_workspace.IsUnlocked) return;

        _boardId = id;
        _board = await _workspace.Objects.GetAsync(id);
        if (_board is null) return;

        TitleText.Text = _board.Title;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        List.Children.Clear();
        if (!_workspace.IsUnlocked) return;

        var query = new CampusQuery
        {
            Kinds = { ObjectKind.Thread },
            ParentId = _boardId,
            Sort = SortField.UpdatedAt,
        };

        switch (Segments.SelectedIndex)
        {
            case 0:
                query.Statuses.Add(ObjectStatus.None);
                query.Statuses.Add(ObjectStatus.NotStarted);
                query.Statuses.Add(ObjectStatus.InProgress);
                break;
            case 1:
                query.Statuses.Add(ObjectStatus.Completed);
                break;
        }

        if (_filter.Length > 0) query.Text = _filter;

        var threads = await _workspace.Objects.QueryAsync(query);

        EmptyState.Visibility = threads.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _filter.Length > 0
            ? $"No thread here matches “{_filter}”."
            : Segments.SelectedIndex == 1
                ? "Nothing has been marked resolved yet."
                : "No threads here yet. Start one with the question you actually have.";

        var description = _board?.PayloadAs<BoardPayload>()?.Description;
        Subtitle.Text = threads.Count == 0
            ? description ?? ""
            : $"{threads.Count} thread{(threads.Count == 1 ? "" : "s")}"
              + (description is { Length: > 0 } ? $" · {description}" : "");

        foreach (var thread in threads) List.Children.Add(BuildRow(thread));
    }

    private FrameworkElement BuildRow(CampusObject thread)
    {
        var payload = thread.PayloadAs<ThreadPayload>();
        var resolved = thread.Status == ObjectStatus.Completed || payload?.Resolved == true;

        var row = new Grid { ColumnSpacing = 14 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(new CampusIcon
        {
            Symbol = resolved ? CampusSymbols.CircleCheck : CampusSymbols.Thread,
            IconSize = 20,
            Foreground = Brush(resolved ? ThemeTokens.Success.Primary : ThemeTokens.Label.Secondary),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };

        var title = new TextBlock
        {
            Text = thread.Title,
            FontFamily = Font("Theme.Font.Text"),
            FontSize = 14.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(resolved ? ThemeTokens.Label.Secondary : ThemeTokens.Label.Primary),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };
        text.Children.Add(title);

        var meta = new List<string>();
        if (payload?.MessageCount is > 0)
            meta.Add($"{payload.MessageCount} repl{(payload.MessageCount == 1 ? "y" : "ies")}");
        meta.Add(Ago(payload?.LastActivityAt ?? thread.UpdatedAt));
        if (payload?.Locked == true) meta.Add("locked");

        text.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", meta),
            Style = (Style)Application.Current.Resources["Text.Caption"],
        });

        if (thread.Tags.Count > 0)
        {
            var tags = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(0, 2, 0, 0),
            };
            foreach (var tag in thread.Tags.Take(4)) tags.Children.Add(Chip(tag));
            text.Children.Add(tags);
        }

        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        if (resolved)
        {
            var badge = Chip("Resolved", ThemeTokens.Success.Subtle, ThemeTokens.Success.Primary);
            badge.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(badge, 2);
            row.Children.Add(badge);
        }

        var button = new Button
        {
            Style = (Style)Application.Current.Resources["Button.Plain"],
            Background = Brush(ThemeTokens.Surface.Primary),
            CornerRadius = (CornerRadius)Application.Current.Resources["Theme.Radius.Card"],
            Padding = new Thickness(16, 12, 16, 12),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = row,
        };

        AutomationProperties.SetName(button, thread.Title);
        button.Click += (_, _) => Frame?.Navigate(typeof(ThreadPage), thread.Id);
        button.RightTapped += (_, e) =>
        {
            var menu = ObjectCommands.Build(thread, XamlRoot, ReloadAsync);

            menu.Items.Insert(1, ObjectCommands.Item(
                resolved ? "Reopen" : "Mark resolved",
                resolved ? CampusSymbols.Undo : CampusSymbols.Check,
                async () =>
                {
                    var updated = thread.PayloadAs<ThreadPayload>() ?? new ThreadPayload();
                    updated.Resolved = !resolved;
                    thread.Payload = updated;
                    thread.Status = resolved ? ObjectStatus.InProgress : ObjectStatus.Completed;
                    thread.CompletedAt = resolved ? null : DateTimeOffset.UtcNow;

                    await _workspace.Objects.SaveAsync(thread);
                    await ReloadAsync();
                }));

            menu.ShowAt(button, e.GetPosition(button));
            e.Handled = true;
        };

        return button;
    }

    private static Border Chip(string text, string? background = null, string? foreground = null)
        => new()
        {
            Background = Brush(background ?? ThemeTokens.Fill.Quaternary),
            CornerRadius = (CornerRadius)Application.Current.Resources["Theme.Radius.Pill"],
            Padding = new Thickness(8, 2, 8, 3),
            Child = new TextBlock
            {
                Text = text,
                FontFamily = Font("Theme.Font.Small"),
                FontSize = 11,
                Foreground = Brush(foreground ?? ThemeTokens.Label.Secondary),
            },
        };

    /// <summary>
    /// How long ago, in the words a person would use. Exact timestamps are for the detail page.
    /// </summary>
    public static string Ago(DateTimeOffset when)
    {
        var span = DateTimeOffset.Now - when.ToLocalTime();

        return span switch
        {
            { TotalMinutes: < 2 } => L.T("ago.now"),
            { TotalMinutes: < 60 } => Plural.Of("ago.minutes", (int)span.TotalMinutes),
            { TotalHours: < 24 } => Plural.Of("ago.hours", (int)span.TotalHours),
            { TotalDays: < 7 } => Plural.Of("ago.days", (int)span.TotalDays),
            { TotalDays: < 60 } => Plural.Of("ago.weeks", (int)(span.TotalDays / 7)),
            _ => when.ToLocalTime().ToString("d MMM yyyy"),
        };
    }

    // ----------------------------------------------------------------------- actions

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (Frame?.CanGoBack == true) Frame.GoBack();
        else Frame?.Navigate(typeof(BoardsPage));
    }

    private async void OnSegmentChanged(object? sender, int index) => await ReloadAsync();

    private async void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        _filter = FilterBox.Text.Trim();
        await ReloadAsync();
    }

    private async void OnNewThreadClick(object sender, RoutedEventArgs e)
    {
        if (!_workspace.IsUnlocked) return;

        var title = await ObjectCommands.AskAsync(
            XamlRoot, "New thread", "", "Why does the sign flip here?");
        if (title is null) return;

        var thread = new CampusObject
        {
            Kind = ObjectKind.Thread,
            Title = title,
            ParentId = _boardId,
            SubjectId = _board?.SubjectId,
            Status = ObjectStatus.NotStarted,
            SourceDeviceId = _workspace.DeviceId,
            Payload = new ThreadPayload { LastActivityAt = DateTimeOffset.UtcNow },
        };

        await _workspace.Objects.SaveAsync(thread);
        Frame?.Navigate(typeof(ThreadPage), thread.Id);
    }

    private static Brush Brush(string token) => (Brush)Application.Current.Resources[token];
    private static FontFamily Font(string key) => (FontFamily)Application.Current.Resources[key];
}
