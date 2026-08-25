using System.Collections.ObjectModel;
using Campus.Desktop.Design;
using Campus.Desktop.Design.Icons;
using Campus.Desktop.Services;
using Campus.Desktop.ViewModels;
using Campus.Domain;
using Campus.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace Campus.Desktop.Views;

/// <summary>
/// Everything that belongs to one subject: its files, its notes, what is due, the lessons that
/// have been taught, and when it meets.
///
/// This is the page a subject card exists to open, and the reason a subject is a real object
/// rather than a label — the same file can be listed here and in the library without two copies
/// of it existing, because both lists are queries.
/// </summary>
public sealed partial class SubjectPage : Page
{
    private readonly WorkspaceService _workspace = App.GetService<WorkspaceService>();
    private readonly ShellRouter _router = App.GetService<ShellRouter>();
    private readonly ObservableCollection<ObjectItem> _items = [];

    private CampusObject? _subject;
    private CampusId _subjectId;

    /// <summary>The tabs, and what each one is a list of.</summary>
    private static readonly (string Name, ObjectKind Kind, string AddLabel, string Empty)[] Tabs =
    [
        ("Files", ObjectKind.File, "Add files", "No files filed under this subject yet."),
        ("Notes", ObjectKind.Note, "New note", "No notes for this subject yet."),
        ("Assignments", ObjectKind.Assignment, "New assignment", "Nothing set for this subject."),
        ("Lessons", ObjectKind.Lesson, "New lesson", "No lessons recorded yet."),
        ("Exams", ObjectKind.Exam, "New exam", "No exams scheduled."),
        ("Books", ObjectKind.Book, "Add book", "No books for this subject."),
        ("Timetable", ObjectKind.Unknown, "Add a lesson time", "This subject has no times set."),
    ];

    public SubjectPage()
    {
        InitializeComponent();
        Items.ItemsSource = _items;

        Segments.Segments = Tabs.Select(t => t.Name).ToList();
        Segments.SelectedIndex = 0;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not CampusId id || !_workspace.IsUnlocked) return;

        _subjectId = id;
        _subject = await _workspace.Objects.GetAsync(id);
        if (_subject is null) return;

        ApplyHeader();
        await ReloadAsync();
    }

    private void ApplyHeader()
    {
        if (_subject is null) return;

        var payload = _subject.PayloadAs<SubjectPayload>();

        TitleText.Text = _subject.Title;
        AccentBlock.Background = (Brush)Application.Current.Resources[
            ThemeTokens.Subject.FromName(payload?.AccentName)];
        SubjectGlyph.Symbol = payload?.IconName ?? CampusSymbols.Subjects;

        var detail = string.Join(" · ", new[]
        {
            payload?.Teacher,
            payload?.Room is { Length: > 0 } room ? $"Room {room}" : null,
            payload?.Code,
        }.Where(v => !string.IsNullOrWhiteSpace(v)));

        DetailText.Text = detail.Length > 0 ? detail : "No teacher or room set";
    }

    private (string Name, ObjectKind Kind, string AddLabel, string Empty) Current
        => Tabs[Math.Clamp(Segments.SelectedIndex, 0, Tabs.Length - 1)];

    private async Task ReloadAsync()
    {
        if (!_workspace.IsUnlocked) return;

        var tab = Current;
        AddLabel.Text = tab.AddLabel;
        DropText.Text = $"Drop to file under {_subject?.Title}";

        if (tab.Name == "Timetable")
        {
            Items.Visibility = Visibility.Collapsed;
            ScheduleSurface.Visibility = Visibility.Visible;
            await LoadScheduleAsync();
            return;
        }

        ScheduleSurface.Visibility = Visibility.Collapsed;
        Items.Visibility = Visibility.Visible;

        var results = await _workspace.Objects.QueryAsync(new CampusQuery
        {
            Kinds = { tab.Kind },
            SubjectIds = { _subjectId },
            Sort = tab.Kind is ObjectKind.Assignment or ObjectKind.Exam
                ? SortField.DueAt
                : SortField.UpdatedAt,
            Descending = tab.Kind is not (ObjectKind.Assignment or ObjectKind.Exam),
        });

        _items.Clear();
        foreach (var model in results) _items.Add(new ObjectItem(model));

        EmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = tab.Empty;
        EmptyGlyph.Symbol = _items.Count == 0 && tab.Kind != ObjectKind.Unknown
            ? new ObjectItem(new CampusObject { Kind = tab.Kind }).Symbol
            : CampusSymbols.Subjects;
    }

    // --------------------------------------------------------------------- timetable

    private async Task LoadScheduleAsync()
    {
        ScheduleList.Children.Clear();

        var slots = await _workspace.Schedule.ForSubjectAsync(_subjectId);

        EmptyState.Visibility = slots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = Current.Empty;

        foreach (var slot in slots) ScheduleList.Children.Add(BuildSlotRow(slot));
    }

    private FrameworkElement BuildSlotRow(ScheduleSlot slot)
    {
        var row = new Grid { ColumnSpacing = 14, Padding = new Thickness(16, 12, 12, 12) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(new TextBlock
        {
            Text = slot.Day.ToString(),
            Style = (Style)Application.Current.Resources["Text.Headline"],
            VerticalAlignment = VerticalAlignment.Center,
        });

        var times = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        times.Children.Add(new TextBlock
        {
            Text = slot.Start.ToString("HH:mm") + " – " + slot.End.ToString("HH:mm"),
            Style = (Style)Application.Current.Resources["Text.Body"],
        });
        times.Children.Add(new TextBlock
        {
            Text = slot.Room is { Length: > 0 } room
                ? $"{room} · {slot.DurationMinutes()} minutes"
                : $"{slot.DurationMinutes()} minutes",
            Style = (Style)Application.Current.Resources["Text.Footnote"],
        });
        Grid.SetColumn(times, 1);
        row.Children.Add(times);

        var remove = new Button
        {
            Style = (Style)Application.Current.Resources["Button.Icon"],
            Content = new CampusIcon
            {
                Symbol = CampusSymbols.Delete,
                IconSize = 16,
                Foreground = (Brush)Application.Current.Resources[ThemeTokens.Label.Tertiary],
            },
            VerticalAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, L.T("remove.this.lesson.time"));
        remove.Click += async (_, _) =>
        {
            await _workspace.Schedule.DeleteAsync(slot.Id);
            await LoadScheduleAsync();
        };
        Grid.SetColumn(remove, 2);
        row.Children.Add(remove);

        return new Border
        {
            Background = (Brush)Application.Current.Resources[ThemeTokens.Surface.Primary],
            CornerRadius = (CornerRadius)Application.Current.Resources["Theme.Radius.Card"],
            Child = row,
        };
    }

    /// <summary>
    /// Asks for one lesson time. Day, start and finish — nothing else, because a timetable entry
    /// that needs a form is a timetable entry nobody will add.
    /// </summary>
    private async Task AddSlotAsync()
    {
        var day = new ComboBox
        {
            Header = "Day",
            SelectedIndex = (int)DateTimeOffset.Now.DayOfWeek,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        foreach (var value in Enum.GetValues<DayOfWeek>()) day.Items.Add(value.ToString());

        var start = new TimePicker { Header = "Starts", ClockIdentifier = "24HourClock",
            Time = new TimeSpan(8, 0, 0) };
        var end = new TimePicker { Header = "Ends", ClockIdentifier = "24HourClock",
            Time = new TimeSpan(8, 45, 0) };
        var room = new TextBox
        {
            Header = "Room",
            PlaceholderText = L.T("optional"),
            Style = (Style)Application.Current.Resources["Input.Text"],
        };

        var body = new StackPanel { Spacing = 12, Width = 320 };
        body.Children.Add(day);
        body.Children.Add(start);
        body.Children.Add(end);
        body.Children.Add(room);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = L.T("lesson.time"),
            Content = body,
            PrimaryButtonText = L.T("add"),
            CloseButtonText = L.T("cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        // A lesson that ends before it starts is a typo, not a lesson that runs past midnight.
        if (end.Time <= start.Time)
        {
            Notifications.Show(L.T("a.lesson.has.to.finish.after.it.starts"), NoticeKind.Warning);
            return;
        }

        await _workspace.Schedule.SaveAsync(new ScheduleSlot
        {
            SubjectId = _subjectId,
            Day = (DayOfWeek)day.SelectedIndex,
            Start = TimeOnly.FromTimeSpan(start.Time),
            End = TimeOnly.FromTimeSpan(end.Time),
            Room = room.Text.Trim() is { Length: > 0 } text ? text : null,
            AcademicYear = _subject?.AcademicYear,
        });

        await LoadScheduleAsync();
    }

    // ------------------------------------------------------------------------ actions

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (Frame?.CanGoBack == true) Frame.GoBack();
        else Frame?.Navigate(typeof(SubjectsPage));
    }

    private async void OnSegmentChanged(object? sender, int index) => await ReloadAsync();

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ObjectItem item) _router.Open(item.Id);
    }

    private void OnItemRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not ObjectItem item) return;

        ObjectCommands.Build(item.Model, XamlRoot, ReloadAsync).ShowAt(Items, e.GetPosition(Items));
        e.Handled = true;
    }

    /// <summary>Edits the things a subject is: who teaches it, where, and what it is called.</summary>
    private async void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (_subject is null) return;

        var payload = _subject.PayloadAs<SubjectPayload>() ?? new SubjectPayload();

        var title = Field("Name", _subject.Title);
        var teacher = Field("Teacher", payload.Teacher ?? "");
        var room = Field("Room", payload.Room ?? "");
        var code = Field("Code", payload.Code ?? "");

        var body = new StackPanel { Spacing = 12, Width = 320 };
        body.Children.Add(title);
        body.Children.Add(teacher);
        body.Children.Add(room);
        body.Children.Add(code);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = L.T("subject"),
            Content = body,
            PrimaryButtonText = L.T("save"),
            CloseButtonText = L.T("cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (title.Text.Trim().Length == 0) return;

        _subject.Title = title.Text.Trim();
        payload.Teacher = Trimmed(teacher.Text);
        payload.Room = Trimmed(room.Text);
        payload.Code = Trimmed(code.Text);
        _subject.Payload = payload;

        await _workspace.Objects.SaveAsync(_subject);
        ApplyHeader();
    }

    private static TextBox Field(string header, string value) => new()
    {
        Header = header,
        Text = value,
        Style = (Style)Application.Current.Resources["Input.Text"],
    };

    private static string? Trimmed(string value)
        => value.Trim() is { Length: > 0 } text ? text : null;

    private async void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (!_workspace.IsUnlocked) return;

        var tab = Current;

        if (tab.Name == "Timetable")
        {
            await AddSlotAsync();
            return;
        }

        if (tab.Kind == ObjectKind.File)
        {
            await ImportAsync();
            return;
        }

        var title = await ObjectCommands.AskAsync(XamlRoot, tab.AddLabel);
        if (title is null) return;

        await _workspace.Objects.SaveAsync(new CampusObject
        {
            Kind = tab.Kind,
            Title = title,
            SubjectId = _subjectId,
            Status = ObjectStatus.NotStarted,
            SourceDeviceId = _workspace.DeviceId,
            Payload = NewPayload(tab.Kind),
        });

        await ReloadAsync();
    }

    private static IObjectPayload? NewPayload(ObjectKind kind) => kind switch
    {
        ObjectKind.Note => new NotePayload(),
        ObjectKind.Assignment => new AssignmentPayload(),
        ObjectKind.Lesson => new LessonPayload(),
        ObjectKind.Exam => new ExamPayload(),
        ObjectKind.Book => new BookPayload(),
        _ => null,
    };

    /// <summary>Brings files in already filed under this subject, which is the point of doing it here.</summary>
    private async Task ImportAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        picker.FileTypeFilter.Add("*");

        var files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0) return;

        var results = await App.GetService<ImportService>()
            .ImportAsync(files.Select(f => f.Path), _subjectId);

        var added = results.Count(r => r.Succeeded);
        Notifications.Show($"Added {added} file{(added == 1 ? "" : "s")} to {_subject?.Title}.",
            NoticeKind.Success);

        await ReloadAsync();
    }

    // ------------------------------------------------------------------ drag and drop

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!_workspace.IsUnlocked || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = $"File under {_subject?.Title}";
        e.DragUIOverride.IsGlyphVisible = false;
        DropHint.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
        => DropHint.Visibility = Visibility.Collapsed;

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        if (!_workspace.IsUnlocked || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.OfType<Windows.Storage.StorageFile>().Select(f => f.Path).ToList();
            if (paths.Count == 0) return;

            var results = await App.GetService<ImportService>().ImportAsync(paths, _subjectId);
            var added = results.Count(r => r.Succeeded);

            Notifications.Show($"Added {added} file{(added == 1 ? "" : "s")}.", NoticeKind.Success);
            await ReloadAsync();
        }
        finally
        {
            deferral.Complete();
        }
    }
}
