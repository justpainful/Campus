using Campus.Desktop.Design;
using Campus.Desktop.Design.Icons;
using Campus.Desktop.Services;
using Campus.Domain;
using Campus.Storage;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Campus.Desktop.Views;

/// <summary>
/// The week ahead: lessons from the timetable, and everything due, in the same columns.
///
/// A timetable that only shows lessons answers half the question. What a student actually asks on
/// a Monday night is "what is tomorrow" — which is two lessons, one assignment due and a ruler to
/// remember. So the projection of recurring slots onto real dates happens here, and the things
/// with a due date land on the same day beside them.
/// </summary>
public sealed partial class PlannerPage : Page
{
    private readonly WorkspaceService _workspace = App.GetService<WorkspaceService>();
    private readonly ShellRouter _router = App.GetService<ShellRouter>();

    private DateOnly _anchor = DateOnly.FromDateTime(DateTime.Today);
    private readonly Dictionary<string, (string Name, string Accent)> _subjects = new(StringComparer.Ordinal);

    private static readonly string[] Views = ["Week", "Day", "Agenda"];

    public PlannerPage()
    {
        InitializeComponent();
        Segments.Segments = Views;
        Segments.SelectedIndex = 0;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ReloadAsync();
    }

    /// <summary>
    /// The Sunday of the week the anchor falls in. Sunday-first because that is where the school
    /// week starts here, and the rest of Campus already resolves "this week" the same way.
    /// </summary>
    private DateOnly WeekStart => _anchor.AddDays(-(int)_anchor.DayOfWeek);

    private async Task ReloadAsync()
    {
        Surface.Children.Clear();
        Surface.ColumnDefinitions.Clear();
        Surface.RowDefinitions.Clear();

        if (!_workspace.IsUnlocked) return;

        await LoadSubjectsAsync();

        switch (Segments.SelectedIndex)
        {
            case 1: await BuildDayAsync(); break;
            case 2: await BuildAgendaAsync(); break;
            default: await BuildWeekAsync(); break;
        }
    }

    private async Task LoadSubjectsAsync()
    {
        _subjects.Clear();

        var subjects = await _workspace.Objects.QueryAsync(new CampusQuery
        {
            Kinds = { ObjectKind.Subject },
            Sort = SortField.Manual,
            Descending = false,
        });

        foreach (var subject in subjects)
        {
            _subjects[subject.Id.Value] = (
                subject.Title,
                ThemeTokens.Subject.FromName(subject.PayloadAs<SubjectPayload>()?.AccentName));
        }
    }

    // ------------------------------------------------------------------------- week

    private async Task BuildWeekAsync()
    {
        var start = WeekStart;
        RangeText.Text = $"{start:d MMM} – {start.AddDays(6):d MMM yyyy}";

        for (var i = 0; i < 7; i++)
            Surface.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Surface.ColumnSpacing = 12;
        Surface.MinWidth = 900;

        var slots = await _workspace.Schedule.AllAsync();
        var due = await DueBetweenAsync(start, start.AddDays(7));

        for (var i = 0; i < 7; i++)
        {
            var date = start.AddDays(i);
            var column = BuildDayColumn(
                date,
                slots.Where(s => s.Day == date.DayOfWeek).OrderBy(s => s.StartMinutes()).ToList(),
                due.Where(o => o.DueAt is { } d && DateOnly.FromDateTime(d.LocalDateTime) == date).ToList());

            Grid.SetColumn(column, i);
            Surface.Children.Add(column);
        }
    }

    private FrameworkElement BuildDayColumn(
        DateOnly date, IReadOnlyList<ScheduleSlot> slots, IReadOnlyList<CampusObject> due)
    {
        var isToday = date == DateOnly.FromDateTime(DateTime.Today);
        var column = new StackPanel { Spacing = 8 };

        var header = new StackPanel { Spacing = 1, Margin = new Thickness(2, 0, 2, 6) };
        header.Children.Add(new TextBlock
        {
            Text = date.ToString("ddd").ToUpperInvariant(),
            Style = (Style)Application.Current.Resources["Text.SectionHeader"],
            Foreground = Brush(isToday ? ThemeTokens.Accent.Primary : ThemeTokens.Label.Tertiary),
        });
        header.Children.Add(new TextBlock
        {
            Text = date.Day.ToString(),
            FontFamily = Font("Theme.Font.Text"),
            FontSize = 22,
            FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
            Foreground = Brush(isToday ? ThemeTokens.Accent.Primary : ThemeTokens.Label.Primary),
        });
        column.Children.Add(header);

        foreach (var slot in slots) column.Children.Add(SlotCard(slot));
        foreach (var item in due) column.Children.Add(DueCard(item));

        if (slots.Count == 0 && due.Count == 0)
        {
            column.Children.Add(new TextBlock
            {
                Text = "—",
                Foreground = Brush(ThemeTokens.Label.Quaternary),
                Margin = new Thickness(4, 4, 0, 0),
            });
        }

        // Today's column is tinted rather than outlined: a border around one of seven columns
        // reads as an error state, a wash reads as "you are here".
        return new Border
        {
            Background = isToday ? Brush(ThemeTokens.Fill.Quaternary) : null,
            CornerRadius = (CornerRadius)Application.Current.Resources["Theme.Radius.Card"],
            Padding = new Thickness(8, 10, 8, 14),
            // Hugging its content rather than stretching: a tint down the whole page reads as a
            // selected region, not as "you are here".
            VerticalAlignment = VerticalAlignment.Top,
            Child = column,
        };
    }

    private FrameworkElement SlotCard(ScheduleSlot slot)
    {
        var known = _subjects.TryGetValue(slot.SubjectId.Value, out var subject);
        var accent = Brush(known ? subject.Accent : ThemeTokens.Label.Quaternary);

        var body = new StackPanel { Spacing = 2 };
        body.Children.Add(new TextBlock
        {
            Text = known ? subject.Name : "Lesson",
            FontFamily = Font("Theme.Font.Text"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(ThemeTokens.Label.Primary),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        });
        body.Children.Add(new TextBlock
        {
            Text = slot.Room is { Length: > 0 } room
                ? $"{slot.Start:HH\\:mm} · {room}"
                : slot.Start.ToString("HH:mm"),
            Style = (Style)Application.Current.Resources["Text.Caption"],
        });

        var card = new Grid();
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        card.Children.Add(new Border { Background = accent, CornerRadius = new CornerRadius(2) });
        Grid.SetColumn(body, 1);
        body.Margin = new Thickness(9, 0, 4, 0);
        card.Children.Add(body);

        var button = new Button
        {
            Style = (Style)Application.Current.Resources["Button.Plain"],
            Background = Brush(ThemeTokens.Surface.Primary),
            CornerRadius = (CornerRadius)Application.Current.Resources["Theme.Radius.S"],
            Padding = new Thickness(6, 8, 6, 8),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = card,
        };

        AutomationProperties.SetName(button,
            $"{(known ? subject.Name : "Lesson")} at {slot.Start:HH\\:mm}");

        if (known) button.Click += (_, _) => Frame?.Navigate(typeof(SubjectPage), slot.SubjectId);
        return button;
    }

    private FrameworkElement DueCard(CampusObject entity)
    {
        var item = new ViewModels.ObjectItem(entity);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        row.Children.Add(new CampusIcon
        {
            Symbol = item.Symbol,
            IconSize = 14,
            Foreground = Brush(ThemeTokens.Label.Secondary),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
        });
        row.Children.Add(new TextBlock
        {
            Text = entity.Title,
            FontSize = 12.5,
            Foreground = Brush(ThemeTokens.Label.Primary),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 160,
        });

        var button = new Button
        {
            Style = (Style)Application.Current.Resources["Button.Plain"],
            Padding = new Thickness(6, 7, 6, 7),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = (CornerRadius)Application.Current.Resources["Theme.Radius.S"],
            Content = row,
        };

        AutomationProperties.SetName(button, entity.Title);
        button.Click += (_, _) => _router.Open(entity.Id);
        button.RightTapped += (_, e) =>
        {
            ObjectCommands.Build(entity, XamlRoot, ReloadAsync).ShowAt(button, e.GetPosition(button));
            e.Handled = true;
        };

        return button;
    }

    // -------------------------------------------------------------------------- day

    private async Task BuildDayAsync()
    {
        RangeText.Text = _anchor.ToString("dddd d MMMM yyyy");

        Surface.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Surface.MinWidth = 0;

        var slots = (await _workspace.Schedule.ForDayAsync(_anchor.DayOfWeek))
            .OrderBy(s => s.StartMinutes()).ToList();
        var due = await DueBetweenAsync(_anchor, _anchor.AddDays(1));

        var stack = new StackPanel { Spacing = 10, MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Left };

        if (slots.Count > 0)
        {
            stack.Children.Add(SectionHeader("Lessons"));
            foreach (var slot in slots) stack.Children.Add(SlotCard(slot));
        }

        if (due.Count > 0)
        {
            stack.Children.Add(SectionHeader("Due"));
            foreach (var item in due) stack.Children.Add(DueCard(item));
        }

        if (slots.Count == 0 && due.Count == 0) stack.Children.Add(Nothing("Nothing on this day."));

        Surface.Children.Add(stack);
    }

    // ------------------------------------------------------------------------ agenda

    /// <summary>
    /// The next fortnight as a flat list, grouped by day and skipping the days with nothing on
    /// them — which is the view that answers "what is coming" rather than "what does a week
    /// look like".
    /// </summary>
    private async Task BuildAgendaAsync()
    {
        var start = DateOnly.FromDateTime(DateTime.Today);
        var end = start.AddDays(14);
        RangeText.Text = L.T("next.two.weeks");

        Surface.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Surface.MinWidth = 0;

        var slots = await _workspace.Schedule.AllAsync();
        var due = await DueBetweenAsync(start, end);

        var stack = new StackPanel { Spacing = 8, MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Left };
        var anything = false;

        for (var date = start; date < end; date = date.AddDays(1))
        {
            var daySlots = slots.Where(s => s.Day == date.DayOfWeek).OrderBy(s => s.StartMinutes()).ToList();
            var dayDue = due
                .Where(o => o.DueAt is { } d && DateOnly.FromDateTime(d.LocalDateTime) == date)
                .ToList();

            if (daySlots.Count == 0 && dayDue.Count == 0) continue;
            anything = true;

            stack.Children.Add(SectionHeader(date == start
                ? $"Today · {date:d MMMM}"
                : date.ToString("dddd d MMMM")));

            foreach (var slot in daySlots) stack.Children.Add(SlotCard(slot));
            foreach (var item in dayDue) stack.Children.Add(DueCard(item));
        }

        if (!anything) stack.Children.Add(Nothing("Nothing scheduled in the next two weeks."));
        Surface.Children.Add(stack);
    }

    // ------------------------------------------------------------------------ shared

    private async Task<IReadOnlyList<CampusObject>> DueBetweenAsync(DateOnly from, DateOnly to)
    {
        return await _workspace.Objects.QueryAsync(new CampusQuery
        {
            Kinds =
            {
                ObjectKind.Assignment, ObjectKind.Task, ObjectKind.Requirement,
                ObjectKind.Exam, ObjectKind.Event,
            },
            Due = new DateRange(
                new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), DateTimeOffset.Now.Offset),
                new DateTimeOffset(to.ToDateTime(TimeOnly.MinValue), DateTimeOffset.Now.Offset)),
            Sort = SortField.DueAt,
            Descending = false,
        });
    }

    private static FrameworkElement SectionHeader(string text) => new TextBlock
    {
        Text = text.ToUpperInvariant(),
        Style = (Style)Application.Current.Resources["Text.SectionHeader"],
        Margin = new Thickness(2, 14, 0, 2),
    };

    private static FrameworkElement Nothing(string text) => new TextBlock
    {
        Text = text,
        Style = (Style)Application.Current.Resources["Text.Callout"],
        Margin = new Thickness(2, 24, 0, 0),
    };

    private static Brush Brush(string token) => (Brush)Application.Current.Resources[token];
    private static FontFamily Font(string key) => (FontFamily)Application.Current.Resources[key];

    // ----------------------------------------------------------------------- actions

    private async void OnSegmentChanged(object? sender, int index) => await ReloadAsync();

    private async void OnPreviousClick(object sender, RoutedEventArgs e)
    {
        _anchor = _anchor.AddDays(Segments.SelectedIndex == 1 ? -1 : -7);
        await ReloadAsync();
    }

    private async void OnNextClick(object sender, RoutedEventArgs e)
    {
        _anchor = _anchor.AddDays(Segments.SelectedIndex == 1 ? 1 : 7);
        await ReloadAsync();
    }

    private async void OnTodayClick(object sender, RoutedEventArgs e)
    {
        _anchor = DateOnly.FromDateTime(DateTime.Today);
        await ReloadAsync();
    }
}
