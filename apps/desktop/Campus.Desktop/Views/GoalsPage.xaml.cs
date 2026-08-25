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
/// Goals, with their steps.
///
/// Progress is computed from the steps rather than typed in, because a number somebody sets by
/// hand stops being true the moment they stop maintaining it. Ticking a step is the only way the
/// bar moves, which makes the bar worth looking at.
/// </summary>
public sealed partial class GoalsPage : Page
{
    private readonly WorkspaceService _workspace = App.GetService<WorkspaceService>();

    public GoalsPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        List.Children.Clear();
        if (!_workspace.IsUnlocked) return;

        var goals = await _workspace.Objects.QueryAsync(new CampusQuery
        {
            Kinds = { ObjectKind.Goal },
            Sort = SortField.UpdatedAt,
        });

        EmptyState.Visibility = goals.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var finished = goals.Count(g => Progress(g) >= 1);
        Subtitle.Text = goals.Count == 0
            ? "Nothing set yet"
            : finished > 0
                ? $"{goals.Count} goals · {finished} reached"
                : $"{goals.Count} goal{(goals.Count == 1 ? "" : "s")}";

        foreach (var goal in goals) List.Children.Add(BuildCard(goal));
    }

    private static double Progress(CampusObject goal)
    {
        var payload = goal.PayloadAs<GoalPayload>();
        if (payload is null) return 0;

        // Steps win over the stored number: a goal with a checklist is measured by its checklist.
        if (payload.Steps.Count > 0)
            return payload.Steps.Count(s => s.Done) / (double)payload.Steps.Count;

        return Math.Clamp(payload.Progress, 0, 1);
    }

    private FrameworkElement BuildCard(CampusObject goal)
    {
        var payload = goal.PayloadAs<GoalPayload>() ?? new GoalPayload();
        var progress = Progress(goal);
        var done = progress >= 1;

        var body = new StackPanel { Spacing = 12 };

        // ---- header
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titles = new StackPanel { Spacing = 2 };
        titles.Children.Add(new TextBlock
        {
            Text = goal.Title,
            Style = (Style)Application.Current.Resources["Text.Headline"],
            TextWrapping = TextWrapping.Wrap,
        });

        var detail = new List<string>();
        if (payload.Detail is { Length: > 0 } text) detail.Add(text);
        if (payload.TargetDate is { } target)
        {
            var days = (target.Date - DateTimeOffset.Now.Date).Days;
            detail.Add(days switch
            {
                < 0 when !done => $"{-days} days past its date",
                0 => "Today",
                1 => "Tomorrow",
                _ => $"{days} days left",
            });
        }

        if (detail.Count > 0)
        {
            titles.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", detail),
                Style = (Style)Application.Current.Resources["Text.Footnote"],
                TextWrapping = TextWrapping.Wrap,
            });
        }

        header.Children.Add(titles);

        var percent = new TextBlock
        {
            Text = $"{progress * 100:0}%",
            FontFamily = Font("Theme.Font.Text"),
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush(done ? ThemeTokens.Success.Primary : ThemeTokens.Label.Secondary),
        };
        Grid.SetColumn(percent, 1);
        header.Children.Add(percent);

        body.Children.Add(header);

        // ---- the bar
        var track = new Border
        {
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = Brush(ThemeTokens.Fill.Tertiary),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var fill = new Border
        {
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = Brush(done ? ThemeTokens.Success.Primary : ThemeTokens.Accent.Primary),
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var bar = new Grid();
        bar.Children.Add(track);
        bar.Children.Add(fill);

        // The fill is sized against the track's real width, so it stays right when the window
        // is resized rather than only when the page is built.
        bar.SizeChanged += (_, args) => fill.Width = args.NewSize.Width * progress;

        AutomationProperties.SetName(bar, $"{progress * 100:0} percent complete");
        body.Children.Add(bar);

        // ---- steps
        if (payload.Steps.Count > 0)
        {
            var steps = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4, 0, 0) };
            foreach (var step in payload.Steps.OrderBy(s => s.SortOrder))
                steps.Children.Add(StepRow(goal, payload, step));
            body.Children.Add(steps);
        }

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 4, 0, 0),
        };

        var addStep = new Button
        {
            Style = (Style)Application.Current.Resources["Button.Plain"],
            Padding = new Thickness(8, 5, 10, 5),
            Content = Row(CampusSymbols.Add, "Add a step"),
        };
        addStep.Click += async (_, _) => await AddStepAsync(goal, payload);
        actions.Children.Add(addStep);

        body.Children.Add(actions);

        var card = new Border
        {
            Background = Brush(ThemeTokens.Surface.Primary),
            CornerRadius = (CornerRadius)Application.Current.Resources["Theme.Radius.Card"],
            Padding = new Thickness(18, 16, 18, 16),
            Child = body,
        };

        card.RightTapped += (_, e) =>
        {
            ObjectCommands.Build(goal, XamlRoot, ReloadAsync).ShowAt(card, e.GetPosition(card));
            e.Handled = true;
        };

        return card;
    }

    private static FrameworkElement Row(string symbol, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        row.Children.Add(new CampusIcon
        {
            Symbol = symbol,
            IconSize = 15,
            Foreground = Brush(ThemeTokens.Label.Secondary),
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(new TextBlock
        {
            Text = text,
            Style = (Style)Application.Current.Resources["Text.Footnote"],
            VerticalAlignment = VerticalAlignment.Center,
        });
        return row;
    }

    private FrameworkElement StepRow(CampusObject goal, GoalPayload payload, ChecklistItem step)
    {
        var box = new CheckBox
        {
            IsChecked = step.Done,
            Content = new TextBlock
            {
                Text = step.Text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush(step.Done ? ThemeTokens.Label.Tertiary : ThemeTokens.Label.Primary),
                TextDecorations = step.Done
                    ? Windows.UI.Text.TextDecorations.Strikethrough
                    : Windows.UI.Text.TextDecorations.None,
            },
            MinWidth = 0,
            Padding = new Thickness(8, 0, 0, 0),
        };

        box.Click += async (_, _) =>
        {
            step.Done = box.IsChecked == true;

            // A goal whose every step is ticked is finished, and says so without being told.
            payload.Progress = payload.Steps.Count == 0
                ? payload.Progress
                : payload.Steps.Count(s => s.Done) / (double)payload.Steps.Count;

            goal.Status = payload.Progress >= 1 ? ObjectStatus.Completed : ObjectStatus.InProgress;
            goal.CompletedAt = payload.Progress >= 1 ? DateTimeOffset.UtcNow : null;
            goal.Payload = payload;

            await _workspace.Objects.SaveAsync(goal);
            await ReloadAsync();
        };

        box.RightTapped += async (_, e) =>
        {
            e.Handled = true;
            if (!await ObjectCommands.ConfirmAsync(XamlRoot, L.T("remove.this.step"),
                step.Text, "Remove")) return;

            payload.Steps.Remove(step);
            goal.Payload = payload;
            await _workspace.Objects.SaveAsync(goal);
            await ReloadAsync();
        };

        return box;
    }

    private async Task AddStepAsync(CampusObject goal, GoalPayload payload)
    {
        var text = await ObjectCommands.AskAsync(XamlRoot, L.T("add.a.step"), "", "Finish chapter 4");
        if (text is null) return;

        payload.Steps.Add(new ChecklistItem
        {
            Text = text,
            SortOrder = payload.Steps.Count,
        });

        goal.Payload = payload;
        if (goal.Status == ObjectStatus.None) goal.Status = ObjectStatus.InProgress;

        await _workspace.Objects.SaveAsync(goal);
        await ReloadAsync();
    }

    private async void OnNewClick(object sender, RoutedEventArgs e)
    {
        if (!_workspace.IsUnlocked) return;

        var title = await ObjectCommands.AskAsync(
            XamlRoot, "New goal", "", "Finish the physics textbook");
        if (title is null) return;

        await _workspace.Objects.SaveAsync(new CampusObject
        {
            Kind = ObjectKind.Goal,
            Title = title,
            Status = ObjectStatus.NotStarted,
            SourceDeviceId = _workspace.DeviceId,
            Payload = new GoalPayload(),
        });

        await ReloadAsync();
    }

    private static Brush Brush(string token) => (Brush)Application.Current.Resources[token];
    private static FontFamily Font(string key) => (FontFamily)Application.Current.Resources[key];
}
