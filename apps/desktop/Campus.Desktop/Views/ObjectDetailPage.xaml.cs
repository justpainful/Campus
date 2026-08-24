using Campus.Desktop.Design;
using Campus.Desktop.Design.Emoji;
using Campus.Desktop.Design.Icons;
using Campus.Desktop.Services;
using Campus.Desktop.ViewModels;
using Campus.Domain;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Campus.Desktop.Views;

/// <summary>
/// One object, open and editable.
///
/// There is no Save button. Every field writes back after a short pause, because an app you use
/// during a lesson should not ask you to confirm that you meant the thing you just typed. The
/// pause exists so that typing a title is one write rather than one per keystroke.
/// </summary>
public sealed partial class ObjectDetailPage : Page
{
    private readonly WorkspaceService _workspace = App.GetService<WorkspaceService>();
    private readonly DispatcherQueueTimer _saveTimer;

    private CampusObject? _model;
    private List<CampusObject> _subjects = [];
    private bool _loading = true;

    // Kind-specific fields, kept by name so saving does not have to walk the visual tree.
    private readonly Dictionary<string, FrameworkElement> _extraFields = new(StringComparer.Ordinal);

    public ObjectDetailPage()
    {
        InitializeComponent();

        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(600);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += async (_, _) => await SaveAsync();

        // Emoji belong in a note, so the body gets a picker.
        BodyToolbar.Children.Add(EmojiFlyout.CreateButton(BodyBox));
    }

    protected override async void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not CampusId id || !_workspace.IsUnlocked) return;

        _model = await _workspace.Objects.GetAsync(id);
        if (_model is null) return;

        _subjects = (await _workspace.Objects.QueryAsync(new CampusQuery
        {
            Kinds = { ObjectKind.Subject },
            Sort = SortField.Manual,
            Descending = false,
        })).ToList();

        Load();
        await _workspace.Objects.MarkOpenedAsync(id);
        await LoadHistoryAsync();
    }

    protected override async void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // Leaving the page must not lose the last few keystrokes.
        _saveTimer.Stop();
        await SaveAsync();
    }

    // ------------------------------------------------------------------------ loading

    private void Load()
    {
        if (_model is null) return;
        _loading = true;

        var item = new ObjectItem(_model);
        KindIcon.Symbol = item.Symbol;
        KindLabel.Text = Humanise(_model.Kind);

        TitleBox.Text = _model.Title;
        BodyBox.Text = ReadBody(_model);
        BodyHeader.Text = _model.Kind == ObjectKind.Note ? "NOTE" : "NOTES";

        StatusChoice.SelectedIndex = _model.Status switch
        {
            ObjectStatus.InProgress => 1,
            ObjectStatus.Completed => 2,
            ObjectStatus.Blocked => 3,
            ObjectStatus.Waiting => 4,
            _ => 0,
        };
        PriorityChoice.SelectedIndex = (int)_model.Priority;

        SubjectChoice.Items.Clear();
        SubjectChoice.Items.Add(new ComboBoxItem { Content = "No subject", Tag = null });
        foreach (var subject in _subjects)
            SubjectChoice.Items.Add(new ComboBoxItem { Content = subject.Title, Tag = subject.Id });
        SubjectChoice.SelectedIndex = _model.SubjectId is { } subjectId
            ? Math.Max(0, _subjects.FindIndex(s => s.Id == subjectId) + 1)
            : 0;

        DuePicker.Date = _model.DueAt;
        ClearDueButton.Visibility = _model.DueAt is null ? Visibility.Collapsed : Visibility.Visible;

        UpdateSubtitle();
        UpdateFlagIcons();
        BuildKindFields();
        BuildChecklist();
        BuildTags();

        _loading = false;
    }

    private void UpdateSubtitle()
    {
        if (_model is null) return;

        var subject = _model.SubjectId is { } id
            ? _subjects.FirstOrDefault(s => s.Id == id)
            : null;

        var accent = subject is null
            ? ThemeTokens.Label.Quaternary
            : ThemeTokens.Subject.FromName(subject.PayloadAs<SubjectPayload>()?.AccentName);

        SubjectDot.Fill = (Brush)Application.Current.Resources[accent];

        var parts = new List<string>();
        if (subject is not null) parts.Add(subject.Title);
        parts.Add(Humanise(_model.Kind));
        if (_model.DueAt is { } due) parts.Add($"due {ObjectItem.FormatRelativeDay(due)}");

        SubtitleText.Text = string.Join(" · ", parts);
    }

    private void UpdateFlagIcons()
    {
        if (_model is null) return;

        FavoriteIcon.Variant = _model.IsFavorite ? IconVariant.Filled : IconVariant.Outline;
        FavoriteIcon.Foreground = (Brush)Application.Current.Resources[
            _model.IsFavorite ? ThemeTokens.Warning.Primary : ThemeTokens.Label.Secondary];

        PinIcon.Variant = _model.IsPinned ? IconVariant.Filled : IconVariant.Outline;
        PinIcon.Foreground = (Brush)Application.Current.Resources[
            _model.IsPinned ? ThemeTokens.Accent.Primary : ThemeTokens.Label.Secondary];
    }

    // -------------------------------------------------------------- kind-specific fields

    /// <summary>
    /// Builds the fields that only some kinds have — a teacher and a mark for an assignment, an
    /// author and a page count for a book. Built rather than declared, because declaring every
    /// field for every kind and hiding most of them is how a detail page becomes unreadable.
    /// </summary>
    private void BuildKindFields()
    {
        KindFields.Children.Clear();
        _extraFields.Clear();

        if (_model is null) return;

        switch (_model.Payload)
        {
            case AssignmentPayload assignment:
                KindSection.Header = "ASSIGNMENT";
                AddText("teacher", "Teacher", CampusSymbols.Teacher, assignment.Teacher);
                AddNumber("points", "Points", CampusSymbols.Chart, assignment.Points);
                AddNumber("earned", "Marked", CampusSymbols.Success, assignment.EarnedPoints);
                AddText("instructions", "Instructions", CampusSymbols.Notes, assignment.Instructions);
                break;

            case RequirementPayload requirement:
                KindSection.Header = "REQUIREMENT";
                AddText("action", "What to do", CampusSymbols.Requirements, requirement.Action);
                AddText("teacher", "Teacher", CampusSymbols.Teacher, requirement.Teacher);
                AddToggle("printing", "Needs printing", CampusSymbols.PrintCenter, requirement.RequiresPrinting);
                break;

            case BookPayload book:
                KindSection.Header = "BOOK";
                AddText("author", "Author", CampusSymbols.Person, book.Author);
                AddText("edition", "Edition", CampusSymbols.Versions, book.Edition);
                AddNumber("currentPage", "Page", CampusSymbols.Book, book.CurrentPage);
                AddNumber("totalPages", "Of", CampusSymbols.Layers, book.TotalPages);
                AddToggle("solution", "Solutions book", CampusSymbols.Check, book.IsSolutionBook);
                break;

            case LinkPayload link:
                KindSection.Header = "LINK";
                AddText("url", "Address", CampusSymbols.Link, link.Url);
                AddText("description", "Description", CampusSymbols.Notes, link.Description);
                break;

            case PrintJobPayload print:
                KindSection.Header = "PRINT JOB";
                AddNumber("pages", "Pages", CampusSymbols.Files, print.Pages);
                AddNumber("copies", "Copies", CampusSymbols.Duplicate, print.Copies);
                AddToggle("duplex", "Double-sided", CampusSymbols.Print, print.DoubleSided);
                AddToggle("colour", "Colour", CampusSymbols.Palette, print.ColorMode == PrintColorMode.Color);
                break;

            case ExamPayload exam:
                KindSection.Header = "EXAM";
                AddText("scope", "Covers", CampusSymbols.Library, exam.Scope);
                AddText("location", "Where", CampusSymbols.SchoolFiles, exam.Location);
                AddNumber("maxScore", "Out of", CampusSymbols.Chart, exam.MaxScore);
                AddNumber("score", "Scored", CampusSymbols.Success, exam.Score);
                break;

            case InboxPayload:
                KindSection.Header = "INBOX";
                AddConvertRow();
                break;

            default:
                KindSection.Visibility = Visibility.Collapsed;
                return;
        }

        KindSection.Visibility = KindFields.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private SettingsRowShell NewRow(string title, string symbol)
    {
        var row = new Design.Controls.SettingsRow
        {
            Title = title,
            Symbol = symbol,
            ShowSeparator = KindFields.Children.Count > 0,
        };
        return new SettingsRowShell(row);
    }

    private readonly record struct SettingsRowShell(Design.Controls.SettingsRow Row);

    private void AddText(string key, string title, string symbol, string? value)
    {
        var box = new TextBox
        {
            Text = value ?? string.Empty,
            MinWidth = 220,
            Style = (Style)Application.Current.Resources["Input.Text"],
        };
        AutomationProperties.SetName(box, title);
        box.TextChanged += OnFieldChanged;

        var row = NewRow(title, symbol).Row;
        row.Content = box;
        KindFields.Children.Add(row);
        _extraFields[key] = box;
    }

    private void AddNumber(string key, string title, string symbol, double? value)
    {
        var box = new NumberBox
        {
            Value = value ?? double.NaN,
            MinWidth = 140,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        AutomationProperties.SetName(box, title);
        box.ValueChanged += (_, _) => OnFieldChanged(box, null!);

        var row = NewRow(title, symbol).Row;
        row.Content = box;
        KindFields.Children.Add(row);
        _extraFields[key] = box;
    }

    private void AddNumber(string key, string title, string symbol, int? value)
        => AddNumber(key, title, symbol, value is null ? (double?)null : value.Value);

    private void AddToggle(string key, string title, string symbol, bool value)
    {
        var toggle = new ToggleSwitch { IsOn = value, OnContent = "", OffContent = "" };
        AutomationProperties.SetName(toggle, title);
        toggle.Toggled += OnFieldChanged;

        var row = NewRow(title, symbol).Row;
        row.Content = toggle;
        KindFields.Children.Add(row);
        _extraFields[key] = toggle;
    }

    /// <summary>
    /// Inbox items exist to become something else. This is the row that does it — the whole
    /// point of an inbox is that triage is one click, not a retype.
    /// </summary>
    private void AddConvertRow()
    {
        var choice = new ComboBox { MinWidth = 190 };
        foreach (var (kind, label) in new[]
        {
            (ObjectKind.Task, "Task"),
            (ObjectKind.Assignment, "Assignment"),
            (ObjectKind.Requirement, "Requirement"),
            (ObjectKind.Note, "Note"),
            (ObjectKind.Link, "Link"),
        })
        {
            choice.Items.Add(new ComboBoxItem { Content = $"Convert to {label}", Tag = kind });
        }
        AutomationProperties.SetName(choice, "Convert this to");
        choice.SelectionChanged += async (_, _) =>
        {
            if (_loading || choice.SelectedItem is not ComboBoxItem { Tag: ObjectKind kind }) return;
            await ConvertAsync(kind);
        };

        var row = NewRow("Turn this into", CampusSymbols.Move).Row;
        row.Subtitle = "Keeps the title, the subject and the date";
        row.Content = choice;
        KindFields.Children.Add(row);
    }

    private async Task ConvertAsync(ObjectKind kind)
    {
        if (_model is null) return;

        var raw = ReadBody(_model);
        _model.Kind = kind;
        _model.Payload = kind switch
        {
            ObjectKind.Task => new TaskPayload { Notes = raw },
            ObjectKind.Assignment => new AssignmentPayload { Instructions = raw },
            ObjectKind.Requirement => new RequirementPayload { Action = raw },
            ObjectKind.Note => new NotePayload { Body = raw },
            ObjectKind.Link => new LinkPayload { Description = raw },
            _ => _model.Payload,
        };
        if (_model.Status == ObjectStatus.None) _model.Status = ObjectStatus.NotStarted;

        await _workspace.Objects.SaveAsync(_model);
        Load();
        Flash("Converted");
    }

    // ---------------------------------------------------------------------- checklist

    private void BuildChecklist()
    {
        ChecklistItems.Children.Clear();

        if (_model?.Payload is not TaskPayload task)
        {
            ChecklistSection.Visibility = Visibility.Collapsed;
            return;
        }

        ChecklistSection.Visibility = Visibility.Visible;

        foreach (var step in task.Checklist.OrderBy(c => c.SortOrder))
        {
            var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(8, 2, 8, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var check = new CheckBox { Content = step.Text, IsChecked = step.Done, MinWidth = 0 };
            AutomationProperties.SetName(check, step.Text);
            var captured = step;
            check.Click += async (_, _) =>
            {
                captured.Done = check.IsChecked == true;
                await SaveAsync();
            };
            row.Children.Add(check);

            var remove = new Button
            {
                Style = (Style)Application.Current.Resources["Button.Icon"],
                Content = new CampusIcon
                {
                    Symbol = CampusSymbols.Close,
                    IconSize = 14,
                    Foreground = (Brush)Application.Current.Resources[ThemeTokens.Label.Tertiary],
                },
            };
            AutomationProperties.SetName(remove, $"Remove {step.Text}");
            remove.Click += async (_, _) =>
            {
                task.Checklist.Remove(captured);
                BuildChecklist();
                await SaveAsync();
            };
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);

            ChecklistItems.Children.Add(row);
        }
    }

    private async void OnAddChecklistClick(object sender, RoutedEventArgs e) => await AddChecklistItemAsync();

    private async void OnNewChecklistKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        await AddChecklistItemAsync();
    }

    private async Task AddChecklistItemAsync()
    {
        if (_model?.Payload is not TaskPayload task) return;

        var text = NewChecklistBox.Text.Trim();
        if (text.Length == 0) return;

        task.Checklist.Add(new ChecklistItem { Text = text, SortOrder = task.Checklist.Count });
        NewChecklistBox.Text = string.Empty;
        BuildChecklist();
        await SaveAsync();
        NewChecklistBox.Focus(FocusState.Programmatic);
    }

    // --------------------------------------------------------------------------- tags

    private void BuildTags()
    {
        TagList.Children.Clear();
        if (_model is null) return;

        foreach (var tag in _model.Tags.Order(StringComparer.OrdinalIgnoreCase))
        {
            var captured = tag;
            var chip = new Button
            {
                Style = (Style)Application.Current.Resources["Button.Secondary"],
                MinWidth = 0,
                Padding = new Thickness(10, 3, 8, 3),
                CornerRadius = new CornerRadius(999),
            };

            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(new TextBlock { Text = "#" + tag, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new CampusIcon
            {
                Symbol = CampusSymbols.Close,
                IconSize = 12,
                Foreground = (Brush)Application.Current.Resources[ThemeTokens.Label.Tertiary],
            });
            chip.Content = content;

            AutomationProperties.SetName(chip, $"Remove tag {tag}");
            chip.Click += async (_, _) =>
            {
                _model.Tags.Remove(captured);
                BuildTags();
                await SaveAsync();
            };

            TagList.Children.Add(chip);
        }
    }

    private async void OnAddTagClick(object sender, RoutedEventArgs e) => await AddTagAsync();

    private async void OnNewTagKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        await AddTagAsync();
    }

    private async Task AddTagAsync()
    {
        if (_model is null) return;

        var tag = NewTagBox.Text.Trim().TrimStart('#');
        if (tag.Length == 0) return;
        if (_model.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
        {
            NewTagBox.Text = string.Empty;
            return;
        }

        _model.Tags.Add(tag);
        NewTagBox.Text = string.Empty;
        BuildTags();
        await SaveAsync();
        NewTagBox.Focus(FocusState.Programmatic);
    }

    // ------------------------------------------------------------------------ history

    private async Task LoadHistoryAsync()
    {
        HistoryList.Children.Clear();
        if (_model is null) return;

        await using var command = _workspace.Database.CreateCommand("""
            SELECT operation, at, device_id FROM journal
            WHERE entity_id = @id ORDER BY seq DESC LIMIT 8;
            """);
        command.Parameters.AddWithValue("@id", _model.Id.Value);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var operation = (ChangeOperation)reader.GetInt32(0);
            var at = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1));
            var device = reader.IsDBNull(2) ? null : reader.GetString(2);

            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            line.Children.Add(new TextBlock
            {
                Text = Describe(operation),
                FontSize = 13,
                Foreground = (Brush)Application.Current.Resources[ThemeTokens.Label.Primary],
            });

            var when = new TextBlock
            {
                Text = at.ToLocalTime().ToString("d MMM, HH:mm"),
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources[ThemeTokens.Label.Tertiary],
            };
            // The device only matters once there is more than one, so it is only shown then.
            if (device is not null && device != _workspace.DeviceId)
                when.Text += " · another device";

            Grid.SetColumn(when, 1);
            line.Children.Add(when);

            HistoryList.Children.Add(line);
        }
    }

    private static string Describe(ChangeOperation operation) => operation switch
    {
        ChangeOperation.Create => "Created",
        ChangeOperation.Update => "Edited",
        ChangeOperation.Trash => "Moved to trash",
        ChangeOperation.Restore => "Restored",
        ChangeOperation.Delete => "Deleted",
        ChangeOperation.Import => "Imported",
        _ => operation.ToString(),
    };

    // ------------------------------------------------------------------------- saving

    private void OnFieldChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SavedHint.Text = "Saving…";
        _saveTimer.Start();     // restarting the timer is what turns a burst of keystrokes into one write
    }

    private void OnDueChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        if (_loading) return;
        ClearDueButton.Visibility = sender.Date is null ? Visibility.Collapsed : Visibility.Visible;
        OnFieldChanged(sender, null!);
    }

    private void OnClearDueClick(object sender, RoutedEventArgs e)
    {
        DuePicker.Date = null;
        ClearDueButton.Visibility = Visibility.Collapsed;
        OnFieldChanged(sender, e);
    }

    private async Task SaveAsync()
    {
        if (_model is null || _loading || !_workspace.IsUnlocked) return;

        _model.Title = TitleBox.Text.Trim();

        _model.Status = StatusChoice.SelectedIndex switch
        {
            1 => ObjectStatus.InProgress,
            2 => ObjectStatus.Completed,
            3 => ObjectStatus.Blocked,
            4 => ObjectStatus.Waiting,
            _ => ObjectStatus.NotStarted,
        };
        _model.CompletedAt = _model.Status == ObjectStatus.Completed
            ? _model.CompletedAt ?? DateTimeOffset.UtcNow
            : null;

        _model.Priority = (Priority)Math.Clamp(PriorityChoice.SelectedIndex, 0, 4);
        _model.SubjectId = (SubjectChoice.SelectedItem as ComboBoxItem)?.Tag as CampusId?;
        _model.DueAt = DuePicker.Date;

        WriteBody(_model, BodyBox.Text);
        WriteKindFields(_model);

        await _workspace.Objects.SaveAsync(_model);

        UpdateSubtitle();
        Flash("Saved");
        await LoadHistoryAsync();
    }

    private void Flash(string message)
    {
        SavedHint.Text = message;
        // Cleared after a moment so the toolbar does not permanently read "Saved".
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(2);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => SavedHint.Text = string.Empty;
        timer.Start();
    }

    private string? Text(string key)
    {
        var value = (_extraFields.GetValueOrDefault(key) as TextBox)?.Text?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private double? Number(string key)
    {
        var value = (_extraFields.GetValueOrDefault(key) as NumberBox)?.Value;
        return value is null || double.IsNaN(value.Value) ? null : value.Value;
    }

    private bool Toggle(string key)
        => (_extraFields.GetValueOrDefault(key) as ToggleSwitch)?.IsOn ?? false;

    private void WriteKindFields(CampusObject model)
    {
        switch (model.Payload)
        {
            case AssignmentPayload assignment:
                assignment.Teacher = Text("teacher");
                assignment.Points = Number("points");
                assignment.EarnedPoints = Number("earned");
                assignment.Instructions = Text("instructions");
                assignment.Submitted = model.Status == ObjectStatus.Completed;
                break;

            case RequirementPayload requirement:
                requirement.Action = Text("action");
                requirement.Teacher = Text("teacher");
                requirement.RequiresPrinting = Toggle("printing");
                requirement.Ready = model.Status == ObjectStatus.Completed;
                break;

            case BookPayload book:
                book.Author = Text("author");
                book.Edition = Text("edition");
                book.CurrentPage = (int?)Number("currentPage");
                book.TotalPages = (int?)Number("totalPages");
                book.IsSolutionBook = Toggle("solution");
                break;

            case LinkPayload link:
                link.Url = Text("url") ?? string.Empty;
                link.Description = Text("description");
                link.Domain = Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) ? uri.Host : null;
                break;

            case PrintJobPayload print:
                print.Pages = (int)(Number("pages") ?? 0);
                print.Copies = Math.Max(1, (int)(Number("copies") ?? 1));
                print.DoubleSided = Toggle("duplex");
                print.ColorMode = Toggle("colour") ? PrintColorMode.Color : PrintColorMode.BlackAndWhite;
                break;

            case ExamPayload exam:
                exam.Scope = Text("scope");
                exam.Location = Text("location");
                exam.MaxScore = Number("maxScore");
                exam.Score = Number("score");
                exam.ScheduledAt = model.DueAt;
                break;
        }
    }

    /// <summary>Where the free text lives differs by kind, so reading and writing it is one place.</summary>
    private static string ReadBody(CampusObject model) => model.Payload switch
    {
        NotePayload note => note.Body,
        TaskPayload task => task.Notes ?? string.Empty,
        LessonPayload lesson => lesson.Body ?? string.Empty,
        ThreadPayload thread => thread.Body ?? string.Empty,
        InboxPayload inbox => inbox.RawText ?? string.Empty,
        PrintJobPayload print => print.Notes ?? string.Empty,
        GoalPayload goal => goal.Detail ?? string.Empty,
        _ => model.Summary ?? string.Empty,
    };

    private static void WriteBody(CampusObject model, string text)
    {
        var value = text.Trim();
        switch (model.Payload)
        {
            case NotePayload note: note.Body = value; break;
            case TaskPayload task: task.Notes = Empty(value); break;
            case LessonPayload lesson: lesson.Body = Empty(value); break;
            case ThreadPayload thread: thread.Body = Empty(value); break;
            case InboxPayload inbox: inbox.RawText = Empty(value); break;
            case PrintJobPayload print: print.Notes = Empty(value); break;
            case GoalPayload goal: goal.Detail = Empty(value); break;
            default: model.Summary = Empty(value); break;
        }

        static string? Empty(string value) => value.Length == 0 ? null : value;
    }

    // ------------------------------------------------------------------------ actions

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (Frame?.CanGoBack == true) Frame.GoBack();
    }

    private async void OnFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (_model is null) return;
        _model.IsFavorite = !_model.IsFavorite;
        UpdateFlagIcons();
        await _workspace.Objects.SetFlagAsync(_model.Id, "is_favorite", _model.IsFavorite);
    }

    private async void OnPinClick(object sender, RoutedEventArgs e)
    {
        if (_model is null) return;
        _model.IsPinned = !_model.IsPinned;
        UpdateFlagIcons();
        await _workspace.Objects.SetFlagAsync(_model.Id, "is_pinned", _model.IsPinned);
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_model is null) return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Move to trash?",
            Content = $"“{_model.Title}” goes to the trash. Nothing is destroyed until the trash "
                    + "is emptied.",
            PrimaryButtonText = "Move to trash",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _loading = true;   // stop the debounce from writing the object back after it is trashed
        _saveTimer.Stop();
        await _workspace.Objects.TrashAsync(_model.Id);

        if (Frame?.CanGoBack == true) Frame.GoBack();
    }

    private static string Humanise(ObjectKind kind) => kind switch
    {
        ObjectKind.InboxItem => "Inbox item",
        ObjectKind.PrintJob => "Print job",
        _ => kind.ToString(),
    };
}
